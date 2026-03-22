namespace EduStream.Core.Logging;

/// <summary>
/// 화면 바인딩과 디버깅 용도로 메모리에 로그를 보관합니다.
/// </summary>
public sealed class InMemoryLogSink : ILogSink
{
    private readonly List<string> _entries = [];

    public void Write(string message)
    {
        _entries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    public IReadOnlyList<string> Snapshot()
    {
        return _entries.AsReadOnly();
    }
}
