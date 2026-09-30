namespace EduStream.Core.Collaboration;

/// <summary>
/// RDP 초대 비밀번호를 그 초대를 받을 학생의 보호 채널로만 보내는 알림입니다. 초대 자체(연결 문자열)는 기존 TCP
/// <c>RdpInvitationPacket</c>으로 가므로, 학생은 InvitationId·ConnectionId·SessionId가 모두 같은 초대에만 이 비밀번호를 씁니다.
/// </summary>
public sealed record RdpInvitationSecretNotice(Guid SessionId, Guid InvitationId, Guid ConnectionId, string Password,
    DateTimeOffset ExpiresAt)
{
    // 로그·디버거 표시에 비밀번호가 나오지 않게 한다.
    public override string ToString() =>
        $"RdpInvitationSecretNotice {{ SessionId = {SessionId}, InvitationId = {InvitationId}, ConnectionId = {ConnectionId}, Password = ***, ExpiresAt = {ExpiresAt:O} }}";
}

public static class RdpInvitationSecretRules
{
    public const int MaxPasswordLength = 128;

    public static void Validate(RdpInvitationSecretNotice notice)
    {
        if (notice is null) throw new CollaborationException(CollaborationError.InvalidRequest);
        CollaborationContract.RequireId(notice.SessionId, nameof(notice.SessionId));
        CollaborationContract.RequireId(notice.InvitationId, nameof(notice.InvitationId));
        CollaborationContract.RequireId(notice.ConnectionId, nameof(notice.ConnectionId));
        if (string.IsNullOrEmpty(notice.Password) || notice.Password.Length > MaxPasswordLength ||
            notice.ExpiresAt == default)
            throw new CollaborationException(CollaborationError.InvalidRequest);
    }
}
