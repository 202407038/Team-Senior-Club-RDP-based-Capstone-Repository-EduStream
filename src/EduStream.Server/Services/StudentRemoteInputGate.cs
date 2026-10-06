using EduStream.Core.Collaboration;
using EduStream.Core.Logging;

namespace EduStream.Server.Services;

/// <summary>
/// 2번 구현: 다른 PC 학생의 실제 입력 허용/회수 게이트. 대상 학생의 보호 채널로 Kind 18을 보내고,
/// 같은 연결에서 온 Kind 19 적용 결과를 받은 뒤에만 완료합니다. 교수자 PC의 로컬 입력이나 모의 성공으로 대체하지 않습니다.
/// </summary>
/// <remarks>
/// 허용은 교수자가 지금 볼 수 있는 학생 공유 세대(<c>currentSharingFor</c>)가 있을 때만 보냅니다. 회수는 그 허용을 보낸 세대로 보냅니다.
/// 학생 연결이 이미 사라졌으면 회수는 성공으로 봅니다. 학생 앱은 연결이 끊기면 자기 PC의 공유 호스트를 닫기 때문입니다.
/// 응답 시간 초과는 실패이며, 회수 실패는 조정자가 대기열에 남겨 다음 요청/중지 때 다시 시도합니다.
/// </remarks>
public sealed class StudentRemoteInputGate : IRemoteInputGate, IDisposable
{
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(10);

    private sealed record PendingCommand(ParticipantConnection Student, RemoteInputAction Action,
        TaskCompletionSource<RemoteInputResultNotice> Completion);

    private readonly object _gate = new();
    private readonly string _professorId;
    private readonly ParticipantRegistry _registry;
    private readonly Func<ParticipantConnection, ICollaborationChannel?> _channelFor;
    private readonly Func<ParticipantConnection, Guid?> _currentSharingFor;
    private readonly ILogSink _logSink;
    private readonly TimeSpan _responseTimeout;
    private readonly Dictionary<Guid, PendingCommand> _pending = new(); // CommandId → 응답 대기
    private readonly Dictionary<Guid, Guid> _grantedSharings = new(); // 제어 RequestId → 허용을 보낸 공유 세대
    private bool _disposed;

    public StudentRemoteInputGate(string professorId, ParticipantRegistry registry,
        Func<ParticipantConnection, ICollaborationChannel?> channelFor, Func<ParticipantConnection, Guid?> currentSharingFor,
        ILogSink logSink, TimeSpan? responseTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(professorId)) throw new ArgumentException("교수자 ID가 필요합니다.", nameof(professorId));
        _professorId = professorId;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _channelFor = channelFor ?? throw new ArgumentNullException(nameof(channelFor));
        _currentSharingFor = currentSharingFor ?? throw new ArgumentNullException(nameof(currentSharingFor));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _responseTimeout = responseTimeout ?? DefaultResponseTimeout;
        _registry.ConnectionRemoved += OnConnectionRemoved;
    }

    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    public async Task GrantAsync(RemoteControlState requested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        cancellationToken.ThrowIfCancellationRequested();
        // 교수자가 볼 수 없는 화면은 조작하게 두지 않는다.
        var sharingId = _currentSharingFor(requested.Student)
                        ?? throw new CollaborationException(CollaborationError.StaleConnection);
        var channel = _channelFor(requested.Student)
                      ?? throw new CollaborationException(CollaborationError.StaleConnection);
        lock (_gate)
        {
            ThrowIfDisposed();
            // 응답 전 취소·시간 초과여도 학생이 이미 적용했을 수 있으므로 회수 대상으로 먼저 기록한다.
            _grantedSharings[requested.RequestId] = sharingId;
        }
        var result = await SendAndWaitAsync(channel, requested, sharingId, RemoteInputAction.Grant, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Applied)
        {
            // 학생이 적용하지 못했다고 답했으면 열린 입력이 없다.
            Forget(requested.RequestId);
            throw new CollaborationException(result.Error ?? CollaborationError.UnsupportedCapability);
        }
        _logSink.Write($"[Control] 학생 PC 입력 허용 확인: request={requested.RequestId}");
    }

    public async Task RevokeAsync(RemoteControlState revoked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        Guid sharingId;
        lock (_gate)
        {
            // 허용을 보낸 적이 없으면 학생 PC에서 열린 입력도 없다.
            if (!_grantedSharings.TryGetValue(revoked.RequestId, out sharingId)) return;
        }
        var channel = _channelFor(revoked.Student);
        if (channel is null)
        {
            Forget(revoked.RequestId);
            _logSink.Write($"[Control] 학생 연결 종료로 입력 회수 완료 처리: request={revoked.RequestId}");
            return;
        }
        var result = await SendAndWaitAsync(channel, revoked, sharingId, RemoteInputAction.Revoke, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Applied)
            throw new CollaborationException(result.Error ?? CollaborationError.UnsupportedCapability);
        Forget(revoked.RequestId);
        _logSink.Write($"[Control] 학생 PC 입력 회수 확인: request={revoked.RequestId}");
    }

    /// <summary>
    /// 학생 보호 채널에서 받은 Kind 19를 대기 중인 명령에 연결합니다. 다른 연결·다른 동작·이미 끝난 명령의 응답이면 false입니다.
    /// </summary>
    public bool HandleResult(ParticipantConnection sender, RemoteInputResultNotice result)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(result);
        PendingCommand? pending;
        lock (_gate)
        {
            if (!_pending.TryGetValue(result.CommandId, out pending) || pending.Student != sender ||
                pending.Action != result.Action || result.SessionId != sender.SessionId)
                pending = null;
        }
        if (pending is null)
        {
            _logSink.Write($"[Control] 입력 적용 결과 무시: command={result.CommandId}, connection={sender.ConnectionId}");
            return false;
        }
        return pending.Completion.TrySetResult(result);
    }

    public void Dispose()
    {
        PendingCommand[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _pending.Values.ToArray();
            _pending.Clear();
            _grantedSharings.Clear();
        }
        _registry.ConnectionRemoved -= OnConnectionRemoved;
        foreach (var command in pending)
            command.Completion.TrySetException(new CollaborationException(CollaborationError.SessionClosed));
    }

    private async Task<RemoteInputResultNotice> SendAndWaitAsync(ICollaborationChannel channel, RemoteControlState state,
        Guid sharingId, RemoteInputAction action, CancellationToken cancellationToken)
    {
        var command = new RemoteInputCommandNotice(Guid.NewGuid(), state.Student.SessionId, sharingId, state.RequestId,
            _professorId, action);
        var completion = new TaskCompletionSource<RemoteInputResultNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ThrowIfDisposed();
            _pending[command.CommandId] = new PendingCommand(state.Student, action, completion);
        }
        try
        {
            await channel.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), command), cancellationToken)
                .ConfigureAwait(false);
            return await completion.Task.WaitAsync(_responseTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logSink.Write($"[Control] 학생 PC 입력 {action} 응답 시간 초과: request={state.RequestId}");
            throw;
        }
        finally
        {
            lock (_gate) _pending.Remove(command.CommandId);
        }
    }

    private void OnConnectionRemoved(ParticipantConnection connection)
    {
        PendingCommand[] closed;
        lock (_gate)
        {
            closed = _pending.Values.Where(command => command.Student == connection).ToArray();
        }
        foreach (var command in closed)
        {
            // 연결이 끊긴 학생은 공유 호스트를 닫으므로 회수는 성공, 허용은 실패로 끝낸다.
            if (command.Action == RemoteInputAction.Revoke)
                command.Completion.TrySetResult(new RemoteInputResultNotice(Guid.NewGuid(), connection.SessionId,
                    RemoteInputAction.Revoke, true, null));
            else
                command.Completion.TrySetException(new CollaborationException(CollaborationError.StaleConnection));
        }
    }

    private void Forget(Guid requestId)
    {
        lock (_gate) _grantedSharings.Remove(requestId);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new CollaborationException(CollaborationError.SessionClosed);
    }
}
