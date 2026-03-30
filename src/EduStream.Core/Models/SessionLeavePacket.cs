namespace EduStream.Core.Models;

/// <summary>
/// 참여자가 세션을 떠날 때 송신하는 공통 패킷입니다.
/// </summary>
public sealed class SessionLeavePacket : BasePacket
{
    public SessionLeavePacket()
    {
        MessageType = PacketType.SessionLeave;
    }

    public string DisplayName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
