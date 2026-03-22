namespace EduStream.Core.Models;

/// <summary>
/// 학생이 세션에 참여할 때 서버로 보내는 공통 요청 패킷입니다.
/// </summary>
public sealed class SessionJoinPacket : BasePacket
{
    public SessionJoinPacket()
    {
        MessageType = PacketType.SessionJoin;
    }

    public string DisplayName { get; set; } = string.Empty;

    public string TargetAddress { get; set; } = string.Empty;

    public int TargetPort { get; set; }
}
