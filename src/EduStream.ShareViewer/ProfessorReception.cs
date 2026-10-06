using System.Linq.Expressions;
using System.Reflection;

namespace EduStream.ShareViewer;

/// <summary>
/// 교수자 PC에서 학생 화면을 수신합니다.
/// 데스크톱 공유 세션은 열지 않습니다. 학생 PC가 만든 연결 문자열로 뷰어만 붙입니다.
/// </summary>
public sealed class ProfessorReception : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<string, ProfessorViewerConnection> _viewers = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ProfessorViewerConnection Watch(string studentId, System.Windows.Forms.Control viewer, string connectionString, string professorName, string password)
    {
        if (string.IsNullOrWhiteSpace(studentId))
            throw new ArgumentException("학생 ID가 필요합니다.", nameof(studentId));
        ArgumentNullException.ThrowIfNull(viewer);
        lock (_gate)
        {
            if (_viewers.ContainsKey(studentId))
                throw new InvalidOperationException("이 학생의 수신 뷰어가 이미 있습니다.");
            var connection = new ProfessorViewerConnection(viewer);
            _viewers.Add(studentId, connection);
            try
            {
                connection.Connect(connectionString, professorName, password);
            }
            catch (Exception connectError)
            {
                try
                {
                    connection.ReleaseAfterSharingStopped();
                }
                catch (Exception releaseError)
                {
                    // 미해제 뷰어를 잃지 않는다. 호출자는 Release(studentId)로 정리를 재시도한다.
                    throw new AggregateException("뷰어 연결과 실패 정리가 모두 실패했습니다.", connectError, releaseError);
                }
                _viewers.Remove(studentId);
                throw;
            }
            return connection;
        }
    }

    public void Release(string studentId)
    {
        ProfessorViewerConnection? connection;
        lock (_gate)
        {
            if (!_viewers.TryGetValue(studentId, out connection))
                return;
        }

        connection.ReleaseAfterSharingStopped();
        RemoveReleased(studentId, connection);
    }

    public async Task ReleaseAsync(string studentId, CancellationToken cancellationToken = default)
    {
        ProfessorViewerConnection? connection;
        lock (_gate)
        {
            if (!_viewers.TryGetValue(studentId, out connection)) return;
        }
        await connection.ReleaseAfterSharingStoppedAsync(cancellationToken).ConfigureAwait(false);
        RemoveReleased(studentId, connection);
    }

    private void RemoveReleased(string studentId, ProfessorViewerConnection connection)
    {
        lock (_gate)
        {
            if (_viewers.TryGetValue(studentId, out var current) && ReferenceEquals(current, connection))
                _viewers.Remove(studentId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        string[] students;
        lock (_gate) students = _viewers.Keys.ToArray();
        var errors = new List<Exception>();
        foreach (var studentId in students)
        {
            try { await ReleaseAsync(studentId).ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateException(errors);
    }

    public void Dispose()
    {
        string[] students;
        lock (_gate) students = _viewers.Keys.ToArray();

        var errors = new List<Exception>();
        foreach (var studentId in students)
        {
            try { Release(studentId); }
            catch (Exception ex) { errors.Add(ex); }
        }

        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateException(errors);
    }
}

/// <summary>
/// 교수자 AxRDPViewer 한 개의 연결 수명.
/// 이미 실패하거나 끊긴 연결에는 Disconnect 를 다시 호출하지 않습니다.
/// </summary>
public sealed class ProfessorViewerConnection : IDisposable, IAsyncDisposable
{
    private readonly System.Windows.Forms.Control _viewer;
    private readonly List<(EventInfo Event, Delegate Handler)> _hooks = new();
    private bool _established;
    private bool _failed;
    private bool _terminated;
    private bool _released;
    private bool _connectStarted;
    private bool _disconnectRequested;
    private readonly TaskCompletionSource _connectionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _releaseLock = new(1, 1);

    public ProfessorViewerConnection(System.Windows.Forms.Control viewer)
    {
        _viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));
        Listen("OnConnectionEstablished", () => _established = true);
        Listen("OnConnectionFailed", () => { _failed = true; _connectionEnded.TrySetResult(); });
        Listen("OnConnectionTerminated", () => { _terminated = true; _connectionEnded.TrySetResult(); });
        Listen("OnError", () => _failed = true);
    }

    public System.Windows.Forms.Control Viewer => _viewer;
    public bool Established => _established;
    public bool Failed => _failed;
    public bool Terminated => _terminated;

    public bool IsConnectionLive
    {
        get
        {
            if (_viewer.IsDisposed) return false;
            return _established && !_failed && !_terminated && _viewer.IsHandleCreated;
        }
    }

    public void Connect(string connectionString, string name, string password)
    {
        if (_released) throw new ObjectDisposedException(nameof(ProfessorViewerConnection));
        RunOnViewerThread(() =>
        {
            if (_connectStarted) throw new InvalidOperationException("이 뷰어는 이미 연결을 시작했습니다. 재접속에는 새 뷰어를 사용하세요.");
            ((dynamic)_viewer).Connect(connectionString, name, password);
            _connectStarted = true;
        });
    }

    /// <summary>
    /// 공유 세션이 끝난 뒤에 호출합니다.
    /// 아직 native 연결 종료 이벤트가 오지 않았다면 비동기 해제를 사용해야 합니다.
    /// UI 스레드를 동기 대기로 막거나 종료되지 않은 뷰어를 폐기하지 않습니다.
    /// </summary>
    public void ReleaseAfterSharingStopped()
    {
        if (_released) return;
        if (_viewer.IsDisposed)
        {
            _hooks.Clear();
            _released = true;
            return;
        }

        RunOnViewerThread(() =>
        {
            // 종료 이벤트 핸들러 안에서 동기 Dispose를 호출해 native 콜백 중 컨트롤을
            // 없애는 경우도 차단한다. 연결을 시작한 컨트롤은 항상 UI 큐를 거치는 비동기 경로를 쓴다.
            if (_connectStarted && _viewer.IsHandleCreated)
                throw new InvalidOperationException("연결을 시작한 WDS 뷰어는 ReleaseAfterSharingStoppedAsync로 해제하세요.");
            ReleaseOnViewerThread();
        });
    }

    /// <summary>
    /// Connect/Disconnect 반환은 native 종료 완료가 아닙니다. 실패/종료 이벤트를 기다린 뒤
    /// UI 큐에 해제를 게시하여 이벤트 콜백이 빠져나온 다음에만 컨트롤을 폐기합니다.
    /// 시간 초과/취소에는 뷰어와 이벤트 구독을 보존합니다. 컨트롤 해제 실패도 완료로 표시하지 않아 재시도할 수 있습니다.
    /// </summary>
    public async Task ReleaseAfterSharingStoppedAsync(CancellationToken cancellationToken = default)
    {
        await _releaseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var waitForEnd = false;
            await OnViewerThreadAsync(() =>
            {
                if (_released || _viewer.IsDisposed) return;
                waitForEnd = NeedsDisconnectConfirmation;
                if (waitForEnd && !_disconnectRequested)
                {
                    ((dynamic)_viewer).Disconnect();
                    _disconnectRequested = true;
                }
            }).ConfigureAwait(false);
            if (waitForEnd)
                await _connectionEnded.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await OnViewerThreadAsync(ReleaseOnViewerThread, alwaysPost: true).ConfigureAwait(false);
        }
        finally { _releaseLock.Release(); }
    }

    private bool NeedsDisconnectConfirmation =>
        _connectStarted && _viewer.IsHandleCreated && !_connectionEnded.Task.IsCompleted;

    private void ReleaseOnViewerThread()
    {
        if (_released) return;
        if (_viewer.IsDisposed)
        {
            _hooks.Clear();
            _released = true;
            return;
        }

        if (NeedsDisconnectConfirmation)
            throw new InvalidOperationException("WDS 연결 종료 확인이 필요합니다. ReleaseAfterSharingStoppedAsync를 사용하세요.");

        Unlisten();

        _viewer.Parent?.Controls.Remove(_viewer);
        if (_viewer.Parent != null)
            throw new InvalidOperationException("뷰어 컨트롤이 부모에서 떨어지지 않았습니다.");

        _viewer.Dispose();
        if (!_viewer.IsDisposed)
            throw new InvalidOperationException("뷰어 컨트롤이 해제되지 않았습니다.");

        _released = true;
    }

    public void Dispose() => ReleaseAfterSharingStopped();

    public ValueTask DisposeAsync() => new(ReleaseAfterSharingStoppedAsync());

    private Task OnViewerThreadAsync(Action action, bool alwaysPost = false)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run()
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        try
        {
            if (!_viewer.IsDisposed && _viewer.IsHandleCreated && (alwaysPost || _viewer.InvokeRequired))
                _viewer.BeginInvoke((Action)Run);
            else Run();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }

    private void RunOnViewerThread(Action action)
    {
        if (_viewer.InvokeRequired)
            _viewer.Invoke(action);
        else
            action();
    }

    private void Listen(string eventName, Action callback)
    {
        var evt = _viewer.GetType().GetEvent(eventName);
        if (evt?.EventHandlerType?.GetMethod("Invoke") is not { } invoke)
            return;

        var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
        var call = Expression.Call(Expression.Constant(callback), typeof(Action).GetMethod(nameof(Action.Invoke))!);
        var handler = Expression.Lambda(evt.EventHandlerType, call, parameters).Compile();
        evt.AddEventHandler(_viewer, handler);
        _hooks.Add((evt, handler));
    }

    private void Unlisten()
    {
        foreach (var (evt, handler) in _hooks)
            evt.RemoveEventHandler(_viewer, handler);
        _hooks.Clear();
    }
}
