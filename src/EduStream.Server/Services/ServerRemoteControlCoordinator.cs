using EduStream.Core.Collaboration;
using EduStream.Core.Logging;

namespace EduStream.Server.Services;

/// <summary>
/// 2번 구현: 교수자가 선택한 학생 한 명에게만 제어 요청을 직렬화합니다.
/// 실제 native 입력 허용/차단은 3번이 IRemoteInputGate로 수행하며, 이 클래스는 그 결과를 기다려
/// Active 표시와 대상 전환 순서를 보장합니다.
/// </summary>
/// <remarks>
/// 순서 규칙:
/// 1) 회수(중지·대상 전환·권한 변경·연결 제거)는 승인 상태를 즉시 Revoked로 바꾸고 입력 회수 대기열에 넣습니다.
/// 2) 새 요청은 대기열의 입력 회수가 모두 확인된 뒤에만 Requested가 됩니다.
/// 3) 회수를 기다리는 동안 중지/다른 요청이 들어오면 그 요청은 폐기되어 Requested로 남지 않습니다.
/// 4) Active는 IRemoteInputGate.GrantAsync 완료 후, 요청이 여전히 최신이고 권한 revision이 같을 때만 적용합니다.
/// 공유 재시작 후 제어 자동 재승인은 하지 않습니다(협의 필요 항목).
/// </remarks>
public sealed class ServerRemoteControlCoordinator : IRemoteControlCoordinator, IDisposable
{
    private readonly ParticipantConnection _professor;
    private readonly ParticipantRegistry _registry;
    private readonly ILogSink _logSink;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _revokeLock = new(1, 1);
    private readonly List<RemoteControlState> _pendingInputRevokes = new();
    private IRemoteInputGate _inputGate;
    private RemoteControlState? _current;
    private CancellationTokenSource? _grantCancellation;
    // 회수·새 요청마다 증가합니다. 비동기 대기 전후로 값을 비교해 대체된 요청을 버립니다.
    private long _version;
    private bool _disposed;

    public ServerRemoteControlCoordinator(ParticipantConnection professor, ParticipantRegistry registry,
        IRemoteInputGate inputGate, ILogSink logSink)
    {
        professor.Validate();
        if (professor.Role != ParticipantRole.Professor)
            throw new CollaborationException(CollaborationError.NotAuthorized);
        _professor = professor;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _inputGate = inputGate ?? throw new ArgumentNullException(nameof(inputGate));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));

        _registry.PermissionsChanged += OnPermissionsChanged;
        _registry.ConnectionRemoved += OnConnectionRemoved;
    }

    public RemoteControlState? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// 승인은 회수했지만 3번의 실제 입력 차단 확인이 아직 끝나지 않은 요청이 있으면 true입니다.
    /// 이 동안 새 대상 요청은 회수 확인을 먼저 다시 시도합니다.
    /// </summary>
    public bool IsInputRevokePending
    {
        get { lock (_gate) return _pendingInputRevokes.Count > 0; }
    }

    /// <summary>
    /// 상태 전이 순서를 보장하기 위해 내부 잠금 안에서 발생합니다.
    /// 핸들러는 빠르게 반환해야 하며 다른 스레드의 작업을 동기 대기하면 안 됩니다.
    /// </summary>
    public event Action<RemoteControlState>? StateChanged;

    /// <summary>
    /// 3번 입력 엔진을 연결합니다. 진행 중인 제어나 회수 확인 대기가 있으면 교체할 수 없습니다.
    /// </summary>
    public void SetInputGate(IRemoteInputGate inputGate)
    {
        ArgumentNullException.ThrowIfNull(inputGate);
        lock (_gate)
        {
            if (_current?.Phase is ControlPhase.Requested or ControlPhase.Active || _pendingInputRevokes.Count > 0)
                throw new InvalidOperationException("원격 제어가 진행 중이거나 입력 회수 확인 대기 중에는 입력 엔진을 바꿀 수 없습니다.");
            _inputGate = inputGate;
        }
    }

    /// <summary>
    /// 지정한 학생에게 제어를 요청합니다. 기존 대상은 먼저 회수하고 실제 입력 회수 확인을 기다립니다.
    /// 기다리는 동안 중지나 다른 요청으로 대체되면 아무 상태도 남기지 않고 반환합니다.
    /// 입력 허용이 끝나면 Active가 되며, 허용 실패는 Failed로 표시한 뒤 예외를 전달합니다.
    /// </summary>
    public async Task RequestAsync(ParticipantConnection target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        long expectedVersion;
        CancellationTokenSource? replacedGrant;
        lock (_gate)
        {
            ThrowIfDisposed();
            WithdrawLocked(out replacedGrant);
            expectedVersion = _version;
        }
        replacedGrant?.Cancel();

        // 이전 대상의 실제 입력 차단이 확인되기 전에는 새 대상을 요청하지 않는다.
        await ConfirmInputRevokedAsync(cancellationToken);

        RemoteControlState requested;
        IRemoteInputGate inputGate;
        CancellationTokenSource grantCancellation;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_version != expectedVersion) return;

            var snapshot = _registry.TryResolve(target.ConnectionId);
            if (snapshot is null || snapshot.Connection != target)
                throw new CollaborationException(CollaborationError.StaleConnection);

            requested = RemoteControlState.Request(_professor, snapshot, Guid.NewGuid());
            grantCancellation = new CancellationTokenSource();
            _grantCancellation = grantCancellation;
            _version++;
            expectedVersion = _version;
            _current = requested;
            inputGate = _inputGate;
            StateChanged?.Invoke(requested);
        }

        try
        {
            await inputGate.GrantAsync(requested, grantCancellation.Token);
        }
        catch (Exception exception)
        {
            var stillCurrent = false;
            lock (_gate)
            {
                if (_version == expectedVersion && _current == requested)
                {
                    stillCurrent = true;
                    _grantCancellation = null;
                    _version++;
                    _current = requested.Fail();
                    // 부분 허용 가능성을 배제할 수 없으므로 실패한 요청도 입력 회수 대상에 넣는다.
                    _pendingInputRevokes.Add(requested.Revoke());
                    StateChanged?.Invoke(_current);
                }
            }
            await TryConfirmInputRevokedAsync("허용 실패");
            if (!stillCurrent) return;
            _logSink.Write($"[Control] 입력 허용 실패: requestId={requested.RequestId}, 사유={exception.GetType().Name}");
            throw;
        }

        CollaborationException? activationFailure = null;
        lock (_gate)
        {
            if (_version != expectedVersion || _current != requested)
            {
                // 허용 완료 전에 회수됐다. 회수 확인이 허용보다 먼저 끝났을 수 있으므로 한 번 더 차단한다.
                _pendingInputRevokes.Add(requested.Revoke());
            }
            else
            {
                _grantCancellation = null;
                var snapshot = _registry.TryResolve(requested.Student.ConnectionId);
                try
                {
                    if (snapshot is null) throw new CollaborationException(CollaborationError.StaleConnection);
                    _current = requested.Activate(requested.RequestId, snapshot);
                }
                catch (CollaborationException exception)
                {
                    activationFailure = exception;
                    _version++;
                    _current = requested.Revoke();
                    _pendingInputRevokes.Add(_current);
                }
                StateChanged?.Invoke(_current);
            }
        }

        await TryConfirmInputRevokedAsync("허용 후 대체");
        if (activationFailure is not null) throw activationFailure;
    }

    /// <summary>
    /// 현재 제어 대상을 회수하고 실제 입력 차단 확인까지 기다립니다.
    /// 진행 중인 요청이 회수 확인을 기다리는 중이었다면 그 요청도 폐기됩니다.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CancellationTokenSource? replacedGrant;
        lock (_gate)
        {
            WithdrawLocked(out replacedGrant);
        }
        replacedGrant?.Cancel();

        await ConfirmInputRevokedAsync(cancellationToken);
    }

    /// <summary>
    /// 회수 대기열에 남은 요청의 실제 입력 차단 확인을 기다립니다. 확인이 실패하면 예외를 전달하고
    /// 대기열에 남겨 다음 요청/중지 때 다시 시도합니다.
    /// </summary>
    public async Task ConfirmInputRevokedAsync(CancellationToken cancellationToken = default)
    {
        await _revokeLock.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                RemoteControlState revoked;
                IRemoteInputGate inputGate;
                lock (_gate)
                {
                    if (_pendingInputRevokes.Count == 0) return;
                    revoked = _pendingInputRevokes[0];
                    inputGate = _inputGate;
                }

                await inputGate.RevokeAsync(revoked, cancellationToken);

                lock (_gate) _pendingInputRevokes.Remove(revoked);
            }
        }
        finally
        {
            _revokeLock.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _registry.PermissionsChanged -= OnPermissionsChanged;
        _registry.ConnectionRemoved -= OnConnectionRemoved;
    }

    private void OnPermissionsChanged(ParticipantSnapshot snapshot)
    {
        // 대상 학생의 허용이 바뀌면(OFF·철회 포함) revision이 달라지므로 기존 승인은 더 이상 유효하지 않다.
        WithdrawForRegistryChange(
            current => current.Student.ConnectionId == snapshot.Connection.ConnectionId &&
                       current.PermissionRevision != snapshot.PermissionRevision,
            "권한 변경");
    }

    private void OnConnectionRemoved(ParticipantConnection connection)
    {
        WithdrawForRegistryChange(
            current => current.Student.ConnectionId == connection.ConnectionId,
            "연결 제거");
    }

    private void WithdrawForRegistryChange(Func<RemoteControlState, bool> affectsCurrent, string reason)
    {
        CancellationTokenSource? replacedGrant;
        lock (_gate)
        {
            if (_disposed || _current?.Phase is not (ControlPhase.Requested or ControlPhase.Active) ||
                !affectsCurrent(_current))
                return;
            WithdrawLocked(out replacedGrant);
        }
        replacedGrant?.Cancel();
        _logSink.Write($"[Control] 회수: 사유={reason}");

        // 레지스트리 이벤트는 동기이므로 실제 입력 차단 확인은 기다리지 않고 바로 시작한다.
        // 호출자가 결과를 기다려야 하면 ConfirmInputRevokedAsync를 이어서 호출한다.
        _ = TryConfirmInputRevokedAsync(reason);
    }

    /// <summary>
    /// 승인 상태를 즉시 회수하고 입력 회수 대기열에 넣습니다. 진행 중인 요청을 무효화하기 위해
    /// 회수할 대상이 없어도 version은 항상 증가합니다. _gate 안에서만 호출합니다.
    /// </summary>
    private void WithdrawLocked(out CancellationTokenSource? replacedGrant)
    {
        _version++;
        replacedGrant = _grantCancellation;
        _grantCancellation = null;

        if (_current?.Phase is ControlPhase.Requested or ControlPhase.Active)
        {
            _current = _current.Revoke();
            _pendingInputRevokes.Add(_current);
            StateChanged?.Invoke(_current);
        }
    }

    private async Task TryConfirmInputRevokedAsync(string reason)
    {
        try
        {
            await ConfirmInputRevokedAsync();
        }
        catch (Exception exception)
        {
            _logSink.Write($"[Control] 입력 회수 확인 실패: 사유={reason}, 오류={exception.GetType().Name}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new CollaborationException(CollaborationError.SessionClosed);
    }
}
