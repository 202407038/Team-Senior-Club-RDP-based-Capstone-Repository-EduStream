namespace EduStream.Core.Models;

/// <summary>
/// 통합 실행 흐름에서 상태와 응답의 소유 기능을 구분합니다.
/// </summary>
public enum FeatureArea
{
    Unknown = 0,
    Session = 1,
    Screen = 2,
    File = 3,
    Chat = 4
}
