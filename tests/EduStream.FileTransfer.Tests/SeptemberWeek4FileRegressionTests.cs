using EduStream.Client.Services;
using EduStream.Core.Models;
using EduStream.Core.Utils;

namespace EduStream.FileTransfer.Tests;

public sealed class SeptemberWeek4FileRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EduStream-September", Guid.NewGuid().ToString("N"));
    public SeptemberWeek4FileRegressionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task OutOfOrderAndDuplicateChunks_KeepUniqueProgressAndContents()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6 };
        var id = Guid.NewGuid();
        var checksum = ChecksumUtility.ComputeSha256(data);
        FilePacket Packet(int index) => AugustFileTestSupport.CreatePacket(
            data[(index * 2)..(index * 2 + 2)], "order.bin", id, index, 3, checksum, data.Length);
        var receiver = new FileReceiver();
        var first = await receiver.TrySaveAsync(Packet(2), _directory);
        var duplicate = await receiver.TrySaveAsync(Packet(2), _directory);
        Assert.Equal(first.ProgressPercent, duplicate.ProgressPercent);
        Assert.True(duplicate.Pending);
        Assert.True((await receiver.TrySaveAsync(Packet(0), _directory)).Pending);
        var final = await receiver.TrySaveAsync(Packet(1), _directory);
        Assert.True(final.Success);
        Assert.Equal(100, final.ProgressPercent);
        Assert.Equal(data, await File.ReadAllBytesAsync(final.FilePath!));
    }

    [Fact]
    public async Task ChecksumFailure_PreservesExistingFile_ThenGoodTransferReplacesIt()
    {
        var path = Path.Combine(_directory, "retry.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 9 });
        var data = new byte[] { 1, 2, 3, 4 };
        var receiver = new FileReceiver();
        var id = Guid.NewGuid();
        var corrupt = AugustFileTestSupport.CreateTwoPackets(data, "retry.bin", id, new string('0', 64));
        await receiver.TrySaveAsync(corrupt[0], _directory);
        Assert.False((await receiver.TrySaveAsync(corrupt[1], _directory)).Success);
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(path));
        var good = AugustFileTestSupport.CreateTwoPackets(data, "retry.bin", id, ChecksumUtility.ComputeSha256(data));
        await receiver.TrySaveAsync(good[0], _directory);
        Assert.True((await receiver.TrySaveAsync(good[1], _directory)).Success);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
