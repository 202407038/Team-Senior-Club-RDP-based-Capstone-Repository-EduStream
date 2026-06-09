using System.Text;
using EduStream.Core.Models;

namespace EduStream.Core.Factories;

/// <summary>
/// 서버와 클라이언트가 공통 패킷을 같은 규칙으로 생성하도록 돕는 팩토리입니다.
/// 서비스나 ViewModel에서 반복되던 기본 필드 채우기 로직을 한 곳으로 모읍니다.
/// </summary>
public static class PacketFactory
{
    public static SessionJoinPacket CreateSessionJoin(
        string senderId,
        string displayName,
        string targetAddress,
        int targetPort)
    {
        return new SessionJoinPacket
        {
            SenderId = senderId,
            DisplayName = displayName,
            TargetAddress = targetAddress,
            TargetPort = targetPort
        };
    }

    public static SessionLeavePacket CreateSessionLeave(
        string senderId,
        string displayName,
        string reason,
        Guid? sessionId = null,
        Guid? correlationId = null)
    {
        return new SessionLeavePacket
        {
            SessionId = sessionId,
            SenderId = senderId,
            DisplayName = displayName,
            Reason = reason,
            CorrelationId = correlationId ?? Guid.NewGuid()
        };
    }

    public static AckPacket CreateAck(
        string senderId,
        string ackCode,
        string message,
        Guid? sessionId = null,
        Guid? correlationId = null)
    {
        return new AckPacket
        {
            SessionId = sessionId,
            SenderId = senderId,
            AckCode = ackCode,
            Message = message,
            DataLength = GetTextPayloadLength(message),
            CorrelationId = correlationId ?? Guid.NewGuid()
        };
    }

    public static ErrorPacket CreateError(
        string senderId,
        string errorCode,
        string message,
        bool isRecoverable,
        Guid? sessionId = null,
        Guid? correlationId = null)
    {
        return new ErrorPacket
        {
            SessionId = sessionId,
            SenderId = senderId,
            ErrorCode = errorCode,
            Message = message,
            IsRecoverable = isRecoverable,
            DataLength = GetTextPayloadLength(message),
            CorrelationId = correlationId ?? Guid.NewGuid()
        };
    }

    public static ChatPacket CreateChat(
        string senderId,
        string sender,
        string message,
        Guid? sessionId = null,
        bool isSystemMessage = false)
    {
        return new ChatPacket
        {
            SessionId = sessionId,
            SenderId = senderId,
            Sender = sender,
            Message = message,
            IsSystemMessage = isSystemMessage,
            DataLength = GetTextPayloadLength(message)
        };
    }

    public static ChatPacket CreateSystemChat(
        string message,
        Guid? sessionId = null)
    {
        return CreateChat(
            senderId: "system",
            sender: "System",
            message: message,
            sessionId: sessionId,
            isSystemMessage: true);
    }

    public static HeartbeatPacket CreateHeartbeat(
        string senderId,
        Guid? sessionId = null,
        DateTimeOffset? lastSeenAt = null)
    {
        return new HeartbeatPacket
        {
            SessionId = sessionId,
            SenderId = senderId,
            LastSeenAt = lastSeenAt ?? DateTimeOffset.UtcNow
        };
    }

    public static FilePacket CreateFileChunk(
        string senderId,
        string fileName,
        long fileSize,
        string checksum,
        Guid transferId,
        int chunkIndex,
        int totalChunks,
        byte[] content,
        Guid? sessionId = null,
        string contentType = "application/octet-stream")
    {
        return new FilePacket
        {
            SessionId = sessionId,
            SenderId = senderId,
            FileName = fileName,
            FileSize = fileSize,
            ContentType = contentType,
            Checksum = checksum,
            TransferId = transferId,
            ChunkIndex = chunkIndex,
            TotalChunks = totalChunks,
            Content = content,
            DataLength = content.Length
        };
    }

    public static ScreenPacket CreateScreenFrame(
        string senderId,
        int frameIndex,
        string frameDescription,
        int width,
        int height,
        string encoding,
        byte[] content,
        Guid? sessionId = null,
        DateTimeOffset? capturedAt = null)
    {
        return new ScreenPacket
        {
            SessionId = sessionId,
            SenderId = senderId,
            FrameIndex = frameIndex,
            FrameDescription = frameDescription,
            CapturedAt = capturedAt ?? DateTimeOffset.UtcNow,
            Width = width,
            Height = height,
            Encoding = encoding,
            Content = content,
            DataLength = content.Length
        };
    }

    private static int GetTextPayloadLength(string message)
    {
        return Encoding.UTF8.GetByteCount(message);
    }
}
