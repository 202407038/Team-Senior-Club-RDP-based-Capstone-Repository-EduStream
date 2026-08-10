using EduStream.Core.Protocols;
using EduStream.Core.Utils;

namespace EduStream.Core.Models;

/// <summary>
/// 서버와 클라이언트가 같은 오류 코드와 사용자 표시 문구를 해석하도록 하는 공통 카탈로그입니다.
/// </summary>
public static class FeatureErrorCatalog
{
    private static readonly IReadOnlyDictionary<string, FeatureErrorInfo> Errors =
        new Dictionary<string, FeatureErrorInfo>(StringComparer.Ordinal)
        {
            [ErrorCodes.SessionNotOpen] = Session(ErrorCodes.SessionNotOpen, "현재 열려 있는 세션이 없습니다.", false),
            [ErrorCodes.DisplayNameRequired] = Session(ErrorCodes.DisplayNameRequired, "참여자 이름을 입력해 주세요.", true),
            [ErrorCodes.JoinRejected] = Session(ErrorCodes.JoinRejected, "세션 참가가 거절되었습니다.", true),
            [ErrorCodes.ClientAlreadyJoined] = Session(ErrorCodes.ClientAlreadyJoined, "이미 세션에 참가한 연결입니다.", true),
            [ErrorCodes.AlreadyJoined] = Session(ErrorCodes.AlreadyJoined, "이미 같은 이름으로 참가 중입니다.", true),
            [ErrorCodes.NotParticipant] = Session(ErrorCodes.NotParticipant, "세션 참가 상태를 다시 확인해 주세요.", true),

            [ErrorCodes.InvalidPacketType] = Protocol(ErrorCodes.InvalidPacketType, "지원하지 않는 패킷 형식입니다.", false),
            [ErrorCodes.InvalidAckCode] = Protocol(ErrorCodes.InvalidAckCode, "알 수 없는 성공 응답을 받았습니다.", false),
            [ErrorCodes.InvalidErrorCode] = Protocol(ErrorCodes.InvalidErrorCode, "알 수 없는 오류 응답을 받았습니다.", false),
            [ErrorCodes.PayloadLengthMismatch] = Protocol(ErrorCodes.PayloadLengthMismatch, "수신 데이터 길이가 올바르지 않습니다.", true),

            [ErrorCodes.ChecksumMismatch] = File(ErrorCodes.ChecksumMismatch, "파일 무결성 확인에 실패했습니다. 다시 전송해 주세요.", true),
            [ErrorCodes.ChecksumRequired] = File(ErrorCodes.ChecksumRequired, "파일 무결성 정보가 없어 전송을 처리할 수 없습니다.", true),
            [ErrorCodes.InvalidChunkSize] = File(ErrorCodes.InvalidChunkSize, "파일 청크 크기가 허용 범위를 벗어났습니다.", false),
            [ErrorCodes.InvalidChunkIndex] = File(ErrorCodes.InvalidChunkIndex, "파일 청크 순서가 올바르지 않습니다.", true),
            [ErrorCodes.InvalidTotalChunks] = File(ErrorCodes.InvalidTotalChunks, "파일 청크 개수가 올바르지 않습니다.", true),
            [ErrorCodes.InvalidFileName] = File(ErrorCodes.InvalidFileName, "파일 이름이 올바르지 않습니다.", false),
            [ErrorCodes.InvalidFileSize] = File(ErrorCodes.InvalidFileSize, "파일 크기 정보가 올바르지 않습니다.", false),
            [ErrorCodes.InvalidFilePayloadLength] = File(ErrorCodes.InvalidFilePayloadLength, "파일 데이터 길이가 메타데이터와 다릅니다.", true),
            [ErrorCodes.EmptyChunkPayload] = File(ErrorCodes.EmptyChunkPayload, "비어 있는 파일 청크를 수신했습니다.", true),
            [ErrorCodes.FileChunkPending] = File(ErrorCodes.FileChunkPending, "파일 청크를 수신 중입니다.", true),
            [ErrorCodes.FileChunkMetadataMismatch] = File(ErrorCodes.FileChunkMetadataMismatch, "파일 청크 정보가 서로 다릅니다. 다시 전송해 주세요.", true),
            [ErrorCodes.FileAssemblyFailed] = File(ErrorCodes.FileAssemblyFailed, "파일 조립에 실패했습니다. 다시 전송해 주세요.", true),

            [ErrorCodes.InvalidFrameDimensions] = Screen(ErrorCodes.InvalidFrameDimensions, "화면 프레임 크기가 올바르지 않습니다.", true),
            [ErrorCodes.InvalidFrameInterval] = Screen(ErrorCodes.InvalidFrameInterval, "화면 전송 간격 설정이 올바르지 않습니다.", true),
            [ErrorCodes.InvalidScreenEncoding] = Screen(ErrorCodes.InvalidScreenEncoding, "지원하지 않는 화면 인코딩입니다.", true),
            [ErrorCodes.EmptyScreenPayload] = Screen(ErrorCodes.EmptyScreenPayload, "비어 있는 화면 프레임을 수신했습니다.", true),

            [ErrorCodes.EmptyMessage] = Chat(ErrorCodes.EmptyMessage, "채팅 메시지를 입력해 주세요.", true),
            [ErrorCodes.MessageTooLong] = Chat(ErrorCodes.MessageTooLong, "채팅 메시지가 너무 깁니다.", true)
        };

    public static FeatureErrorInfo Resolve(string errorCode)
    {
        PacketContractUtility.ValidateErrorCode(errorCode);

        return Errors.TryGetValue(errorCode, out var error)
            ? error
            : new FeatureErrorInfo(errorCode, FeatureArea.Protocol, "요청을 처리하는 중 오류가 발생했습니다.", false);
    }

    public static bool TryResolve(string? errorCode, out FeatureErrorInfo? error)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            error = null;
            return false;
        }

        return Errors.TryGetValue(errorCode, out error);
    }

    private static FeatureErrorInfo Session(string code, string message, bool recoverable) =>
        new(code, FeatureArea.Session, message, recoverable);

    private static FeatureErrorInfo Protocol(string code, string message, bool recoverable) =>
        new(code, FeatureArea.Protocol, message, recoverable);

    private static FeatureErrorInfo File(string code, string message, bool recoverable) =>
        new(code, FeatureArea.File, message, recoverable);

    private static FeatureErrorInfo Screen(string code, string message, bool recoverable) =>
        new(code, FeatureArea.Screen, message, recoverable);

    private static FeatureErrorInfo Chat(string code, string message, bool recoverable) =>
        new(code, FeatureArea.Chat, message, recoverable);
}
