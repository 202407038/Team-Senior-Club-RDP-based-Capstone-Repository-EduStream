namespace EduStream.Core.Protocols;

/// <summary>
/// 파일 전송에서 공통으로 사용하는 청크 크기와 제한값을 정의합니다.
/// 서버와 클라이언트가 같은 기준으로 파일 패킷을 해석하도록 맞추기 위한 상수입니다.
/// </summary>
public static class FileTransferRules
{
    public const int DefaultChunkSize = 64 * 1024;

    public const int MinChunkSize = 4 * 1024;

    public const int MaxChunkSize = 1024 * 1024;
}
