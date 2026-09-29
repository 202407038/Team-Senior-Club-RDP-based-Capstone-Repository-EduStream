namespace EduStream.Core.Models;

/// <summary>
/// 공통 오류 코드를 기능 영역과 사용자 표시 기준으로 연결합니다.
/// </summary>
public sealed record FeatureErrorInfo(
    string Code,
    FeatureArea Feature,
    string UserMessage,
    bool DefaultRecoverable);
