namespace EduStream.Server.Services;

/// <summary>
/// 원격 제어 회수 뒤 3번 입력 엔진의 실제 입력 차단 확인 결과입니다.
/// 승인 상태(Revoked)는 결과와 관계없이 즉시 회수되므로, 이 값은 "실제 입력이 막혔는가"만 구분합니다.
/// </summary>
public enum RemoteInputRevokeStatus
{
    /// <summary>회수할 입력이 없거나, 모든 회수 요청의 차단 확인이 끝났습니다.</summary>
    Confirmed,

    /// <summary>
    /// 취소를 무시한 허용 작업이 아직 끝나지 않아 차단 확인이 남았습니다.
    /// 허용 작업이 끝나면 조정자가 자동으로 다시 회수합니다.
    /// </summary>
    Pending,

    /// <summary>입력 엔진의 회수 호출이 실패했습니다. 회수 요청은 대기열에 남아 다음 요청/중지 때 다시 시도됩니다.</summary>
    Failed
}
