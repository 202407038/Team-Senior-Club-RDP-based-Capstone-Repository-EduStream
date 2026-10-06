using System.Linq.Expressions;
using System.Reflection;
using Forms = System.Windows.Forms;

namespace EduStream.Server.Rdp;

/// <summary>
/// 학생 한 명의 역방향 호스트와, 그 화면을 보는 교수자 뷰어들의 수명.
/// 앱(2번 세션, 5번 창)은 이 객체를 켜고 끄기만 하면 됩니다. 학생 앱이 Server 프로젝트를 참조하지 않아도 됩니다.
/// 거절·실패한 뷰어는 공유 세션을 끝낸 뒤에만 해제합니다.
/// </summary>
public sealed class ReverseStudentStation : IAsyncDisposable
{
    private readonly List<ProfessorViewerConnection> _viewers = new();
    private readonly object _gate = new();

    public ReverseStudentStation(string studentId, ReverseSessionManager? host = null)
    {
        if (string.IsNullOrWhiteSpace(studentId))
            throw new ArgumentException("학생 ID가 필요합니다.", nameof(studentId));
        StudentId = studentId;
        Host = host ?? new ReverseSessionManager();
    }

    public string StudentId { get; }
    public ReverseSessionManager Host { get; }
    public MonitorInfo? SharedMonitor => Host.SharedMonitor;

    public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Host.StartReverseSharingAsync(sessionId, StudentId, cancellationToken);

    public Task<Guid> StartAsync(Guid sessionId, MonitorInfo shareMonitor, CancellationToken cancellationToken = default)
        => Host.StartReverseSharingAsync(sessionId, StudentId, shareMonitor, cancellationToken);

    /// <summary>이 학생 화면용 뷰어를 붙입니다. 컨트롤을 만든 UI 스레드에서 호출합니다.</summary>
    public ProfessorViewerConnection ConnectProfessor(Forms.Control viewer, string connectionString, string name, string password)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var connection = new ProfessorViewerConnection(viewer);
        lock (_gate) _viewers.Add(connection);
        connection.Connect(connectionString, name, password);
        return connection;
    }

    /// <summary>공유 세션을 먼저 끝낸 다음, 붙어 있던 뷰어를 해제합니다.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await Host.StopReverseSharingAsync(cancellationToken).ConfigureAwait(false);
        ProfessorViewerConnection[] viewers;
        lock (_gate)
        {
            viewers = _viewers.ToArray();
            _viewers.Clear();
        }

        foreach (var viewer in viewers)
            viewer.ReleaseAfterSharingStopped();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

/// <summary>
/// 학생마다 독립된 호스트를 둡니다. 한 매니저를 두 학생에게 다시 쓰지 않습니다.
/// </summary>
public sealed class ReverseClassroomPlacement : IAsyncDisposable
{
    private readonly Dictionary<string, ReverseStudentStation> _stations = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ReverseStudentStation StationFor(string studentId)
    {
        lock (_gate)
        {
            if (!_stations.TryGetValue(studentId, out var station))
            {
                station = new ReverseStudentStation(studentId);
                _stations.Add(studentId, station);
            }
            return station;
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        ReverseStudentStation[] stations;
        lock (_gate)
        {
            stations = _stations.Values.ToArray();
            _stations.Clear();
        }

        foreach (var station in stations)
            await station.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}

/// <summary>
/// 교수자 AxRDPViewer 한 개의 연결 수명.
/// 이미 실패하거나 끊긴 연결에는 Disconnect 를 다시 호출하지 않습니다.
/// </summary>
public sealed class ProfessorViewerConnection : IDisposable
{
    private readonly Forms.Control _viewer;
    private readonly List<(EventInfo Event, Delegate Handler)> _hooks = new();
    private bool _established;
    private bool _failed;
    private bool _terminated;
    private bool _released;

    public ProfessorViewerConnection(Forms.Control viewer)
    {
        _viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));
        Listen("OnConnectionEstablished", () => _established = true);
        Listen("OnConnectionFailed", () => _failed = true);
        Listen("OnConnectionTerminated", () => _terminated = true);
        Listen("OnError", () => _failed = true);
    }

    public Forms.Control Viewer => _viewer;
    public bool Established => _established;
    public bool Failed => _failed;
    public bool Terminated => _terminated;

    public bool IsConnectionLive
    {
        get
        {
            try
            {
                return _established && !_failed && !_terminated && _viewer.IsHandleCreated && !_viewer.IsDisposed;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Connect(string connectionString, string name, string password)
    {
        if (_released) throw new ObjectDisposedException(nameof(ProfessorViewerConnection));
        RunOnViewerThread(() => ((dynamic)_viewer).Connect(connectionString, name, password));
    }

    /// <summary>공유 세션이 끝난 뒤에만 호출합니다. 살아 있는 연결만 Disconnect 하고 컨트롤을 해제합니다.</summary>
    public void ReleaseAfterSharingStopped()
    {
        if (_released) return;
        _released = true;
        Unlisten();

        if (IsConnectionLive)
        {
            try { RunOnViewerThread(() => ((dynamic)_viewer).Disconnect()); }
            catch { /* 이미 끊김 */ }
        }

        try
        {
            if (!_viewer.IsDisposed)
            {
                _viewer.Parent?.Controls.Remove(_viewer);
                _viewer.Dispose();
            }
        }
        catch { /* 표시 컨트롤 이미 해제 */ }
    }

    public void Dispose() => ReleaseAfterSharingStopped();

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
        {
            try { evt.RemoveEventHandler(_viewer, handler); }
            catch { /* 컨트롤이 이미 해제됨 */ }
        }
        _hooks.Clear();
    }
}
