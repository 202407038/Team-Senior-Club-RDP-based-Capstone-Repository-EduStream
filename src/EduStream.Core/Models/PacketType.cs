namespace EduStream.Core.Models;

/// <summary>
/// 서버와 클라이언트가 주고받는 패킷 종류를 구분합니다.
/// </summary>
public enum PacketType
{
    Unknown = 0,
    SessionJoin = 1,
    SessionLeave = 2,
    Chat = 3,
    File = 4,
    Screen = 5,
    Ack = 6,
    Error = 7,
    Heartbeat = 8
}
