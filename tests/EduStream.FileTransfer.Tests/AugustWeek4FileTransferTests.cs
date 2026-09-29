using EduStream.Client.Services;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class AugustWeek4FileStabilityTests
{
    [Fact]
    public async Task EightMegabyteFile_ShouldRoundTripWithoutStateLoss()
    {
        var content = AugustFileTestSupport.CreateContent(8 * 1024 * 1024);
        var sourcePath = AugustFileTestSupport.CreateFile("large-8mb.bin", content);
        var targetDirectory = AugustFileTestSupport.CreateDirectory();
        var distributor = new FileDistributor(new PacketSerializer(), new InMemoryLogSink());
        var receiver = new FileReceiver();
        var packets = await distributor.BuildFilePacketsAsync(sourcePath, FileTransferRules.DefaultChunkSize);

        FileReceiveResult? final = null;
        foreach (var packet in packets)
        {
            final = await receiver.TrySaveAsync(packet, targetDirectory);
        }

        Assert.NotNull(final);
        Assert.True(final!.Success);
        Assert.Equal(packets.Count, final.ReceivedChunkCount);
        Assert.Equal(content, await File.ReadAllBytesAsync(final.FilePath!));
    }

    [Fact]
    public async Task ConcurrentMediumTransfers_ShouldRemainIsolated()
    {
        var distributor = new FileDistributor(new PacketSerializer(), new InMemoryLogSink());
        var receiver = new FileReceiver();
        var targetDirectory = AugustFileTestSupport.CreateDirectory();
        var inputs = Enumerable.Range(1, 3)
            .Select(index => new
            {
                Name = $"concurrent-{index}.bin",
                Content = AugustFileTestSupport.CreateContent(1024 * 1024 + index * 37)
            })
            .ToArray();

        var transfers = new List<(byte[] Content, IReadOnlyList<FilePacket> Packets)>();
        foreach (var input in inputs)
        {
            var path = AugustFileTestSupport.CreateFile(input.Name, input.Content);
            var packets = await distributor.BuildFilePacketsAsync(path, FileTransferRules.DefaultChunkSize);
            transfers.Add((input.Content, packets));
        }

        var results = await Task.WhenAll(transfers.Select(async transfer =>
        {
            FileReceiveResult? final = null;
            foreach (var packet in transfer.Packets)
            {
                final = await receiver.TrySaveAsync(packet, targetDirectory);
            }

            return final!;
        }));

        Assert.All(results, result => Assert.True(result.Success));
        for (var index = 0; index < results.Length; index++)
        {
            Assert.Equal(transfers[index].Content, await File.ReadAllBytesAsync(results[index].FilePath!));
        }
    }
}
