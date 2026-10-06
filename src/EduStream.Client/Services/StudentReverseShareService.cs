using System.Security.Cryptography;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.ShareHost;

namespace EduStream.Client.Services;

/// <summary>학생 PC 역방향 공유 호스트. 테스트에서 실제 WDS 세션 없이 수명 규칙을 검증하기 위한 경계입니다.</summary>
public interface IStudentShareHost : IAsyncDisposable
{
    int ActiveViewerCount { get; }
    long ConnectionRevision { get; }
    Task ApplyControlAsync(string professorId, bool grant);
    Task<int?> GetControlLevelAsync(string professorId);
    Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<ReverseRdpInvitationNotice> CreateInvitationAsync(Guid sessionId, Guid sharingId, string professorId,
        Guid connectionId, string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
}

/// <summary>3번 <see cref="StudentDesktopHost"/>를 그대로 감쌉니다. 모니터를 지정하지 않으면 이 PC의 데스크톱 전체를 공유합니다.</summary>
public sealed class StudentDesktopShareHost : IStudentShareHost
{
    private readonly StudentDesktopHost _host;
    private long _connectionRevision;

    public StudentDesktopShareHost(string studentId)
    {
        _host = new StudentDesktopHost(studentId);
        _host.Session.AttendeeLifecycleChanged += (_, notice) =>
        {
            if (notice.Kind == ReverseAttendeeEventKind.Disconnected) Interlocked.Increment(ref _connectionRevision);
        };
    }
    public int ActiveViewerCount => _host.Session.ActiveAttendeeCount;
    public long ConnectionRevision => Interlocked.Read(ref _connectionRevision);
    public Task ApplyControlAsync(string professorId, bool grant) =>
        grant ? _host.GrantControlAsync(professorId) : _host.RevokeControlAsync(professorId);
    public Task<int?> GetControlLevelAsync(string professorId) => _host.Session.GetAttendeeControlLevelAsync(professorId);

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
/// 접속 중인 viewer는 초대 갱신으로 끊지 않습니다. 미접속 초대는 갱신하고 viewer 이탈 후에는 새 공유 세대로 자동 복구합니다.
/// 시작·초대가 실패하거나 거부되면 허용/대상이 바뀔 때까지 다시 시도하지 않습니다. 종료 실패는 성공으로 숨기지 않고 재시도합니다.
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
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _refreshLoop;
    private readonly Func<Task>? _beforeStart;
    private bool _allowViewing;
    private bool _localAllowViewing = true;
    private bool _disposed;
    private bool _stopFailed;
    // 실패/거부된 대상. 같은 대상으로 재시도를 반복하지 않는다.
    private ReverseRdpInvitationTarget? _blockedTarget;
    private IStudentShareHost? _host;
    private ReverseRdpInvitationTarget? _hostTarget;
    private Guid _invitationId;
    private Guid _sharingId;
    private DateTimeOffset _expiresAt;
    private long _hostRevision;
    private readonly Queue<Guid> _stoppedSharingOrder = new();
    private readonly HashSet<Guid> _stoppedSharings = new();

    /// <summary>공유 시작/종료 시 발생합니다(공유 중 여부, 사용자 안내 문구).</summary>
    public event Action<bool, string>? SharingChanged;

    public StudentReverseShareService(ReverseCollaborationClient reverse, ILogSink logSink,
        Func<string, IStudentShareHost>? hostFactory = null, Func<DateTimeOffset>? clock = null,
        bool enableAutoRefresh = true, Func<Task>? beforeStart = null)
    {
        _reverse = reverse ?? throw new ArgumentNullException(nameof(reverse));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _hostFactory = hostFactory ?? (studentId => new StudentDesktopShareHost(studentId));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _beforeStart = beforeStart;
        _reverse.InvitationRejected += OnInvitationRejected;
        _refreshLoop = enableAutoRefresh ? RefreshLoopAsync() : Task.CompletedTask;
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
            _disposed = true;
        }
        _reverse.InvitationRejected -= OnInvitationRejected;
        _stop.Cancel();
        await _refreshLoop.ConfigureAwait(false);
        await ReconcileAsync().ConfigureAwait(false);
        if (IsSharing) throw new InvalidOperationException("학생 화면 공유 종료를 확인하지 못했습니다.");
    }

    /// <summary>로컬 OFF는 서버 응답 전에 적용한다. ON은 이후 서버 스냅샷에서 다시 허용해야 시작한다.</summary>
    public Task SetLocalViewingAllowedAsync(bool allowed)
    {
        lock (_gate)
        {
            if (_localAllowViewing == allowed) return Task.CompletedTask;
            _localAllowViewing = allowed;
            _allowViewing = false;
            _blockedTarget = null;
        }
        return ReconcileAsync();
    }

    /// <summary>현재 권한을 변경하지 않고 초대 만료·viewer 이탈·실패한 종료를 재점검한다.</summary>
    public Task RefreshAsync() => ReconcileAsync();

    /// <summary>공유 수명과 같은 잠금에서 실제 native 적용 및 결과를 확인한다. #89 공통 규격만 사용한다.</summary>
    public async Task<RemoteInputResultNotice> ApplyInputAsync(RemoteInputCommandNotice command, bool allowControl)
    {
        var applied = false;
        var error = CollaborationError.PermissionDenied;
        await _order.WaitAsync().ConfigureAwait(false);
        try
        {
            RemoteInputRules.Validate(command);
            var target = _reverse.Target;
            if (_disposed || target is null || command.SessionId != target.SessionId ||
                command.ProfessorId != target.ProfessorId)
                error = CollaborationError.StaleConnection;
            else if (command.Action == RemoteInputAction.Revoke && _stoppedSharings.Contains(command.SharingId))
                applied = true; // 이 서비스가 실제 종료를 확인한 세대에만 멱등 성공.
            else if (command.SharingId != _sharingId || _host is null || _hostTarget != target)
                error = CollaborationError.StaleConnection;
            else if (command.Action == RemoteInputAction.Revoke || (allowControl && _allowViewing && _localAllowViewing && !_stopFailed))
            {
                var grant = command.Action == RemoteInputAction.Grant;
                await _host.ApplyControlAsync(target.ProfessorId, grant).ConfigureAwait(false);
                var level = await _host.GetControlLevelAsync(target.ProfessorId).ConfigureAwait(false);
                applied = grant ? level == ReverseSessionManager.ControlLevelInteractive :
                    level is null or ReverseSessionManager.ControlLevelView;
            }
        }
        catch (Exception ex) { _logSink.Write("[Reverse] 원격 입력 적용 실패: " + ex.GetType().Name); }
        finally { _order.Release(); }
        return new RemoteInputResultNotice(command.CommandId, command.SessionId, command.Action, applied, applied ? null : error);
    }

    public async Task RevokeCurrentInputAsync()
    {
        await _order.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_host is null || _hostTarget is null) return;
            await _host.ApplyControlAsync(_hostTarget.ProfessorId, false).ConfigureAwait(false);
            if (await _host.GetControlLevelAsync(_hostTarget.ProfessorId).ConfigureAwait(false) is not
                (null or ReverseSessionManager.ControlLevelView))
                throw new InvalidOperationException("실제 입력 차단 상태를 확인하지 못했습니다.");
        }
        finally { _order.Release(); }
    }

    private async Task RefreshLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                await ReconcileAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
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
                desired = _disposed || !_allowViewing || !_localAllowViewing ? null : _reverse.Target;
                blocked = _blockedTarget;
                current = _hostTarget;
            }
            if (desired is not null && desired == blocked) desired = null;
            if (desired is not null && desired == current && !_stopFailed)
            {
                var host = _host!;
                if (host.ActiveViewerCount == 0)
                {
                    if (host.ConnectionRevision != _hostRevision)
                    {
                        // native 엔진은 viewer 이탈 후 Disconnected 상태여서 같은 세대 초대를 만들 수 없다.
                        // 학생 허용 토글 없이 호스트를 정상 종료한 뒤 새 공유 세대로 복구한다.
                        if (await StopHostAsync(null).ConfigureAwait(false))
                            await StartHostAsync(desired).ConfigureAwait(false);
                    }
                    else if (_expiresAt <= _clock().AddMinutes(1))
                        await SendInvitationAsync(host, desired).ConfigureAwait(false);
                }
                return;
            }

            if (current is not null)
            {
                var message = desired is not null ? null
                    : current == blocked ? "교수자 앱이 화면 공유 초대를 받지 않아 공유를 중지했습니다."
                    : "내 화면 공유를 종료했습니다.";
                if (!await StopHostAsync(message).ConfigureAwait(false)) return;
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
            if (_beforeStart is not null) await _beforeStart().ConfigureAwait(false);
            host = _hostFactory(target.StudentId);
            lock (_gate)
            {
                // 시작/종료가 실패해도 정리할 호스트 참조를 잃지 않는다.
                _host = host;
                _hostTarget = target;
            }
            _sharingId = await host.StartAsync(target.SessionId).ConfigureAwait(false);
            await SendInvitationAsync(host, target).ConfigureAwait(false);
            _logSink.Write($"[Reverse] 역방향 공유 시작: sharingId={_sharingId}");
            RaiseSharingChanged(true, "교수자가 내 화면을 볼 수 있도록 공유를 시작했습니다.");
        }
        catch (Exception ex)
        {
            lock (_gate) _blockedTarget = target;
            _logSink.Write($"[Reverse] 역방향 공유 시작 실패: {ex.GetType().Name}");
            if (!await StopHostAsync(null).ConfigureAwait(false)) return;
            RaiseSharingChanged(false, ex is NotSupportedException
                ? "이 PC에서는 내 화면 공유(Windows Desktop Sharing)를 지원하지 않습니다."
                : "내 화면 공유를 시작하지 못했습니다. 교수자 연결 상태를 확인해 주세요.");
        }
    }

    private async Task SendInvitationAsync(IStudentShareHost host, ReverseRdpInvitationTarget target)
    {
        var revision = host.ConnectionRevision;
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var invitation = await host.CreateInvitationAsync(target.SessionId, _sharingId, target.ProfessorId,
            target.ConnectionId, password, _clock() + InvitationLifetime).ConfigureAwait(false);
        var secret = new ReverseRdpInvitationSecretNotice(target.SessionId, _sharingId, invitation.InvitationId,
            target.ConnectionId, target.StudentId, target.ProfessorId, password, invitation.ExpiresAt);
        lock (_gate) _invitationId = invitation.InvitationId;
        await _reverse.SendInvitationAsync(invitation, secret).ConfigureAwait(false);
        _expiresAt = invitation.ExpiresAt;
        _hostRevision = revision;
    }

    private async Task<bool> StopHostAsync(string? message)
    {
        IStudentShareHost? host;
        lock (_gate) host = _host;
        if (host is null) return true;
        try { await host.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _stopFailed = true;
            _logSink.Write($"[Reverse] 역방향 공유 호스트 종료 실패: {ex.GetType().Name}");
            RaiseSharingChanged(true, "내 화면 공유 종료를 확인하지 못했습니다. 종료를 재시도합니다.");
            return false;
        }
        lock (_gate)
        {
            _host = null;
            _hostTarget = null;
            _invitationId = Guid.Empty;
            if (_sharingId != Guid.Empty && _stoppedSharings.Add(_sharingId))
            {
                _stoppedSharingOrder.Enqueue(_sharingId);
                if (_stoppedSharingOrder.Count > 256) _stoppedSharings.Remove(_stoppedSharingOrder.Dequeue());
            }
            _sharingId = Guid.Empty;
            _stopFailed = false;
        }
        _logSink.Write("[Reverse] 역방향 공유 종료");
        if (message is not null) RaiseSharingChanged(false, message);
        return true;
    }

    private void RaiseSharingChanged(bool sharing, string message)
    {
        try { SharingChanged?.Invoke(sharing, message); }
        catch (Exception ex) { _logSink.Write($"[Reverse] 공유 상태 알림 처리 실패: {ex.GetType().Name}"); }
    }
}
