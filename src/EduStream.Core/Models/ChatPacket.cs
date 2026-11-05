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

    // 기존 JSON 수신자는 이 필드를 무시하고 일반 채팅으로 표시한다.
    public bool IsAnnouncement { get; set; }
    public Guid? AnnouncementId { get; set; }
    public bool IsImportantAnnouncement { get; set; }
    public bool IsAnnouncementHistory { get; set; }
    // 수신/명시 확인 응답은 서버에서 소비하고 다른 학생에게 중계하지 않는다.
    public AnnouncementReceipt AnnouncementReceipt { get; set; }

    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum AnnouncementReceipt { None, Received, Confirmed }
