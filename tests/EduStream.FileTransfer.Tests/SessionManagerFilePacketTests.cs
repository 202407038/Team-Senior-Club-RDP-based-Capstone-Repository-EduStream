using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class SessionManagerFilePacketTests
{
    [Fact]
    public async Task BroadcastPacketAsync_WithValidFilePacket_ShouldComplete()
    {
        var logSink = new InMemoryLogSink();
        var sessionManager = CreateSessionManager(logSink);
        var payload = new byte[] { 1, 2, 3, 4 };
        var packet = new FilePacket
        {
            SenderId = "Server",
            FileName = "lecture-note.txt",
            FileSize = payload.Length,
            Checksum = "sha256",
            TransferId = Guid.NewGuid(),
            ChunkIndex = 0,
            TotalChunks = 1,
            Content = payload
        };

        await sessionManager.BroadcastPacketAsync(packet);

        Assert.Contains(logSink.Snapshot(), entry => entry.Contains("File"));
    }

    [Fact]
    public async Task BroadcastPacketAsync_WithMissingChecksum_ShouldRejectBeforeBroadcast()
    {
        var sessionManager = CreateSessionManager(new InMemoryLogSink());
        var packet = new FilePacket
        {
            SenderId = "Server",
            FileName = "lecture-note.txt",
            FileSize = 4,
            TransferId = Guid.NewGuid(),
            ChunkIndex = 0,
            TotalChunks = 1,
            Content = new byte[] { 1, 2, 3, 4 }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sessionManager.BroadcastPacketAsync(packet));
        Assert.Equal(ErrorCodes.ChecksumRequired, ex.Message);
    }

    private static SessionManager CreateSessionManager(InMemoryLogSink logSink)
    {
        var serializer = new PacketSerializer();
        var tcpServer = new TcpServerService(logSink, serializer);
        return new SessionManager(logSink, tcpServer);
    }
}
