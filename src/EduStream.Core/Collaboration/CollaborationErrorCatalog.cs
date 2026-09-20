namespace EduStream.Core.Collaboration;

public sealed record CollaborationFailure(string Code, string UserMessage, bool CanRetry);

public static class CollaborationErrorCatalog
{
    /// <summary>UI에는 예외의 로컬 경로/내부 메시지를 그대로 노출하지 않습니다.</summary>
    public static CollaborationFailure FromException(Exception exception) => exception switch
    {
        OperationCanceledException => new("CANCELLED", "작업이 취소되었습니다.", true),
        UnauthorizedAccessException => new("FILE_ACCESS_DENIED", "파일 또는 저장 폴더의 접근 권한을 확인해 주세요.", false),
        PathTooLongException => new("FILE_PATH_TOO_LONG", "파일 이름이나 저장 경로가 너무 깁니다.", false),
        IOException => new("FILE_IO_ERROR", "파일 잠금·저장 공간·원본 파일을 확인해 주세요.", true),
        CollaborationException error => Describe(error.Code),
        _ => new("INTERNAL_ERROR", "작업을 완료하지 못했습니다. 진단 기록을 확인해 주세요.", false)
    };

    public static CollaborationFailure Describe(CollaborationError error) => error switch
    {
        CollaborationError.InvalidRequest => new("INVALID_REQUEST", "요청 또는 수신 데이터가 올바르지 않습니다.", false),
        CollaborationError.NotAuthorized => new("NOT_AUTHORIZED", "승인된 세션 참가 상태를 확인해 주세요.", false),
        CollaborationError.SessionClosed => new("SESSION_CLOSED", "세션이 종료되었습니다.", false),
        CollaborationError.StaleConnection => new("STALE_CONNECTION", "이전 연결의 요청은 사용할 수 없습니다.", false),
        CollaborationError.PermissionDenied => new("PERMISSION_DENIED", "학생의 화면 보기·제어 허용 상태를 확인해 주세요.", false),
        CollaborationError.UnsupportedCapability => new("UNSUPPORTED_CAPABILITY", "연결 상대가 이 기능을 지원하지 않습니다.", false),
        CollaborationError.FileUnavailable => new("FILE_UNAVAILABLE", "등록이 해제되었거나 사용할 수 없는 파일입니다.", false),
        CollaborationError.SourceChanged => new("SOURCE_CHANGED", "원본 파일이 변경되었습니다. 교수자가 다시 등록해야 합니다.", false),
        CollaborationError.IntegrityFailure => new("INTEGRITY_FAILURE", "파일 무결성 확인에 실패했습니다. 다시 다운로드해 주세요.", true),
        CollaborationError.TransferIncomplete => new("TRANSFER_INCOMPLETE", "파일을 끝까지 받지 못했습니다. 다시 다운로드해 주세요.", true),
        CollaborationError.ResourceLimit => new("RESOURCE_LIMIT", "파일 크기·등록 수·동시 전송 제한을 확인해 주세요.", false),
        _ => new("INTERNAL_ERROR", "알 수 없는 오류입니다.", false)
    };
}
