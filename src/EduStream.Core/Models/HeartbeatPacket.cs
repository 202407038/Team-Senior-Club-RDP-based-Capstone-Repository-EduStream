namespace EduStream.Core.Models;

/// <summary>
/// 연결 유지를 위해 주기적으로 송수신하는 상태 확인 패킷입니다.
/// </summary>
public sealed class HeartbeatPacket : BasePacket
{
    public HeartbeatPacket()
    {
        MessageType = PacketType.Heartbeat;
    }

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
