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
}
