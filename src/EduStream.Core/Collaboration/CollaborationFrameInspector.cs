using System.Text.Json;

namespace EduStream.Core.Collaboration;

/// <summary>
/// 수신 dispatcher가 어떤 타입으로 <see cref="CollaborationMessageCodec.Decode{T}"/>할지 고르기 위해
/// envelope의 Kind만 먼저 읽습니다. 버전·식별자·본문 검증은 이후 Decode가 수행합니다.
/// </summary>
public static class CollaborationFrameInspector
{
    public static CollaborationMessageKind PeekKind(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0 || frame.Length > CollaborationMessageCodec.MaxFrameBytes)
            throw new CollaborationException(CollaborationError.ResourceLimit);
        try
        {
            var reader = new Utf8JsonReader(frame, new JsonReaderOptions { MaxDepth = 16 });
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(nameof(CollaborationEnvelope.Kind), out var kind) ||
                kind.ValueKind != JsonValueKind.Number || !kind.TryGetInt32(out var value) || !Enum.IsDefined((CollaborationMessageKind)value))
                throw new CollaborationException(CollaborationError.InvalidRequest);
            return (CollaborationMessageKind)value;
        }
        catch (JsonException) { throw new CollaborationException(CollaborationError.InvalidRequest); }
    }
}
