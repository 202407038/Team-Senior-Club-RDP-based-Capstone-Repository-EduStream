namespace EduStream.Core.Protocols;

/// <summary>
/// 오류 응답 패킷에서 공통으로 사용하는 코드 목록입니다.
/// </summary>
public static class ErrorCodes
{
    public const string SessionNotOpen = "SESSION_NOT_OPEN";
    public const string DisplayNameRequired = "DISPLAY_NAME_REQUIRED";
    public const string JoinRejected = "JOIN_REJECTED";
    public const string ClientAlreadyJoined = "CLIENT_ALREADY_JOINED";
    public const string ChecksumMismatch = "CHECKSUM_MISMATCH";
    public const string ChecksumRequired = "CHECKSUM_REQUIRED";
    public const string InvalidChunkSize = "INVALID_CHUNK_SIZE";
    public const string InvalidChunkIndex = "INVALID_CHUNK_INDEX";
    public const string InvalidTotalChunks = "INVALID_TOTAL_CHUNKS";
    public const string InvalidFileName = "INVALID_FILE_NAME";
    public const string InvalidFileSize = "INVALID_FILE_SIZE";
    public const string EmptyChunkPayload = "EMPTY_CHUNK_PAYLOAD";
    public const string FileChunkPending = "FILE_CHUNK_PENDING";
    public const string FileChunkMetadataMismatch = "FILE_CHUNK_METADATA_MISMATCH";
    public const string FileAssemblyFailed = "FILE_ASSEMBLY_FAILED";
    public const string AlreadyJoined = "ALREADY_JOINED";
    public const string InvalidFrameDimensions = "INVALID_FRAME_DIMENSIONS";
    public const string InvalidFrameInterval = "INVALID_FRAME_INTERVAL";
    public const string InvalidScreenEncoding = "INVALID_SCREEN_ENCODING";
    public const string EmptyScreenPayload = "EMPTY_SCREEN_PAYLOAD";
    public const string NotParticipant = "NOT_PARTICIPANT";
    public const string EmptyMessage = "EMPTY_MESSAGE";
    public const string MessageTooLong = "MESSAGE_TOO_LONG";
}
