namespace EduStream.Core.Logging;

/// <summary>
/// 프로젝트 전반에서 공통으로 사용할 수 있는 최소 로그 인터페이스입니다.
/// </summary>
public interface ILogSink
{
    void Write(string message);
    IReadOnlyList<string> Snapshot();
}
