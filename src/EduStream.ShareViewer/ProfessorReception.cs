using System.Linq.Expressions;
using System.Reflection;

namespace EduStream.ShareViewer;

/// <summary>
/// 교수자 PC에서 학생 화면을 수신합니다.
/// 데스크톱 공유 세션은 열지 않습니다. 학생 PC가 만든 연결 문자열로 뷰어만 붙입니다.
/// </summary>
public sealed class ProfessorReception : IDisposable
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
            connection.Connect(connectionString, professorName, password);
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
        lock (_gate) _viewers.Remove(studentId);
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
public sealed class ProfessorViewerConnection : IDisposable
{
    private readonly System.Windows.Forms.Control _viewer;
    private readonly List<(EventInfo Event, Delegate Handler)> _hooks = new();
    private bool _established;
    private bool _failed;
    private bool _terminated;
    private bool _released;

    public ProfessorViewerConnection(System.Windows.Forms.Control viewer)
    {
        _viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));
        Listen("OnConnectionEstablished", () => _established = true);
        Listen("OnConnectionFailed", () => _failed = true);
        Listen("OnConnectionTerminated", () => _terminated = true);
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
        RunOnViewerThread(() => ((dynamic)_viewer).Connect(connectionString, name, password));
    }

    /// <summary>
    /// 공유 세션이 끝난 뒤에 호출합니다.
    /// 이벤트 해제, Disconnect, 부모 제거, Dispose 를 컨트롤을 소유한 UI 스레드에서 끝내고, 그 다음에만 완료로 표시합니다.
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

        if (_viewer.IsHandleCreated && _viewer.InvokeRequired)
            _viewer.Invoke(ReleaseOnViewerThread);
        else
            ReleaseOnViewerThread();
    }

    private void ReleaseOnViewerThread()
    {
        if (_released) return;
        if (_viewer.IsDisposed)
        {
            _hooks.Clear();
            _released = true;
            return;
        }

        Unlisten();
        if (IsConnectionLive)
            ((dynamic)_viewer).Disconnect();

        _viewer.Parent?.Controls.Remove(_viewer);
        if (_viewer.Parent != null)
            throw new InvalidOperationException("뷰어 컨트롤이 부모에서 떨어지지 않았습니다.");

        _viewer.Dispose();
        if (!_viewer.IsDisposed)
            throw new InvalidOperationException("뷰어 컨트롤이 해제되지 않았습니다.");

        _released = true;
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
            evt.RemoveEventHandler(_viewer, handler);
        _hooks.Clear();
    }
}
