using EduStream.Client.Services;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Core.Utils;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class AugustWeek3FileSizeMatrixTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(FileTransferRules.MinChunkSize)]
    [InlineData(FileTransferRules.MinChunkSize + 1)]
    [InlineData(256 * 1024 + 17)]
    public async Task FileSizes_ShouldRoundTripWithStableChecksum(int size)
    {
        var content = AugustFileTestSupport.CreateContent(size);
        var sourcePath = AugustFileTestSupport.CreateFile($"matrix-{size}.bin", content);
        var targetDirectory = AugustFileTestSupport.CreateDirectory();
        var distributor = new FileDistributor(new PacketSerializer(), new InMemoryLogSink());
        var receiver = new FileReceiver();

        var packets = await distributor.BuildFilePacketsAsync(sourcePath, FileTransferRules.MinChunkSize);
        FileReceiveResult? final = null;
        foreach (var packet in packets)
        {
            final = await receiver.TrySaveAsync(packet, targetDirectory);
        }

        Assert.NotNull(final);
        Assert.True(final!.Success);
        Assert.Equal(100, final.ProgressPercent);
        Assert.Equal(
            ChecksumUtility.ComputeSha256(content),
            ChecksumUtility.ComputeSha256(await File.ReadAllBytesAsync(final.FilePath!)));
    }
}
