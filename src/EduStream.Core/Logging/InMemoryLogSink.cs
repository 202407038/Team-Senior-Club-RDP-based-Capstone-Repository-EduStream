namespace EduStream.Core.Logging;

/// <summary>
/// 화면 바인딩과 디버깅 용도로 메모리에 로그를 보관합니다.
/// 송수신·세션·하트비트 루프가 모두 같은 sink로 쓰므로 lock으로 안전성을 보장합니다.
/// </summary>
public sealed class InMemoryLogSink : ILogSink
{
    private readonly List<string> _entries = [];
    private readonly object _sync = new();

    public void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_sync)
        {
            _entries.Add(line);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }
}
