namespace EduStream.Core.Collaboration;

/// <summary>
/// 보호 채널의 Guid 신원(<see cref="ParticipantConnection"/>)과 #51 역방향 WDS 엔진의 문자열 신원 사이의 단일 매핑 규칙입니다.
/// 학생 앱(초대 생성)과 교수자 앱(초대 대조·제어 게이트)이 같은 규칙을 써야 하므로 Core에 둡니다.
/// </summary>
/// <remarks>
/// 참가 레지스트리는 참가·재접속마다 ParticipantId를 새로 발급하므로, 재접속 전에 만든 초대는 신원 대조에서 자동으로 거부됩니다.
/// 표시 이름은 사람이 바꿀 수 있고 중복 검사 외의 보장이 없어 신원으로 쓰지 않습니다.
/// </remarks>
public static class ReverseRdpIdentity
{
    /// <summary>역방향 초대의 ProfessorId/StudentId(ParticipantId)로 쓸 문자열입니다.</summary>
    public static string For(ParticipantConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.Validate();
        return connection.ParticipantId.ToString("N");
    }
}

/// <summary>
/// 학생 호스트가 역방향 초대를 만들 때 넣어야 하는 값입니다. 모두 서버가 보호 채널로 알려 준 현재 연결에서 얻습니다.
/// ConnectionId는 초대를 보내는 학생의 현재 인증 연결이며, 그 연결이 끊기면 초대도 무효입니다.
/// </summary>
public sealed record ReverseRdpInvitationTarget(Guid SessionId, Guid ConnectionId, string StudentId, string ProfessorId);
