using System.Runtime.CompilerServices;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Core.Protocols;

namespace EduStream.FileTransfer.Tests;

/// <summary>4번 저장기/카탈로그를 #59 실제 루프백 TLS/TCP 라우팅으로 검증합니다.</summary>
public sealed partial class SecureFileRoutingWiringTests
{
    [Fact]
    public async Task RepeatedDownload_PreservesExistingFileAndAcknowledgesBoth()
    {
        await using var rig = await Rig.OpenAsync();
        var source = await rig.WriteSourceAsync(FileTransferRules.DefaultChunkSize + 17);
        var file = await rig.SessionManager.RegisterFileAsync(source);
        var alice = await rig.JoinAsync("Alice");
        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Count == 1);
        var stored = 0;
        rig.SessionManager.FileTransfers!.FileStored += (_, _) => Interlocked.Increment(ref stored);
        var first = await alice.Files.DownloadAsync(file.FileId).WaitAsync(Wait);
        var second = await alice.Files.DownloadAsync(file.FileId).WaitAsync(Wait);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.NotEqual(first.LocalPath, second.LocalPath);
        var expected = await File.ReadAllBytesAsync(source);
        Assert.Equal(expected, await File.ReadAllBytesAsync(first.LocalPath));
        Assert.Equal(expected, await File.ReadAllBytesAsync(second.LocalPath));
        await WaitUntilAsync(() => Volatile.Read(ref stored) == 2 && rig.SessionManager.FileTransfers.PendingTransferCount == 0);
    }

    [Fact]
    public async Task ChangedSource_FailsWithoutStoredAck_AndReregisterCanDownload()
    {
        await using var rig = await Rig.OpenAsync();
        var source = await rig.WriteSourceAsync(1000);
        var file = await rig.SessionManager.RegisterFileAsync(source);
        var alice = await rig.JoinAsync("Alice");
        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Count == 1);
        var stored = 0;
        rig.SessionManager.FileTransfers!.FileStored += (_, _) => Interlocked.Increment(ref stored);
        var bytes = await File.ReadAllBytesAsync(source);
        bytes[0] ^= 0xff; // 길이는 유지하고 해시만 바꿉니다.
        await File.WriteAllBytesAsync(source, bytes);
        var error = await Assert.ThrowsAsync<CollaborationException>(() => alice.Files.DownloadAsync(file.FileId).WaitAsync(Wait));
        Assert.Equal(CollaborationError.SourceChanged, error.Code);
        await WaitUntilAsync(() => rig.SessionManager.FileTransfers.PendingTransferCount == 0);
        Assert.Equal(0, Volatile.Read(ref stored));
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "downloads-Alice")));
        Assert.True(rig.SessionManager.UnregisterFile(file.FileId));
        var fresh = await rig.SessionManager.RegisterFileAsync(source);
        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Any(f => f.FileId == fresh.FileId) == true);
        var receipt = await alice.Files.DownloadAsync(fresh.FileId).WaitAsync(Wait);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(receipt.LocalPath));
        await WaitUntilAsync(() => Volatile.Read(ref stored) == 1);
    }

    [Fact]
    public async Task InvalidDownloadDirectory_CancelsServerRequest_ThenRetrySucceeds()
    {
        await using var rig = await Rig.OpenAsync();
        var source = await rig.WriteSourceAsync(FileTransferRules.DefaultChunkSize * 10);
        var file = await rig.SessionManager.RegisterFileAsync(source);
        var alice = await rig.JoinAsync("Alice");
        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Count == 1);
        var directory = Path.Combine(rig.Root, "downloads-Alice");
        await File.WriteAllTextAsync(directory, "this is a file, not a directory");
        var stored = 0;
        rig.SessionManager.FileTransfers!.FileStored += (_, _) => Interlocked.Increment(ref stored);
        await Assert.ThrowsAsync<IOException>(() => alice.Files.DownloadAsync(file.FileId).WaitAsync(Wait));
        await WaitUntilAsync(() => rig.SessionManager.FileTransfers.PendingTransferCount == 0);
        Assert.Equal(0, Volatile.Read(ref stored));
        File.Delete(directory);
        var receipt = await alice.Files.DownloadAsync(file.FileId).WaitAsync(Wait);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(receipt.LocalPath));
        await WaitUntilAsync(() => Volatile.Read(ref stored) == 1);
    }

    [Fact]
    public async Task CancelAfterFirstSavedChunk_RemovesPartialAndAllowsNewRequest()
    {
        await using var rig = await Rig.OpenAsync();
        var source = await rig.WriteSourceAsync(FileTransferRules.DefaultChunkSize * 12);
        var file = await rig.SessionManager.RegisterFileAsync(source);
        PausedDownloader? paused = null;
        var alice = await rig.JoinAsync("Alice", inner => paused = new PausedDownloader(inner));
        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Count == 1);
        var stored = 0;
        rig.SessionManager.FileTransfers!.FileStored += (_, _) => Interlocked.Increment(ref stored);
        using var cancellation = new CancellationTokenSource();
        var download = alice.Files.DownloadAsync(file.FileId, cancellationToken: cancellation.Token);
        
        // 수정 1: 10초 대기 완벽 적용
        await paused!.FirstSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        
        Assert.Single(Directory.GetFiles(Path.Combine(rig.Root, "downloads-Alice"), "*.partial"));
        cancellation.Cancel();
        
        // 수정 2: 10초 대기 완벽 적용
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(10)));
        
        paused.Resume.TrySetResult();
        await WaitUntilAsync(() => rig.SessionManager.FileTransfers.PendingTransferCount == 0);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "downloads-Alice")));
        Assert.Equal(0, Volatile.Read(ref stored));
        
        // 수정 3: 10초 대기 완벽 적용
        var receipt = await alice.Files.DownloadAsync(file.FileId).WaitAsync(TimeSpan.FromSeconds(10));
        
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(receipt.LocalPath));
        await WaitUntilAsync(() => Volatile.Read(ref stored) == 1);
    }

    [Fact]
    public async Task RemovedCatalogEntry_CannotStartAnotherDownload_ExistingCopyRemains()
    {
        await using var rig = await Rig.OpenAsync();
        var file = await rig.SessionManager.RegisterFileAsync(await rig.WriteSourceAsync(100));
        var alice = await rig.JoinAsync("Alice");
        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Count == 1);
        var receipt = await alice.Files.DownloadAsync(file.FileId).WaitAsync(Wait);
        await WaitUntilAsync(() => rig.SessionManager.FileTransfers!.PendingTransferCount == 0);
        Assert.True(rig.SessionManager.UnregisterFile(file.FileId));
        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Count == 0);
        var error = await Assert.ThrowsAsync<CollaborationException>(() => alice.Files.DownloadAsync(file.FileId));
        Assert.Equal(CollaborationError.FileUnavailable, error.Code);
        Assert.True(File.Exists(receipt.LocalPath));
        Assert.Equal(0, rig.SessionManager.FileTransfers!.PendingTransferCount);
    }

    private sealed class PausedDownloader(ISessionFileDownloader inner) : ISessionFileDownloader
    {
        public TaskCompletionSource FirstSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DownloadReceipt> SaveAsync(SessionFileDescriptor file, SessionFileRequest request,
            IAsyncEnumerable<SessionFileChunk> chunks, IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            inner.SaveAsync(file, request, Pause(chunks, cancellationToken), progress, cancellationToken);

        private async IAsyncEnumerable<SessionFileChunk> Pause(IAsyncEnumerable<SessionFileChunk> chunks,
            [EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var chunk in chunks.WithCancellation(token))
            {
                yield return chunk;
                // 실제 저장기가 청크를 기록한 뒤 다음 청크를 요청하는 시점에 멈춥니다.
                FirstSaved.TrySetResult();
                await Resume.Task.WaitAsync(token);
            }
        }
    }
}
