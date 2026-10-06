using System.Security.Cryptography;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.ShareHost;

namespace EduStream.Client.Services;

/// <summary>학생 PC 역방향 공유 호스트. 테스트에서 실제 WDS 세션 없이 수명 규칙을 검증하기 위한 경계입니다.</summary>
public interface IStudentShareHost : IAsyncDisposable
{
    Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<ReverseRdpInvitationNotice> CreateInvitationAsync(Guid sessionId, Guid sharingId, string professorId,
        Guid connectionId, string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
}

/// <summary>3번 <see cref="StudentDesktopHost"/>를 그대로 감쌉니다. 모니터를 지정하지 않으면 이 PC의 데스크톱 전체를 공유합니다.</summary>
public sealed class StudentDesktopShareHost(string studentId) : IStudentShareHost
{
    private readonly StudentDesktopHost _host = new(studentId);

    public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _host.StartAsync(sessionId, cancellationToken);

    public Task<ReverseRdpInvitationNotice> CreateInvitationAsync(Guid sessionId, Guid sharingId, string professorId,
        Guid connectionId, string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        _host.CreateInvitationAsync(sessionId, sharingId, professorId, connectionId, invitationPassword, expiresAt, cancellationToken);

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}

/// <summary>
/// 2번 구현: 학생 앱의 역방향 공유 수명. 서버가 확정한 보기 허용과 <see cref="ReverseCollaborationClient.Target"/>이 모두 있을 때만
/// 이 PC에서 호스트를 열고 Kind 15·16을 보냅니다. 허용 OFF·대상(연결/교수자) 변경·퇴장·끊김이면 호스트를 닫습니다.
/// </summary>
/// <remarks>
/// 현재 단계에서는 초대 만료 전 재발급을 하지 않습니다. 같은 공유의 새 초대는 서버가 이전 초대를 회수해 교수자 viewer를 닫기 때문입니다.
/// 교수자 viewer는 InvitationReady 직후 접속한다는 전제이며, 시작·초대가 실패하거나 거부되면 허용/대상이 바뀔 때까지 다시 시도하지 않습니다.
/// </remarks>
public sealed class StudentReverseShareService : IAsyncDisposable
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _order = new(1, 1);
    private readonly ReverseCollaborationClient _reverse;
    private readonly Func<string, IStudentShareHost> _hostFactory;
    private readonly ILogSink _logSink;
    private readonly Func<DateTimeOffset> _clock;
    private bool _allowViewing;
    private bool _disposed;
    // 실패/거부된 대상. 같은 대상으로 재시도를 반복하지 않는다.
    private ReverseRdpInvitationTarget? _blockedTarget;
    private IStudentShareHost? _host;
    private ReverseRdpInvitationTarget? _hostTarget;
    private Guid _invitationId;

    /// <summary>공유 시작/종료 시 발생합니다(공유 중 여부, 사용자 안내 문구).</summary>
    public event Action<bool, string>? SharingChanged;

    public StudentReverseShareService(ReverseCollaborationClient reverse, ILogSink logSink,
        Func<string, IStudentShareHost>? hostFactory = null, Func<DateTimeOffset>? clock = null)
    {
        _reverse = reverse ?? throw new ArgumentNullException(nameof(reverse));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _hostFactory = hostFactory ?? (studentId => new StudentDesktopShareHost(studentId));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _reverse.InvitationRejected += OnInvitationRejected;
    }

    public bool IsSharing
    {
        get { lock (_gate) return _host is not null; }
    }

    /// <summary>
    /// 서버가 돌려준 보기 허용과 현재 대상으로 호스트 상태를 맞춥니다. 상태 스냅샷마다 호출해도 대상이 같으면 다시 열지 않습니다.
    /// 예외를 던지지 않으므로 수신 루프에서 기다리지 않고 호출할 수 있습니다.
    /// </summary>
    public Task UpdateAsync(bool allowViewing)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_allowViewing != allowViewing) _blockedTarget = null;
            _allowViewing = allowViewing;
        }
        return ReconcileAsync();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _reverse.InvitationRejected -= OnInvitationRejected;
        await ReconcileAsync().ConfigureAwait(false);
    }

    private void OnInvitationRejected(Guid invitationId, CollaborationError error)
    {
        lock (_gate)
        {
            if (_host is null || invitationId != _invitationId) return;
            _blockedTarget = _hostTarget;
        }
        _ = ReconcileAsync();
    }

    private async Task ReconcileAsync()
    {
        await _order.WaitAsync().ConfigureAwait(false);
        try
        {
            ReverseRdpInvitationTarget? desired;
            ReverseRdpInvitationTarget? blocked;
            ReverseRdpInvitationTarget? current;
            lock (_gate)
            {
                desired = _disposed || !_allowViewing ? null : _reverse.Target;
                blocked = _blockedTarget;
                current = _hostTarget;
            }
            if (desired is not null && desired == blocked) desired = null;
            if (desired is not null && desired == current) return;

            if (current is not null)
            {
                var message = desired is not null ? null
                    : current == blocked ? "교수자 앱이 화면 공유 초대를 받지 않아 공유를 중지했습니다."
                    : "내 화면 공유를 종료했습니다.";
                await StopHostAsync(message).ConfigureAwait(false);
            }
            if (desired is not null) await StartHostAsync(desired).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logSink.Write($"[Reverse] 역방향 공유 상태 정리 실패: {ex.GetType().Name}");
        }
        finally
        {
            _order.Release();
        }
    }

    private async Task StartHostAsync(ReverseRdpInvitationTarget target)
    {
        IStudentShareHost? host = null;
        try
        {
            host = _hostFactory(target.StudentId);
            var sharingId = await host.StartAsync(target.SessionId).ConfigureAwait(false);
            // 초대마다 새 비밀번호. 교수자 연결 하나에만 Kind 16으로 보내고 보관하지 않는다.
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            var invitation = await host.CreateInvitationAsync(target.SessionId, sharingId, target.ProfessorId,
                target.ConnectionId, password, _clock() + InvitationLifetime).ConfigureAwait(false);
            var secret = new ReverseRdpInvitationSecretNotice(target.SessionId, sharingId, invitation.InvitationId,
                target.ConnectionId, target.StudentId, target.ProfessorId, password, invitation.ExpiresAt);
            lock (_gate)
            {
                // 거부 알림이 전송 완료보다 먼저 올 수 있어 보내기 전에 현재 초대로 기록한다.
                _host = host;
                _hostTarget = target;
                _invitationId = invitation.InvitationId;
            }
            host = null;
            await _reverse.SendInvitationAsync(invitation, secret).ConfigureAwait(false);
            _logSink.Write($"[Reverse] 역방향 공유 시작: sharingId={sharingId}");
            RaiseSharingChanged(true, "교수자가 내 화면을 볼 수 있도록 공유를 시작했습니다.");
        }
        catch (Exception ex)
        {
            lock (_gate) _blockedTarget = target;
            _logSink.Write($"[Reverse] 역방향 공유 시작 실패: {ex.GetType().Name}");
            if (host is not null) await DisposeHostAsync(host).ConfigureAwait(false);
            await StopHostAsync(null).ConfigureAwait(false);
            RaiseSharingChanged(false, ex is NotSupportedException
                ? "이 PC에서는 내 화면 공유(Windows Desktop Sharing)를 지원하지 않습니다."
                : "내 화면 공유를 시작하지 못했습니다. 교수자 연결 상태를 확인해 주세요.");
        }
    }

    private async Task StopHostAsync(string? message)
    {
        IStudentShareHost? host;
        lock (_gate)
        {
            host = _host;
            _host = null;
            _hostTarget = null;
            _invitationId = Guid.Empty;
        }
        if (host is null) return;
        await DisposeHostAsync(host).ConfigureAwait(false);
        _logSink.Write("[Reverse] 역방향 공유 종료");
        if (message is not null) RaiseSharingChanged(false, message);
    }

    private async Task DisposeHostAsync(IStudentShareHost host)
    {
        try { await host.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logSink.Write($"[Reverse] 역방향 공유 호스트 종료 실패: {ex.GetType().Name}"); }
    }

    private void RaiseSharingChanged(bool sharing, string message)
    {
        try { SharingChanged?.Invoke(sharing, message); }
        catch (Exception ex) { _logSink.Write($"[Reverse] 공유 상태 알림 처리 실패: {ex.GetType().Name}"); }
    }
}
