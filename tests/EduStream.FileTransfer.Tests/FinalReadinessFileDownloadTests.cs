using System.Runtime.CompilerServices;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class FinalReadinessFileDownloadTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(8 * 1024 * 1024)]
    public async Task RequestDownload_StreamsToDiskWithExactBytes(int size)
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var source = await fixture.SourceAsync(size);
        var file = await catalog.RegisterAsync(source);
        var request = ReadinessFiles.Request(file);
        var receiver = new SessionFileDownloader(new ReadinessDirectory(fixture.Downloads));
        var receipt = await receiver.SaveAsync(file, request, catalog.DownloadAsync(fixture.Context, request));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(receipt.LocalPath));
        Assert.Equal(size, receipt.Length);
        Assert.Equal(file.Sha256, receipt.Sha256);
        Assert.Equal(request.RequestId, receipt.RequestId);
        fixture.AssertNoPartials();
    }

    [Fact]
    public async Task ConcurrentSameNameDownloads_NeverOverwriteExistingFile()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var file = await catalog.RegisterAsync(await fixture.SourceAsync(200000));
        Directory.CreateDirectory(fixture.Downloads);
        var original = Path.Combine(fixture.Downloads, file.FileName);
        await File.WriteAllTextAsync(original, "원래 파일");
        var receiver = new SessionFileDownloader(new ReadinessDirectory(fixture.Downloads));
        var requests = Enumerable.Range(0, 3).Select(_ => ReadinessFiles.Request(file)).ToArray();
        var receipts = await Task.WhenAll(requests.Select(r =>
            receiver.SaveAsync(file, r, catalog.DownloadAsync(fixture.Context, r))));
        Assert.Equal(3, receipts.Select(x => x.LocalPath).Distinct().Count());
        Assert.Equal("원래 파일", await File.ReadAllTextAsync(original));
        Assert.All(receipts, x => Assert.NotEqual(original, x.LocalPath));
        fixture.AssertNoPartials();
    }

    [Fact]
    public async Task FileContracts_SerializeBetweenCatalogAndDownloader()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var source = await fixture.SourceAsync(200000);
        var file = await catalog.RegisterAsync(source);
        var request = ReadinessFiles.Request(file);
        request = CollaborationMessageCodec.Decode<SessionFileRequest>(
            CollaborationMessageCodec.Encode(Guid.NewGuid(), request), out _);
        var receiver = new SessionFileDownloader(new ReadinessDirectory(fixture.Downloads));
        var receipt = await receiver.SaveAsync(file, request, WireRoundTrip(catalog.DownloadAsync(fixture.Context, request)));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(receipt.LocalPath));
    }

    private static async IAsyncEnumerable<SessionFileChunk> WireRoundTrip(IAsyncEnumerable<SessionFileChunk> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in source.WithCancellation(cancellationToken))
            yield return CollaborationMessageCodec.Decode<SessionFileChunk>(
                CollaborationMessageCodec.Encode(Guid.NewGuid(), chunk), out _);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("truncated")]
    [InlineData("index")]
    [InlineData("request")]
    [InlineData("session")]
    [InlineData("file")]
    [InlineData("revision")]
    [InlineData("oversize")]
    [InlineData("duplicate")]
    public async Task InvalidStream_IsRejectedAndPartialRemoved(string fault)
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var file = await catalog.RegisterAsync(await fixture.SourceAsync(200000));
        var request = ReadinessFiles.Request(file);
        var receiver = new SessionFileDownloader(new ReadinessDirectory(fixture.Downloads));
        await Assert.ThrowsAsync<CollaborationException>(() =>
            receiver.SaveAsync(file, request, Corrupt(catalog.DownloadAsync(fixture.Context, request), fault)));
        fixture.AssertNoPartials();
        Assert.Empty(Directory.GetFiles(fixture.Downloads));
        // 같은 수신기를 재사용해도 실패 상태가 남지 않아야 합니다.
        var retry = ReadinessFiles.Request(file);
        Assert.True(File.Exists((await receiver.SaveAsync(file, retry,
            catalog.DownloadAsync(fixture.Context, retry))).LocalPath));
    }

    [Fact]
    public async Task UnregisterDuringReceive_RemovesPartialButKeepsPreviousDownloadAndSource()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var source = await fixture.SourceAsync(200000);
        var file = await catalog.RegisterAsync(source);
        var receiver = new SessionFileDownloader(new ReadinessDirectory(fixture.Downloads));
        var completed = ReadinessFiles.Request(file);
        var receipt = await receiver.SaveAsync(file, completed, catalog.DownloadAsync(fixture.Context, completed));
        var request = ReadinessFiles.Request(file);
        await Assert.ThrowsAsync<CollaborationException>(() => receiver.SaveAsync(file, request,
            AfterFirst(catalog.DownloadAsync(fixture.Context, request), () => catalog.Unregister(file.FileId))));
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(receipt.LocalPath));
        Assert.Single(Directory.GetFiles(fixture.Downloads));
        fixture.AssertNoPartials();
    }

    [Fact]
    public async Task CancellationDuringReceive_RemovesPartialAndDoesNotCommit()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var file = await catalog.RegisterAsync(await fixture.SourceAsync(200000));
        var request = ReadinessFiles.Request(file);
        using var cancellation = new CancellationTokenSource();
        var receiver = new SessionFileDownloader(new ReadinessDirectory(fixture.Downloads));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.SaveAsync(file, request,
            AfterFirst(catalog.DownloadAsync(fixture.Context, request), cancellation.Cancel),
            cancellationToken: cancellation.Token));
        fixture.AssertNoPartials();
        Assert.Empty(Directory.GetFiles(fixture.Downloads));
    }

    [Fact]
    public async Task InvalidDownloadDirectory_ReturnsIoFailureWithoutTouchingSource()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var source = await fixture.SourceAsync(10);
        var file = await catalog.RegisterAsync(source);
        var request = ReadinessFiles.Request(file);
        var receiver = new SessionFileDownloader(new ReadinessDirectory(source)); // 파일을 디렉터리로 지정
        await Assert.ThrowsAnyAsync<IOException>(() =>
            receiver.SaveAsync(file, request, catalog.DownloadAsync(fixture.Context, request)));
        Assert.Equal(10, new FileInfo(source).Length);
    }

    [Fact]
    public void WindowsDownloadsFolder_ResolvesAbsoluteCurrentUserPath()
    {
        var path = new WindowsDownloadsDirectory().GetPath();
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.False(string.IsNullOrWhiteSpace(path));
    }

    private static async IAsyncEnumerable<SessionFileChunk> AfterFirst(
        IAsyncEnumerable<SessionFileChunk> source, Action action,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var first = true;
        await foreach (var chunk in source.WithCancellation(cancellationToken))
        {
            yield return chunk;
            if (first) { first = false; action(); }
        }
    }

    private static async IAsyncEnumerable<SessionFileChunk> Corrupt(
        IAsyncEnumerable<SessionFileChunk> source, string fault,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var first = true;
        await foreach (var original in source.WithCancellation(cancellationToken))
        {
            var chunk = original;
            if (first)
            {
                if (fault == "truncated") yield break;
                if (fault == "index") chunk = chunk with { Index = 1 };
                if (fault == "request") chunk = chunk with { RequestId = Guid.NewGuid() };
                if (fault == "session") chunk = chunk with { SessionId = Guid.NewGuid() };
                if (fault == "file") chunk = chunk with { FileId = Guid.NewGuid() };
                if (fault == "revision") chunk = chunk with { Revision = 99 };
                if (fault == "oversize") chunk = chunk with { Content = new byte[1024 * 1024] };
                if (fault == "checksum")
                {
                    var bytes = chunk.Content.ToArray();
                    bytes[0] ^= 0xff;
                    chunk = chunk with { Content = bytes };
                }
                if (fault == "duplicate") yield return chunk;
                first = false;
            }
            yield return chunk;
        }
    }
}
