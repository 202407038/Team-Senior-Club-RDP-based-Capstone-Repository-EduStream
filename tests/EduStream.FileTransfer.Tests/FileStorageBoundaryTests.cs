using EduStream.Client.Services;
using EduStream.Core.FileSharing;
using EduStream.Core.Collaboration;
using EduStream.Server.Services;
using System.Security.AccessControl;
using System.Security.Principal;

namespace EduStream.FileTransfer.Tests;

public sealed class FileStorageBoundaryTests
{
    [Fact]
    public async Task DeniedDirectoryWrite_UsesActualAcl_FailsWithoutSavingAndRecovers()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var source = await fixture.SourceAsync(500);
        var file = await catalog.RegisterAsync(source);
        var downloader = new SessionFileDownloader(new ReadinessDirectory(fixture.Downloads));
        var directory = Directory.CreateDirectory(fixture.Downloads);
        var original = directory.GetAccessControl();
        var denied = directory.GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        denied.AddAccessRule(new FileSystemAccessRule(user,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(denied);
            var request = ReadinessFiles.Request(file);
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                downloader.SaveAsync(file, request, catalog.DownloadAsync(fixture.Context, request)));
            Assert.Equal("FILE_ACCESS_DENIED", CollaborationErrorCatalog.FromException(error).Code);
            Assert.Empty(Directory.GetFiles(fixture.Downloads));
        }
        finally
        {
            // 읽기만 한 DirectorySecurity는 변경 플래그가 없어 SetAccessControl이 복원을 생략할 수 있습니다.
            denied.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            directory.SetAccessControl(denied);
        }
        var retry = ReadinessFiles.Request(file);
        var receipt = await downloader.SaveAsync(file, retry, catalog.DownloadAsync(fixture.Context, retry));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(receipt.LocalPath));
        fixture.AssertNoPartials();
    }

    [Fact]
    public async Task LockedSource_FailsWithoutSavingAndReleasesTransferSlots()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var source = await fixture.SourceAsync(500);
        var file = await catalog.RegisterAsync(source);
        var downloader = new SessionFileDownloader(new ReadinessDirectory(fixture.Downloads));
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            for (var i = 0; i <= SessionFileLimits.MaxConcurrentTransfers; i++)
            {
                var request = ReadinessFiles.Request(file);
                await Assert.ThrowsAsync<IOException>(() => downloader.SaveAsync(file, request,
                    catalog.DownloadAsync(fixture.Context, request)));
                fixture.AssertNoPartials();
                Assert.Empty(Directory.GetFiles(fixture.Downloads));
            }
        }
        var retry = ReadinessFiles.Request(file);
        var receipt = await downloader.SaveAsync(file, retry, catalog.DownloadAsync(fixture.Context, retry));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(receipt.LocalPath));
    }

    [Fact]
    public async Task ExistingFileAndDirectoryNames_ArePreservedWhenChoosingDownloadName()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var source = await fixture.SourceAsync(500, "강의.txt");
        var file = await catalog.RegisterAsync(source);
        Directory.CreateDirectory(Path.Combine(fixture.Downloads, "강의.txt"));
        var existing = Path.Combine(fixture.Downloads, "강의 (1).txt");
        await File.WriteAllTextAsync(existing, "keep");
        using var locked = new FileStream(existing, FileMode.Open, FileAccess.Read, FileShare.Read);
        var request = ReadinessFiles.Request(file);
        var receipt = await new SessionFileDownloader(new ReadinessDirectory(fixture.Downloads))
            .SaveAsync(file, request, catalog.DownloadAsync(fixture.Context, request));
        Assert.Equal("강의 (2).txt", Path.GetFileName(receipt.LocalPath));
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
        Assert.True(Directory.Exists(Path.Combine(fixture.Downloads, "강의.txt")));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(receipt.LocalPath));
        fixture.AssertNoPartials();
    }

    [Fact]
    public async Task CancellationAfterCommittedReceipt_DoesNotDeleteStoredFile()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var source = await fixture.SourceAsync(500);
        var file = await catalog.RegisterAsync(source);
        var request = ReadinessFiles.Request(file);
        using var cancellation = new CancellationTokenSource();
        var receipt = await new SessionFileDownloader(new ReadinessDirectory(fixture.Downloads))
            .SaveAsync(file, request, catalog.DownloadAsync(fixture.Context, request),
                cancellationToken: cancellation.Token);
        cancellation.Cancel();
        Assert.True(catalog.Unregister(file.FileId));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(receipt.LocalPath));
        fixture.AssertNoPartials();
    }

    [Theory]
    [InlineData(".")]
    [InlineData("downloads")]
    [InlineData("\\downloads")]
    public async Task NonAbsoluteDownloadsPath_FailsBeforeReadingChunksAndCanRetry(string invalidPath)
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var source = await fixture.SourceAsync(500);
        var file = await catalog.RegisterAsync(source);
        var directory = new MutableDirectory(invalidPath);
        var downloader = new SessionFileDownloader(directory);
        var request = ReadinessFiles.Request(file);
        var enumerated = false;
        async IAsyncEnumerable<SessionFileChunk> Chunks()
        {
            enumerated = true;
            if (!Path.IsPathFullyQualified(directory.Path))
                throw new InvalidOperationException("저장 경로 검사보다 청크 열거가 먼저 실행되었습니다.");
            await foreach (var chunk in catalog.DownloadAsync(fixture.Context, request)) yield return chunk;
        }
        await Assert.ThrowsAsync<IOException>(() => downloader.SaveAsync(file, request, Chunks()));
        Assert.False(enumerated);
        directory.Path = fixture.Downloads;
        var receipt = await downloader.SaveAsync(file, request, Chunks());
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(receipt.LocalPath));
        fixture.AssertNoPartials();
    }

    private sealed class MutableDirectory(string path) : IDownloadsDirectory
    {
        public string Path { get; set; } = path;
        public string GetPath() => Path;
    }
}
