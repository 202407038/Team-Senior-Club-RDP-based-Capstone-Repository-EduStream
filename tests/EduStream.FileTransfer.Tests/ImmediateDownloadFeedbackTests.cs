using System.Reflection;
using EduStream.Client.Services;
using EduStream.Client.ViewModels;
using EduStream.Core.Models;
using EduStream.Core.Utils;

namespace EduStream.FileTransfer.Tests;

public sealed class ImmediateDownloadFeedbackTests
{
    private static Task Receive(ClientViewModel vm, FilePacket packet) =>
        (Task)typeof(ClientViewModel).GetMethod("HandleFileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [packet])!;
    private static FilePacket Packet(byte[] bytes) => AugustFileTestSupport.CreatePacket(bytes, "강의.txt",
        Guid.NewGuid(), 0, 1, ChecksumUtility.ComputeSha256(bytes), bytes.Length);

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public async Task ImmediateReceiveUsesDownloadsAndPreservesExistingFile(int size)
    {
        using var files = new ReadinessFiles();
        Directory.CreateDirectory(files.Downloads);
        var existing = Path.Combine(files.Downloads, "강의.txt");
        await File.WriteAllTextAsync(existing, "original");
        var bytes = AugustFileTestSupport.CreateContent(size);
        var vm = new ClientViewModel(downloadsDirectory: new ReadinessDirectory(files.Downloads));
        try
        {
            await Receive(vm, Packet(bytes));
            Assert.Equal("original", await File.ReadAllTextAsync(existing));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(files.Downloads, "강의 (1).txt")));
            Assert.Equal("강의 (1).txt", Assert.Single(vm.DownloadedFiles));
            Assert.Contains("다운로드 폴더", vm.DownloadStatus);
            Assert.False(vm.IsStatusError);
            files.AssertNoPartials();
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task ChunkedReceiveWritesOnlyAfterFinalChunk()
    {
        using var files = new ReadinessFiles();
        var bytes = AugustFileTestSupport.CreateContent(128);
        var packets = AugustFileTestSupport.CreateTwoPackets(bytes, "강의.txt", Guid.NewGuid(), ChecksumUtility.ComputeSha256(bytes));
        var vm = new ClientViewModel(downloadsDirectory: new ReadinessDirectory(files.Downloads));
        try
        {
            await Receive(vm, packets[0]);
            Assert.Empty(vm.DownloadedFiles);
            Assert.False(File.Exists(Path.Combine(files.Downloads, "강의.txt")));
            await Receive(vm, packets[1]);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(files.Downloads, "강의.txt")));
            Assert.Single(vm.DownloadedFiles);
            files.AssertNoPartials();
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task InvalidDownloadsPathReportsFailureWithoutTempFallback()
    {
        var vm = new ClientViewModel(downloadsDirectory: new ReadinessDirectory("relative-downloads"));
        try
        {
            await Receive(vm, Packet([1, 2]));
            Assert.True(vm.IsStatusError);
            Assert.Empty(vm.DownloadedFiles);
            Assert.Contains("파일 저장 실패", vm.DownloadStatus);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task ConcurrentSameNameReceivesDoNotOverwrite()
    {
        using var files = new ReadinessFiles();
        var results = await Task.WhenAll(Enumerable.Range(1, 3).Select(index =>
            new FileReceiver().TrySaveAsync(Packet([(byte)index]), files.Downloads, overwrite: false)));
        Assert.All(results, result => Assert.True(result.Success, result.ErrorMessage));
        Assert.Equal(3, results.Select(result => result.FilePath).Distinct().Count());
        var bytes = await Task.WhenAll(results.Select(result => File.ReadAllBytesAsync(result.FilePath!)));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes.Select(data => data.Single()).Order().ToArray());
        files.AssertNoPartials();
    }
}
