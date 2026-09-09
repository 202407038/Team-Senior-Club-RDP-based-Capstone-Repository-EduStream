using EduStream.Client.Services;
using EduStream.Core.Models;
using EduStream.Core.Utils;

namespace EduStream.FileTransfer.Tests;

public sealed class SeptemberWeek1FileRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EduStream-September", Guid.NewGuid().ToString("N"));

    public SeptemberWeek1FileRecoveryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task OversizedChunks_FailBeforeAssembly_ThenSameTransferCanRetry()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var packets = AugustFileTestSupport.CreateTwoPackets(content, "sample.bin", Guid.NewGuid(), ChecksumUtility.ComputeSha256(content));
        var receiver = new FileReceiver();
        Assert.True((await receiver.TrySaveAsync(packets[0], _directory)).Pending);
        packets[1].Content = new byte[] { 3, 4, 5 };
        packets[1].DataLength = 3;
        var failure = await receiver.TrySaveAsync(packets[1], _directory);
        Assert.Equal("FILE_ASSEMBLY_FAILED", failure.ErrorCode);
        Assert.True(failure.CanRetry);
        Assert.Empty(Directory.GetFiles(_directory));
        packets[1].Content = new byte[] { 3, 4 };
        packets[1].DataLength = 2;
        Assert.True((await receiver.TrySaveAsync(packets[0], _directory)).Pending);
        Assert.True((await receiver.TrySaveAsync(packets[1], _directory)).Success);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_directory, "sample.bin")));
    }

    [Fact]
    public async Task LockedDestination_PreservesOriginal_RemovesTemporaryFile_AndAllowsRetry()
    {
        var path = Path.Combine(_directory, "locked.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 9 });
        var packet = new FilePacket
        {
            FileName = "locked.bin", FileSize = 2, Content = new byte[] { 1, 2 }, DataLength = 2,
            Checksum = ChecksumUtility.ComputeSha256(new byte[] { 1, 2 })
        };
        var receiver = new FileReceiver();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = await receiver.TrySaveAsync(packet, _directory);
            Assert.False(failure.Success);
            Assert.Contains(failure.ErrorCode, new[] { "FILE_IO_ERROR", "FILE_WRITE_PERMISSION_DENIED" });
        }
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
        Assert.True((await receiver.TrySaveAsync(packet, _directory)).Success);
        Assert.Equal(packet.Content, await File.ReadAllBytesAsync(path));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
