namespace EduStream.Core.Collaboration;

/// <summary>
/// 교수자가 학생 한 명에게 그 학생이 원격 제어 대상인지 알리는 메시지입니다(U07 "현재 제어 중" 표시).
/// Sequence는 세션 안에서 단조 증가하며, 학생은 더 큰 값만 적용합니다.
/// </summary>
public sealed record ControlStatusNotice(Guid SessionId, long Sequence, ControlPhase Phase);

/// <summary>
/// 학생이 자기 보기/제어 허용을 바꾸는 요청입니다. 대상 학생은 보호 채널에 묶인 연결로만 정하며
/// 메시지에 신원을 싣지 않습니다. 보기를 끄면 제어도 함께 꺼집니다.
/// </summary>
public sealed record PermissionChangeRequest(Guid RequestId, bool AllowViewing, bool AllowControl);

public static class StudentStatusRules
{
    public static void Validate(ControlStatusNotice notice)
    {
        if (notice is null) throw new CollaborationException(CollaborationError.InvalidRequest);
        CollaborationContract.RequireId(notice.SessionId, nameof(notice.SessionId));
        if (notice.Sequence < 1 || !Enum.IsDefined(notice.Phase))
            throw new CollaborationException(CollaborationError.InvalidRequest);
    }

    public static void Validate(PermissionChangeRequest request)
    {
        if (request is null) throw new CollaborationException(CollaborationError.InvalidRequest);
        CollaborationContract.RequireId(request.RequestId, nameof(request.RequestId));
    }
}
