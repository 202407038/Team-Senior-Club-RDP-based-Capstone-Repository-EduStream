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

    /// <summary>
    /// 보호 채널 참가 인증으로 받은 일회용 티켓입니다. 보호 채널을 쓰는 세션은 이 값이 없으면 참가를 거부합니다.
    /// 비밀번호는 담지 않습니다.
    /// </summary>
    public string? JoinTicket { get; set; }
}
