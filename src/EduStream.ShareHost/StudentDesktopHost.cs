using EduStream.Core.Collaboration;

namespace EduStream.ShareHost;

/// <summary>
/// 학생 PC에서 실행하는 역방향 공유 호스트입니다.
/// 이 객체가 돌아가는 컴퓨터의 데스크톱만 송신합니다. 다른 학생 화면은 그 학생 PC의 프로세스에서 따로 엽니다.
/// </summary>
public sealed class StudentDesktopHost : IAsyncDisposable
{
    private readonly ReverseSessionManager _session = new();

    public StudentDesktopHost(string studentId)
    {
        if (string.IsNullOrWhiteSpace(studentId))
            throw new ArgumentException("학생 ID가 필요합니다.", nameof(studentId));
        StudentId = studentId;
    }

    public string StudentId { get; }

    /// <summary>이 PC에서 열린 WDS 세션. 권한 변경 알림을 구독할 때 사용합니다.</summary>
    public ReverseSessionManager Session => _session;

    public MonitorInfo? SharedMonitor => _session.SharedMonitor;
    public ReverseSessionState State => _session.CurrentState;

    public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _session.StartReverseSharingAsync(sessionId, StudentId, cancellationToken);

    public Task<Guid> StartAsync(Guid sessionId, MonitorInfo shareMonitor, CancellationToken cancellationToken = default)
        => _session.StartReverseSharingAsync(sessionId, StudentId, shareMonitor, cancellationToken);

    /// <summary>Core 초대 계약을 만듭니다. 비밀번호는 반환값에 넣지 않습니다.</summary>
    public async Task<ReverseRdpInvitationNotice> CreateInvitationAsync(
        Guid sessionId,
        Guid sharingId,
        string professorId,
        Guid connectionId,
        string invitationPassword,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        var packet = await _session.CreateProfessorInvitationAsync(
            sessionId, sharingId, professorId, connectionId, invitationPassword, expiresAt, cancellationToken);
        return ReverseInvitationWire.From(packet).ToContract();
    }

    public Task GrantControlAsync(string professorId, CancellationToken cancellationToken = default)
        => _session.GrantControlAsync(professorId, cancellationToken);

    public Task RevokeControlAsync(string professorId, CancellationToken cancellationToken = default)
        => _session.RevokeControlAsync(professorId, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _session.StopReverseSharingAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { _session.Dispose(); }
    }
}
