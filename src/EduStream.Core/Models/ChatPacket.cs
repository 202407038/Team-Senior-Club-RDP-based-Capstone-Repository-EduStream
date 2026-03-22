namespace EduStream.Core.Models;

/// <summary>
/// 텍스트 채팅 메시지 전송용 패킷입니다.
/// </summary>
public sealed class ChatPacket : BasePacket
{
    public ChatPacket()
    {
        MessageType = PacketType.Chat;
    }

    public string Sender { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public bool IsSystemMessage { get; set; }

    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}
