namespace EduStream.Core.Models;

/// <summary>
/// 세션, 화면, 파일, 채팅에서 공통으로 사용하는 작업 상태입니다.
/// </summary>
public enum OperationState
{
    Unknown = 0,
    Idle = 1,
    Pending = 2,
    InProgress = 3,
    Succeeded = 4,
    Failed = 5,
    Stopped = 6
}
