using System.Text.Json;

namespace EduStream.Core.Collaboration;

/// <summary>
/// 수신 dispatcher가 어떤 타입으로 <see cref="CollaborationMessageCodec.Decode{T}"/>할지 고르기 위해
/// envelope의 구조·버전·식별자를 확인하고 Kind를 읽습니다.
/// 본문별 검증·인증된 발신자 대조는 이후 Decode 및 라우터가 반드시 수행합니다.
/// </summary>
public static class CollaborationFrameInspector
{
    public static CollaborationMessageKind PeekKind(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0 || frame.Length > CollaborationMessageCodec.MaxFrameBytes)
            throw new CollaborationException(CollaborationError.ResourceLimit);
        try
        {
            // ParseValue는 첫 JSON 뒤의 추가 데이터를 무시할 수 있으므로 프레임 전체를 검사합니다.
            using var document = JsonDocument.Parse(frame.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            CollaborationMessageCodec.RejectDuplicateProperties(root);
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(nameof(CollaborationEnvelope.Version), out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber))
                throw new CollaborationException(CollaborationError.InvalidRequest);
            if (versionNumber != CollaborationContract.Version)
                throw new CollaborationException(CollaborationError.UnsupportedCapability);
            if (!root.TryGetProperty(nameof(CollaborationEnvelope.MessageId), out var message) ||
                message.ValueKind != JsonValueKind.String || !message.TryGetGuid(out var messageId) || messageId == Guid.Empty ||
                !root.TryGetProperty(nameof(CollaborationEnvelope.Payload), out var payload) ||
                payload.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(nameof(CollaborationEnvelope.Kind), out var kind) ||
                kind.ValueKind != JsonValueKind.Number || !kind.TryGetInt32(out var value) || !Enum.IsDefined((CollaborationMessageKind)value))
                throw new CollaborationException(CollaborationError.InvalidRequest);
            return (CollaborationMessageKind)value;
        }
        catch (JsonException) { throw new CollaborationException(CollaborationError.InvalidRequest); }
    }
}
