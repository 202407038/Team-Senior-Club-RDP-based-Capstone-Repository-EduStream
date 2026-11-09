using System.Text;
using EduStream.Core.Models;

namespace EduStream.Server.Services;

public sealed record AttendanceEntry(Guid SessionId, DateTimeOffset At, string Name, string StudentId, string Action);
public sealed record RosterPresence(string Name, string StudentId, bool Connected)
{
    public string Summary => $"{Name} · {StudentId} · {(Connected ? "접속 중" : "미접속")}";
}

/// <summary>참가 승인/이탈 사실만 기록한다. 출석 인정이나 학교 본인 인증이 아니다.</summary>
public sealed class SessionAttendance
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Name, string StudentId)> _connections = new();
    private readonly List<AttendanceEntry> _entries = [];
    private RosterRow[] _roster = [];
    private Guid _session;
    public event Action? Changed;
    public void Start(Guid session, IEnumerable<RosterRow> roster)
    {
        lock (_gate) { _session = session; _roster = roster.ToArray(); _entries.Clear(); _connections.Clear(); }
        Changed?.Invoke();
    }
    public void Join(string clientId, string name, string studentId, bool verifiedReconnect)
    {
        lock (_gate)
        {
            if (_session == Guid.Empty || !_connections.TryAdd(clientId, (name, studentId))) return;
            _entries.Add(new(_session, DateTimeOffset.Now, name, studentId, verifiedReconnect ? "재접속" : "입장"));
        }
        Changed?.Invoke();
    }
    public void Leave(string clientId, string action)
    {
        lock (_gate)
        {
            if (!_connections.Remove(clientId, out var student)) return;
            _entries.Add(new(_session, DateTimeOffset.Now, student.Name, student.StudentId, action));
        }
        Changed?.Invoke();
    }
    public void Close()
    {
        lock (_gate)
        {
            foreach (var student in _connections.Values)
                _entries.Add(new(_session, DateTimeOffset.Now, student.Name, student.StudentId, "세션 종료"));
            _connections.Clear(); _session = Guid.Empty;
        }
        Changed?.Invoke();
    }
    public IReadOnlyList<AttendanceEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }
    public IReadOnlyList<RosterPresence> ConnectedStudents
    {
        get { lock (_gate) return _connections.Values.Distinct().Select(x => new RosterPresence(x.Name, x.StudentId, true)).ToArray(); }
    }
    public IReadOnlyList<RosterPresence> Presence
    {
        get
        {
            lock (_gate) return _roster.Select(x => new RosterPresence(x.Name, x.StudentId,
                _connections.Values.Any(c => c.StudentId == x.StudentId && c.Name == x.Name))).ToArray();
        }
    }
    public string ToCsv()
    {
        var result = new StringBuilder("세션,시각,이름,학번,구분\r\n");
        foreach (var row in Entries)
            result.AppendLine(string.Join(",", new[] { row.SessionId.ToString(), row.At.ToString("O"), row.Name, row.StudentId, row.Action }.Select(CsvCell)));
        return result.ToString();
    }
    private static string CsvCell(string value)
    {
        // CSV를 엑셀로 열 때 사용자 이름이 수식으로 실행되지 않게 한다.
        if (value.TrimStart() is { Length: > 0 } trimmed && "=+-@".Contains(trimmed[0]) || value.Any(c => c is '\t' or '\r' or '\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
