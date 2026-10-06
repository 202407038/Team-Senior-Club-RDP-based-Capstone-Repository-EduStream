namespace EduStream.Core.Collaboration;

public enum RemoteInputAction { Grant = 1, Revoke = 2 }

/// <summary>
/// 교수자 앱이 제어 대상 학생 한 명의 보호 채널로만 보내는 실제 입력 허용/회수 명령입니다(Kind 18).
/// 학생 앱은 자기 PC의 역방향 공유 호스트에 적용한 뒤 같은 CommandId로 <see cref="RemoteInputResultNotice"/>를 돌려줍니다.
/// </summary>
/// <remarks>
/// ControlRequestId는 교수자 측 <see cref="RemoteControlState.RequestId"/>, ProfessorId는 <see cref="ReverseRdpIdentity"/> 문자열입니다.
/// 학생은 ProfessorId·SharingId를 자기 보호 채널 상태와 대조해야 하며 수신 값만으로 대상을 정하지 않습니다.
/// 회수는 멱등이므로 이미 끝난 공유·허용 전 요청에도 성공으로 응답합니다.
/// </remarks>
public sealed record RemoteInputCommandNotice(Guid CommandId, Guid SessionId, Guid SharingId, Guid ControlRequestId,
    string ProfessorId, RemoteInputAction Action);

/// <summary>
/// 학생 PC에서 명령을 실제로 적용했는지 알리는 결과입니다(Kind 19). 성공이면 Error는 null, 실패면 사유가 있어야 합니다.
/// 송신 성공이나 명령 수신만으로 교수자가 제어를 Active로 표시하지 않도록 이 결과를 기다립니다.
/// </summary>
public sealed record RemoteInputResultNotice(Guid CommandId, Guid SessionId, RemoteInputAction Action, bool Applied,
    CollaborationError? Error);

public static class RemoteInputRules
{
    public static void Validate(RemoteInputCommandNotice notice)
    {
        if (notice is null) throw new CollaborationException(CollaborationError.InvalidRequest);
        CollaborationContract.RequireId(notice.CommandId, nameof(notice.CommandId));
        CollaborationContract.RequireId(notice.SessionId, nameof(notice.SessionId));
        CollaborationContract.RequireId(notice.SharingId, nameof(notice.SharingId));
        CollaborationContract.RequireId(notice.ControlRequestId, nameof(notice.ControlRequestId));
        if (!ReverseRdpInvitationNotice.ValidIdentity(notice.ProfessorId) || !Enum.IsDefined(notice.Action))
            throw new CollaborationException(CollaborationError.InvalidRequest);
    }

    public static void Validate(RemoteInputResultNotice notice)
    {
        if (notice is null) throw new CollaborationException(CollaborationError.InvalidRequest);
        CollaborationContract.RequireId(notice.CommandId, nameof(notice.CommandId));
        CollaborationContract.RequireId(notice.SessionId, nameof(notice.SessionId));
        if (!Enum.IsDefined(notice.Action) || notice.Applied != (notice.Error is null) ||
            (notice.Error is { } error && !Enum.IsDefined(error)))
            throw new CollaborationException(CollaborationError.InvalidRequest);
    }
}
