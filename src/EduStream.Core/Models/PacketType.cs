namespace EduStream.Core.Models;

/// <summary>
/// 서버와 클라이언트가 주고받는 패킷 종류를 구분합니다.
/// </summary>
public enum PacketType
{
    Unknown = 0,
    Chat = 1,
    File = 2,
    Screen = 3
}
