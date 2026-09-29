using System.Text;
using EduStream.Client.Services;
using EduStream.Core.Protocols;
using EduStream.Core.Utils;

namespace EduStream.FileTransfer.Tests;

public sealed class AugustWeek2FileRecoveryTests
{
    [Fact]
    public async Task InvalidPacket_ShouldClearSameTransferBufferBeforeRetry()
    {
        var receiver = new FileReceiver();
        var targetDirectory = AugustFileTestSupport.CreateDirectory();
        var content = Encoding.UTF8.GetBytes("buffer-cleanup-" + new string('R', 120));
        var checksum = ChecksumUtility.ComputeSha256(content);
        var transferId = Guid.NewGuid();
        var first = AugustFileTestSupport.CreatePacket(content[..60], "retry.bin", transferId, 0, 2, checksum, content.Length);
        var second = AugustFileTestSupport.CreatePacket(content[60..], "retry.bin", transferId, 1, 2, checksum, content.Length);
        var invalid = AugustFileTestSupport.CreatePacket(content[60..], "retry.bin", transferId, 2, 2, checksum, content.Length);

        Assert.True((await receiver.TrySaveAsync(first, targetDirectory)).Pending);

        var failure = await receiver.TrySaveAsync(invalid, targetDirectory);
        var retrySecond = await receiver.TrySaveAsync(second, targetDirectory);
        var retryCompleted = await receiver.TrySaveAsync(first, targetDirectory);

        Assert.Equal(ErrorCodes.InvalidChunkIndex, failure.ErrorCode);
        Assert.True(failure.CanRetry);
        Assert.True(retrySecond.Pending);
        Assert.Equal(1, retrySecond.ReceivedChunkCount);
        Assert.True(retryCompleted.Success);
        Assert.Equal(content, await File.ReadAllBytesAsync(retryCompleted.FilePath!));
    }

    [Fact]
    public async Task ChecksumFailure_ShouldAllowSameTransferToRestart()
    {
        var receiver = new FileReceiver();
        var targetDirectory = AugustFileTestSupport.CreateDirectory();
        var content = Encoding.UTF8.GetBytes("same-transfer-checksum-retry-" + new string('C', 96));
        var correctChecksum = ChecksumUtility.ComputeSha256(content);
        var wrongChecksum = ChecksumUtility.ComputeSha256(Encoding.UTF8.GetBytes("wrong"));
        var transferId = Guid.NewGuid();

        var wrongPackets = AugustFileTestSupport.CreateTwoPackets(content, "checksum-retry.bin", transferId, wrongChecksum);
        await receiver.TrySaveAsync(wrongPackets[0], targetDirectory);
        var failure = await receiver.TrySaveAsync(wrongPackets[1], targetDirectory);

        var retryPackets = AugustFileTestSupport.CreateTwoPackets(content, "checksum-retry.bin", transferId, correctChecksum);
        var retryPending = await receiver.TrySaveAsync(retryPackets[0], targetDirectory);
        var retrySuccess = await receiver.TrySaveAsync(retryPackets[1], targetDirectory);

        Assert.Equal(ErrorCodes.ChecksumMismatch, failure.ErrorCode);
        Assert.True(failure.CanRetry);
        Assert.True(retryPending.Pending);
        Assert.True(retrySuccess.Success);
        Assert.Equal(content, await File.ReadAllBytesAsync(retrySuccess.FilePath!));
    }
}
