namespace EduStream.Server.Services;

/// <summary>
/// 발급된 RDP 초대의 비밀번호를 교수자 앱이 화면 표시·구두 전달 등 별도 채널로 인계하기 위한 값입니다.
/// TCP 패킷에는 실리지 않으며 프로세스 내부(SessionManager → ServerViewModel 등)에서만 전달합니다.
/// </summary>
public sealed record RdpInvitationHandoff(
    string ParticipantId,
    Guid ConnectionId,
    Guid InvitationId,
    string Password,
    DateTimeOffset ExpiresAt);
