namespace EduStream.Core.Collaboration;

/// <summary>
/// 학생이 보호 채널로 보내는 참가 인증 요청입니다. 비밀번호는 보호 채널 안에서만 전송하며
/// 기존 v1 TCP 패킷에는 싣지 않습니다.
/// </summary>
public sealed class RoomAuthRequest
{
    public Guid AttemptId { get; init; }
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>UTF-8 비밀번호. 비밀번호 없는 방이면 비어 있습니다. 사용 후 보낸 쪽·받은 쪽 모두 0으로 지웁니다.</summary>
    public byte[] Password { get; init; } = [];

    // 로그·디버거 표시에 비밀번호가 나오지 않게 한다.
    public override string ToString() =>
        $"RoomAuthRequest {{ AttemptId = {AttemptId}, DisplayName = {DisplayName}, Password = *** }}";
}

/// <summary>
/// 교수자 응답. 승인되면 기존 TCP 참가 요청에 한 번만 쓸 수 있는 티켓을 줍니다.
/// 거부 사유는 NotAuthorized(비밀번호 불일치), ResourceLimit(시도 제한), InvalidRequest 중 하나입니다.
/// </summary>
public sealed record RoomAuthResult(Guid AttemptId, bool Accepted, CollaborationError? Error,
    string? JoinTicket, Guid? SessionId);

public static class RoomAuthRules
{
    public const int MaxDisplayNameLength = 80;
    // RoomPasswordVerifier.MaxPasswordLength(128자)의 UTF-8 최대 길이.
    public const int MaxPasswordBytes = 128 * 4;
    public const int MaxTicketLength = 64;
    // 인증 직후 같은 학생 앱이 바로 TCP 참가 요청을 보내는 데 충분한 시간만 준다.
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);

    public static void Validate(RoomAuthRequest request)
    {
        if (request is null) throw new CollaborationException(CollaborationError.InvalidRequest);
        CollaborationContract.RequireId(request.AttemptId, nameof(request.AttemptId));
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > MaxDisplayNameLength ||
            request.Password is null || request.Password.Length > MaxPasswordBytes)
            throw new CollaborationException(CollaborationError.InvalidRequest);
    }

    public static void Validate(RoomAuthResult result)
    {
        if (result is null) throw new CollaborationException(CollaborationError.InvalidRequest);
        CollaborationContract.RequireId(result.AttemptId, nameof(result.AttemptId));
        var valid = result.Accepted
            ? result.Error is null && !string.IsNullOrEmpty(result.JoinTicket) &&
              result.JoinTicket.Length <= MaxTicketLength && result.SessionId is { } id && id != Guid.Empty
            : result.Error is { } error && Enum.IsDefined(error) && result.JoinTicket is null;
        if (!valid) throw new CollaborationException(CollaborationError.InvalidRequest);
    }
}
