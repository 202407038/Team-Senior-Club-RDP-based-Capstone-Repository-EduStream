using System.Text;

namespace EduStream.Core.Collaboration;

public enum ReverseRdpControlMode { ViewOnly, HostGrantedInteractive }

/// <summary>
/// #51 역방향 WDS 초대를 Client/Server가 함께 사용하는 계약입니다.
/// 정방향 보기 전용 계약과 별개이며, 생성·직렬화만으로 연결이나 제어 권한이 생기지 않습니다.
/// </summary>
public sealed record ReverseRdpInvitationNotice
{
    public const int CurrentVersion = 1;
    public const string ProviderName = "windows-desktop-sharing";
    public const string StudentToProfessor = "student-to-professor";
    public const int MaxConnectionStringBytes = 64 * 1024;
    public const int MaxIdentityLength = 256;

    public required int ContractVersion { get; init; }
    public required string Provider { get; init; }
    public required string Direction { get; init; }
    public required Guid SessionId { get; init; }
    public required Guid SharingId { get; init; }
    public required Guid InvitationId { get; init; }
    public required Guid ConnectionId { get; init; }
    public required string ProfessorId { get; init; }
    public required string StudentId { get; init; }
    public required string ParticipantId { get; init; }
    public required string ConnectionString { get; init; }
    public required int DataLength { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required ReverseRdpControlMode ControlMode { get; init; }
    public required bool ViewOnly { get; init; }

    /// <summary>구조 검사. 유효기간·인증된 연결과의 대조는 ValidateForConnection으로 별도 수행합니다.</summary>
    public void Validate()
    {
        if (ContractVersion != CurrentVersion || Provider != ProviderName || Direction != StudentToProfessor ||
            SessionId == Guid.Empty || SharingId == Guid.Empty || InvitationId == Guid.Empty || ConnectionId == Guid.Empty ||
            !ValidIdentity(ProfessorId) || !ValidIdentity(StudentId) || ParticipantId != ProfessorId ||
            ExpiresAt == default || !Enum.IsDefined(ControlMode) ||
            ViewOnly != (ControlMode == ReverseRdpControlMode.ViewOnly) || string.IsNullOrWhiteSpace(ConnectionString) ||
            Encoding.UTF8.GetByteCount(ConnectionString) > MaxConnectionStringBytes ||
            DataLength != Encoding.UTF8.GetByteCount(ConnectionString))
            throw new ArgumentException("유효하지 않은 역방향 RDP 초대입니다.");
    }

    /// <summary>2번이 인증된 연결에서 얻은 값으로 대조해야 하며 수신 payload의 값을 그대로 넘기면 안 됩니다.</summary>
    public void ValidateForConnection(Guid sessionId, string professorId, Guid connectionId,
        string studentId, DateTimeOffset now)
    {
        Validate();
        if (SessionId != sessionId || ProfessorId != professorId || ConnectionId != connectionId ||
            StudentId != studentId || ExpiresAt <= now)
            throw new ArgumentException("현재 연결과 일치하지 않거나 만료된 역방향 초대입니다.");
    }

    public override string ToString() =>
        $"ReverseRdpInvitationNotice {{ SessionId = {SessionId}, SharingId = {SharingId}, InvitationId = {InvitationId}, ConnectionString = *** }}";

    internal static bool ValidIdentity(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxIdentityLength;
}

/// <summary>역방향 초대의 비밀번호. 인증된 학생→교수자 보호 채널로만 전달하며 브로드캐스트하지 않습니다.</summary>
public sealed record ReverseRdpInvitationSecretNotice(Guid SessionId, Guid SharingId, Guid InvitationId,
    Guid ConnectionId, string StudentId, string ProfessorId, string Password, DateTimeOffset ExpiresAt)
{
    public void Validate()
    {
        if (SessionId == Guid.Empty || SharingId == Guid.Empty || InvitationId == Guid.Empty || ConnectionId == Guid.Empty ||
            !ReverseRdpInvitationNotice.ValidIdentity(StudentId) || !ReverseRdpInvitationNotice.ValidIdentity(ProfessorId) ||
            string.IsNullOrEmpty(Password) || Password.Length > RdpInvitationSecretRules.MaxPasswordLength || ExpiresAt == default)
            throw new ArgumentException("유효하지 않은 역방향 초대 비밀번호 알림입니다.");
    }

    /// <summary>초대의 인증된 연결 대조 후 호출합니다. 재접속 전 비밀번호를 새 초대에 재사용하지 않습니다.</summary>
    public void ValidateForInvitation(ReverseRdpInvitationNotice invitation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        Validate();
        invitation.Validate();
        if (SessionId != invitation.SessionId || SharingId != invitation.SharingId || InvitationId != invitation.InvitationId ||
            ConnectionId != invitation.ConnectionId || StudentId != invitation.StudentId || ProfessorId != invitation.ProfessorId ||
            ExpiresAt != invitation.ExpiresAt || ExpiresAt <= now)
            throw new ArgumentException("역방향 초대와 비밀번호 알림이 일치하지 않습니다.");
    }

    public override string ToString() =>
        $"ReverseRdpInvitationSecretNotice {{ SessionId = {SessionId}, InvitationId = {InvitationId}, Password = *** }}";
}
