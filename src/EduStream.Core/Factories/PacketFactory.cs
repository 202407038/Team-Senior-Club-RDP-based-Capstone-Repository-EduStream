using EduStream.Core.Models;

namespace EduStream.Core.Factories;

/// <summary>
/// 서버와 클라이언트가 공통 패킷을 같은 규칙으로 생성하도록 돕는 팩토리입니다.
/// 서비스나 ViewModel에서 반복되던 기본 필드 채우기 로직을 한 곳으로 모읍니다.
/// </summary>
public static class PacketFactory
{
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
            DataLength = message.Length,
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
            DataLength = message.Length,
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
            DataLength = message.Length
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
}
