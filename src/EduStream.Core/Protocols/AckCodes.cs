namespace EduStream.Core.Protocols;

/// <summary>
/// 성공 응답 패킷에서 공통으로 사용하는 코드 목록입니다.
/// </summary>
public static class AckCodes
{
    public const string SessionJoined = "SESSION_JOINED";
    public const string SessionLeft = "SESSION_LEFT";
    public const string FileAccepted = "FILE_ACCEPTED";
    public const string FileSaved = "FILE_SAVED";

    /// <summary>
    /// 화면 공유가 (재)시작돼 화면을 기다리던 학생이 새 연결 ID로 RDP 초대를 다시 요청해도 된다는 알림입니다.
    /// 초대 자체나 비밀번호는 담지 않습니다.
    /// </summary>
    public const string RdpSharingStarted = "RDP_SHARING_STARTED";
}
