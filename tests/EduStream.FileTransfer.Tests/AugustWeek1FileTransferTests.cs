using System.Text;
using EduStream.Client.Services;
using EduStream.Core.Logging;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Core.Utils;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class AugustWeek1FileStateTests
{
    [Fact]
    public async Task EmptyFile_ShouldRoundTripWithChecksumValidation()
    {
        var sourcePath = AugustFileTestSupport.CreateFile("empty.bin", []);
        var targetDirectory = AugustFileTestSupport.CreateDirectory();
        var distributor = new FileDistributor(new PacketSerializer(), new InMemoryLogSink());
        var receiver = new FileReceiver();

        var packets = await distributor.BuildFilePacketsAsync(sourcePath, FileTransferRules.MinChunkSize);
        var result = await receiver.TrySaveAsync(packets.Single(), targetDirectory);

        Assert.True(result.Success);
        Assert.Equal(100, result.ProgressPercent);
        Assert.False(result.CanRetry);
        Assert.NotNull(result.FilePath);
        Assert.Empty(await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task PositiveFileSizeWithEmptyPayload_ShouldFailWithoutWriting()
    {
        var targetDirectory = AugustFileTestSupport.CreateDirectory();
        var packet = AugustFileTestSupport.CreatePacket(
            content: [],
            fileName: "declared-nonempty.bin",
            transferId: Guid.NewGuid(),
            chunkIndex: 0,
            totalChunks: 1,
            checksum: ChecksumUtility.ComputeSha256([]),
            fileSize: 10);

        var result = await new FileReceiver().TrySaveAsync(packet, targetDirectory);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.EmptyChunkPayload, result.ErrorCode);
        Assert.True(result.CanRetry);
        Assert.Empty(Directory.GetFiles(targetDirectory));
    }

    [Fact]
    public async Task EmptyFileWithWrongChecksum_ShouldRemainRetryableFailure()
    {
        var targetDirectory = AugustFileTestSupport.CreateDirectory();
        var packet = AugustFileTestSupport.CreatePacket(
            content: [],
            fileName: "empty.bin",
            transferId: Guid.NewGuid(),
            chunkIndex: 0,
            totalChunks: 1,
            checksum: ChecksumUtility.ComputeSha256([1]),
            fileSize: 0);

        var result = await new FileReceiver().TrySaveAsync(packet, targetDirectory);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.ChecksumMismatch, result.ErrorCode);
        Assert.True(result.CanRetry);
        Assert.True(result.ToOperationResult().IsRecoverable);
        Assert.Empty(Directory.GetFiles(targetDirectory));
    }

    [Fact]
    public async Task UnsafeFileName_ShouldFailBeforeLeavingTargetDirectory()
    {
        var targetDirectory = AugustFileTestSupport.CreateDirectory();
        var content = Encoding.UTF8.GetBytes("unsafe-name");
        var packet = AugustFileTestSupport.CreatePacket(
            content,
            "..\\outside.txt",
            Guid.NewGuid(),
            0,
            1,
            ChecksumUtility.ComputeSha256(content),
            content.Length);

        var result = await new FileReceiver().TrySaveAsync(packet, targetDirectory);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidFileName, result.ErrorCode);
        Assert.Empty(Directory.GetFiles(targetDirectory));
    }
}
