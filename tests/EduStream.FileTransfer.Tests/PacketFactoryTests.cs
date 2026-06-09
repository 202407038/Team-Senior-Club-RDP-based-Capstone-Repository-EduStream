using System.Text;
using EduStream.Core.Factories;
using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.FileTransfer.Tests;

public sealed class PacketFactoryTests
{
    [Fact]
    public void CreateSessionJoin_ShouldPopulateJoinContract()
    {
        var packet = PacketFactory.CreateSessionJoin(
            senderId: "student-01",
            displayName: "Student 01",
            targetAddress: "127.0.0.1",
            targetPort: 5000);

        Assert.Equal(PacketType.SessionJoin, packet.MessageType);
        Assert.Equal("student-01", packet.SenderId);
        Assert.Equal("Student 01", packet.DisplayName);
        Assert.Equal("127.0.0.1", packet.TargetAddress);
        Assert.Equal(5000, packet.TargetPort);
    }

    [Fact]
    public void CreateSessionLeave_ShouldPopulateLeaveContract()
    {
        var sessionId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var packet = PacketFactory.CreateSessionLeave(
            senderId: "student-01",
            displayName: "Student 01",
            reason: "manual disconnect",
            sessionId: sessionId,
            correlationId: correlationId);

        Assert.Equal(PacketType.SessionLeave, packet.MessageType);
        Assert.Equal(sessionId, packet.SessionId);
        Assert.Equal(correlationId, packet.CorrelationId);
        Assert.Equal("student-01", packet.SenderId);
        Assert.Equal("Student 01", packet.DisplayName);
        Assert.Equal("manual disconnect", packet.Reason);
    }

    [Fact]
    public void CreateHeartbeat_ShouldUseProvidedSenderAndSession()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var packet = PacketFactory.CreateHeartbeat(
            senderId: "server",
            sessionId: sessionId,
            lastSeenAt: now);

        Assert.Equal(PacketType.Heartbeat, packet.MessageType);
        Assert.Equal("server", packet.SenderId);
        Assert.Equal(sessionId, packet.SessionId);
        Assert.Equal(now, packet.LastSeenAt);
    }

    [Fact]
    public void CreateTextPackets_ShouldUseUtf8PayloadLength()
    {
        const string message = "세션 참여 완료";
        var expectedLength = Encoding.UTF8.GetByteCount(message);

        var ack = PacketFactory.CreateAck("server", AckCodes.SessionJoined, message);
        var error = PacketFactory.CreateError("server", ErrorCodes.JoinRejected, message, true);
        var chat = PacketFactory.CreateChat("student-01", "Student 01", message);

        Assert.Equal(expectedLength, ack.DataLength);
        Assert.Equal(expectedLength, error.DataLength);
        Assert.Equal(expectedLength, chat.DataLength);
    }

    [Fact]
    public void CreateFileChunk_ShouldPopulateMetadataAndPayloadLength()
    {
        var sessionId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var payload = new byte[] { 1, 2, 3, 4 };

        var packet = PacketFactory.CreateFileChunk(
            senderId: "server",
            fileName: "lecture.pdf",
            fileSize: 1024,
            checksum: "sha256",
            transferId: transferId,
            chunkIndex: 1,
            totalChunks: 4,
            content: payload,
            sessionId: sessionId,
            contentType: "application/pdf");

        Assert.Equal(PacketType.File, packet.MessageType);
        Assert.Equal("server", packet.SenderId);
        Assert.Equal(sessionId, packet.SessionId);
        Assert.Equal("lecture.pdf", packet.FileName);
        Assert.Equal(1024, packet.FileSize);
        Assert.Equal("sha256", packet.Checksum);
        Assert.Equal(transferId, packet.TransferId);
        Assert.Equal(1, packet.ChunkIndex);
        Assert.Equal(4, packet.TotalChunks);
        Assert.Equal("application/pdf", packet.ContentType);
        Assert.Equal(payload, packet.Content);
        Assert.Equal(payload.Length, packet.DataLength);
    }

    [Fact]
    public void CreateScreenFrame_ShouldPopulateFrameMetadata()
    {
        var sessionId = Guid.NewGuid();
        var capturedAt = DateTimeOffset.UtcNow;
        var payload = new byte[] { 9, 8, 7 };

        var packet = PacketFactory.CreateScreenFrame(
            senderId: "server",
            frameIndex: 3,
            frameDescription: "Primary screen frame",
            width: 1920,
            height: 1080,
            encoding: ScreenEncodings.Png,
            content: payload,
            sessionId: sessionId,
            capturedAt: capturedAt);

        Assert.Equal(PacketType.Screen, packet.MessageType);
        Assert.Equal("server", packet.SenderId);
        Assert.Equal(sessionId, packet.SessionId);
        Assert.Equal(3, packet.FrameIndex);
        Assert.Equal("Primary screen frame", packet.FrameDescription);
        Assert.Equal(1920, packet.Width);
        Assert.Equal(1080, packet.Height);
        Assert.Equal(ScreenEncodings.Png, packet.Encoding);
        Assert.Equal(payload, packet.Content);
        Assert.Equal(payload.Length, packet.DataLength);
        Assert.Equal(capturedAt, packet.CapturedAt);
    }
}
