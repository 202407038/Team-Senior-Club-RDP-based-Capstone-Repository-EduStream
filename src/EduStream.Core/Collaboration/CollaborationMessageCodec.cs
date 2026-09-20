using System.Text.Json;
using EduStream.Core.FileSharing;

namespace EduStream.Core.Collaboration;

public enum CollaborationMessageKind
{
    Participants = 1, FileCatalog = 2, FileRequest = 3, FileChunk = 4,
    FileCancel = 5, FileStored = 6, Failure = 7
}
public sealed record FileCancelRequest(Guid RequestId, Guid SessionId);
public sealed record FileStoredNotice(Guid RequestId, Guid FileId, long Length, string Sha256);
public sealed record CollaborationFailureNotice(Guid RequestId, CollaborationError Error);
public sealed record CollaborationEnvelope(int Version, Guid MessageId, CollaborationMessageKind Kind, JsonElement Payload);

/// <summary>
/// 신규 목록/파일 메시지의 크기 제한 JSON 계약. 암호화·인증 자체를 수행하지 않습니다.
/// 2번은 검증된 보호 채널에서만 사용하고 프레임 메모리 할당 전 길이 상한을 검사합니다.
/// </summary>
public static class CollaborationMessageCodec
{
    public const int MaxFrameBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 16 };

    public static byte[] Encode<T>(Guid messageId, T payload) where T : notnull
    {
        CollaborationContract.RequireId(messageId, nameof(messageId));
        var kind = KindFor(typeof(T));
        ValidatePayload(payload);
        var envelope = new CollaborationEnvelope(CollaborationContract.Version, messageId, kind,
            JsonSerializer.SerializeToElement(payload, Options));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        if (bytes.Length > MaxFrameBytes) throw new CollaborationException(CollaborationError.ResourceLimit);
        return bytes;
    }

    public static T Decode<T>(ReadOnlySpan<byte> bytes, out Guid messageId) where T : notnull
    {
        messageId = Guid.Empty;
        if (bytes.Length == 0 || bytes.Length > MaxFrameBytes)
            throw new CollaborationException(CollaborationError.ResourceLimit);
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            var envelope = document.RootElement.Deserialize<CollaborationEnvelope>(Options)
                ?? throw new CollaborationException(CollaborationError.InvalidRequest);
            if (envelope.Version != CollaborationContract.Version)
                throw new CollaborationException(CollaborationError.UnsupportedCapability);
            CollaborationContract.RequireId(envelope.MessageId, nameof(envelope.MessageId));
            if (envelope.Kind != KindFor(typeof(T)))
                throw new CollaborationException(CollaborationError.InvalidRequest);
            if (envelope.Payload.ValueKind != JsonValueKind.Object)
                throw new CollaborationException(CollaborationError.InvalidRequest);
            var payload = envelope.Payload.Deserialize<T>(Options)
                ?? throw new CollaborationException(CollaborationError.InvalidRequest);
            ValidatePayload(payload);
            messageId = envelope.MessageId;
            return payload;
        }
        catch (JsonException) { throw new CollaborationException(CollaborationError.InvalidRequest); }
        catch (ArgumentException) { throw new CollaborationException(CollaborationError.InvalidRequest); }
    }

    private static CollaborationMessageKind KindFor(Type type)
    {
        if (type == typeof(RoomJoined)) return CollaborationMessageKind.Participants;
        if (type == typeof(SessionFileCatalogSnapshot)) return CollaborationMessageKind.FileCatalog;
        if (type == typeof(SessionFileRequest)) return CollaborationMessageKind.FileRequest;
        if (type == typeof(SessionFileChunk)) return CollaborationMessageKind.FileChunk;
        if (type == typeof(FileCancelRequest)) return CollaborationMessageKind.FileCancel;
        if (type == typeof(FileStoredNotice)) return CollaborationMessageKind.FileStored;
        if (type == typeof(CollaborationFailureNotice)) return CollaborationMessageKind.Failure;
        throw new CollaborationException(CollaborationError.UnsupportedCapability);
    }

    private static void ValidatePayload(object payload)
    {
        switch (payload)
        {
            case RoomJoined room:
                if (room.Participants is null || room.Participants.Count > 256)
                    throw new CollaborationException(CollaborationError.ResourceLimit);
                ParticipantSnapshotRules.Validate(room);
                break;
            case SessionFileCatalogSnapshot catalog:
                CollaborationContract.RequireId(catalog.SessionId, nameof(catalog.SessionId));
                if (catalog.Revision < 0 || catalog.Files is null ||
                    catalog.Files.Count > SessionFileLimits.MaxRegisteredFiles)
                    throw new CollaborationException(CollaborationError.InvalidRequest);
                var ids = new HashSet<Guid>();
                foreach (var file in catalog.Files)
                {
                    if (file is null) throw new CollaborationException(CollaborationError.InvalidRequest);
                    file.Validate();
                    if (file.SessionId != catalog.SessionId || !ids.Add(file.FileId))
                        throw new CollaborationException(CollaborationError.InvalidRequest);
                }
                break;
            case SessionFileRequest request: request.Validate(); break;
            case SessionFileChunk chunk:
                new SessionFileRequest(chunk.RequestId, chunk.SessionId, chunk.FileId, chunk.Revision).Validate();
                if (chunk.Index < 0 || chunk.Content is null ||
                    chunk.Content.Length > EduStream.Core.Protocols.FileTransferRules.MaxChunkSize)
                    throw new CollaborationException(CollaborationError.InvalidRequest);
                break;
            case FileCancelRequest cancel:
                CollaborationContract.RequireId(cancel.RequestId, nameof(cancel.RequestId));
                CollaborationContract.RequireId(cancel.SessionId, nameof(cancel.SessionId));
                break;
            case FileStoredNotice stored:
                CollaborationContract.RequireId(stored.RequestId, nameof(stored.RequestId));
                CollaborationContract.RequireId(stored.FileId, nameof(stored.FileId));
                if (stored.Length < 0 || stored.Length > SessionFileLimits.MaxFileBytes ||
                    stored.Sha256 is null || stored.Sha256.Length != 64 || !stored.Sha256.All(Uri.IsHexDigit))
                    throw new CollaborationException(CollaborationError.InvalidRequest);
                break;
            case CollaborationFailureNotice failure:
                CollaborationContract.RequireId(failure.RequestId, nameof(failure.RequestId));
                if (!Enum.IsDefined(failure.Error))
                    throw new CollaborationException(CollaborationError.InvalidRequest);
                break;
            default: throw new CollaborationException(CollaborationError.UnsupportedCapability);
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new CollaborationException(CollaborationError.InvalidRequest);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
