using System.Text;
using EduStream.Core.Logging;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class FileDistributorTests
{
    [Fact]
    public async Task BuildFilePacketsAsync_ShouldSplitFileUsingChunkMetadata()
    {
        var distributor = new FileDistributor(new PacketSerializer(), new InMemoryLogSink());
        var filePath = CreateSampleFile(new string('Z', FileTransferRules.MinChunkSize * 2 + 37));

        var packets = await distributor.BuildFilePacketsAsync(
            filePath,
            senderId: "Professor",
            sessionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            chunkSize: FileTransferRules.MinChunkSize);

        Assert.True(packets.Count >= 3);
        Assert.All(packets, packet =>
        {
            Assert.Equal("Professor", packet.SenderId);
            Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), packet.SessionId);
            Assert.Equal(Path.GetFileName(filePath), packet.FileName);
            Assert.Equal(packets.Count, packet.TotalChunks);
            Assert.True(packet.Content.Length > 0);
        });
        Assert.Equal(0, packets[0].ChunkIndex);
        Assert.Equal(packets.Count - 1, packets[^1].ChunkIndex);
    }

    [Fact]
    public async Task BuildFilePacketsAsync_ShouldPreservePayloadAcrossRoundTrip()
    {
        var distributor = new FileDistributor(new PacketSerializer(), new InMemoryLogSink());
        var originalText = string.Join('|', Enumerable.Range(1, 2500));
        var filePath = CreateSampleFile(originalText);

        var packets = await distributor.BuildFilePacketsAsync(filePath, chunkSize: FileTransferRules.MinChunkSize);
        var assembled = packets
            .OrderBy(packet => packet.ChunkIndex)
            .SelectMany(packet => packet.Content)
            .ToArray();

        Assert.Equal(Encoding.UTF8.GetBytes(originalText), assembled);
    }

    private static string CreateSampleFile(string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "EduStream-Distributor-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "sample.txt");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }
}
