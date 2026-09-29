namespace EduStream.Core.Collaboration;

/// <summary>
/// 참가를 마친 학생에게 주는 재연결 토큰입니다. 연결이 비정상으로 끊긴 뒤 WindowSeconds 안에만 한 번 쓸 수 있고,
/// 쓰면 비밀번호 재입력 없이 같은 이름으로 다시 참가합니다. 정상 퇴장·세션 종료 후에는 쓸 수 없습니다.
/// </summary>
public sealed record ReconnectGrantNotice(string Token, int WindowSeconds)
{
    // 로그·디버거 표시에 토큰이 나오지 않게 한다.
    public override string ToString() => $"ReconnectGrantNotice {{ Token = ***, WindowSeconds = {WindowSeconds} }}";
}

/// <summary>교수자가 세션을 끝낼 때 보내는 알림입니다. 받은 학생은 자동 재연결을 시도하지 않습니다.</summary>
public sealed record SessionEndedNotice(Guid SessionId);

public static class ReconnectRules
{
    public const int MaxTokenLength = 64;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(2);

    public static void Validate(ReconnectGrantNotice notice)
    {
        if (notice is null || string.IsNullOrEmpty(notice.Token) || notice.Token.Length > MaxTokenLength ||
            notice.WindowSeconds is < 1 or > 3600)
            throw new CollaborationException(CollaborationError.InvalidRequest);
    }

    public static void Validate(SessionEndedNotice notice)
    {
        if (notice is null) throw new CollaborationException(CollaborationError.InvalidRequest);
        CollaborationContract.RequireId(notice.SessionId, nameof(notice.SessionId));
    }
}
