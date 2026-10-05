using System.Text;
using System.Text.Json;

namespace EduStream.Core.Collaboration;

/// <summary>
/// #51 AnnotationStrokeWire/AnnotationLayerWire JSON을 운반하는 공통 봉투입니다.
/// 렌더러가 없는 Core는 WPF/Server를 참조하지 않습니다. 신규 메시지는 라우터 연결 전 자동 송신되지 않습니다.
/// </summary>
public sealed record AnnotationTransportNotice(Guid SessionId, Guid SharingId, Guid ConnectionId,
    long Sequence, string PayloadJson)
{
    public const int MaxPayloadBytes = 1024 * 1024;
    public const int MaxStrokes = 4096;
    public const int MaxPointsPerStroke = 4096;

    public void Validate()
    {
        if (SessionId == Guid.Empty || SharingId == Guid.Empty || ConnectionId == Guid.Empty || Sequence <= 0 ||
            string.IsNullOrWhiteSpace(PayloadJson) || Encoding.UTF8.GetByteCount(PayloadJson) > MaxPayloadBytes)
            throw new ArgumentException("유효하지 않은 판서 메시지입니다.");
        try
        {
            using var document = Parse(PayloadJson);
            var root = document.RootElement;
            if (root.TryGetProperty("Kind", out var kind))
            {
                if (kind.GetString() != "annotation-layer" || root.GetProperty("Version").GetInt32() != 1 ||
                    root.GetProperty("Change").GetInt32() is < 0 or > 3 ||
                    root.GetProperty("ContentRevision").GetInt64() < 0)
                    throw new ArgumentException("지원하지 않는 판서 레이어입니다.");
                var visible = root.GetProperty("IsVisible").GetBoolean();
                var strokes = root.GetProperty("Strokes");
                if (strokes.ValueKind != JsonValueKind.Array || strokes.GetArrayLength() > MaxStrokes ||
                    (!visible && strokes.GetArrayLength() != 0))
                    throw new ArgumentException("판서 레이어 크기 또는 표시 상태가 잘못되었습니다.");
                var ids = new HashSet<Guid>();
                foreach (var stroke in strokes.EnumerateArray())
                {
                    using var nested = Parse(stroke.GetString() ?? "");
                    if (!ids.Add(ValidateStroke(nested.RootElement)))
                        throw new ArgumentException("판서 ID가 중복되었습니다.");
                }
            }
            else ValidateStroke(root);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new ArgumentException("판서 JSON 구조가 잘못되었습니다.", ex);
        }
    }

    /// <summary>
    /// 현재 공유의 교수자 인증 연결 및 마지막 적용 번호와 대조합니다.
    /// 번호 저장은 렌더러 적용이 성공한 뒤 라우터가 수행하며, 재접속 시 새 ConnectionId로 초기화합니다.
    /// </summary>
    public void ValidateForSender(ParticipantConnection sender, Guid sharingId, long lastAppliedSequence)
    {
        ArgumentNullException.ThrowIfNull(sender);
        sender.Validate();
        Validate();
        if (sender.Role != ParticipantRole.Professor || SessionId != sender.SessionId ||
            ConnectionId != sender.ConnectionId || SharingId != sharingId || Sequence <= lastAppliedSequence)
            throw new CollaborationException(CollaborationError.StaleConnection);
    }

    private static Guid ValidateStroke(JsonElement stroke)
    {
        var id = stroke.GetProperty("StrokeId").GetGuid();
        var participant = stroke.GetProperty("ParticipantId").GetString();
        if (id == Guid.Empty || !ReverseRdpInvitationNotice.ValidIdentity(participant!) ||
            stroke.GetProperty("CreatedAt").GetDateTimeOffset() == default ||
            // Core의 UI 도구 Ellipse는 기존 엔진 wire에서 Circle로 직렬화된다.
            // 엔진이 표시하지 못하는 Highlighter/Arrow/Text는 전달 전에 거부한다.
            stroke.GetProperty("Tool").GetString() is not ("Pen" or "Eraser" or "Line" or "Rectangle" or "Circle") ||
            stroke.GetProperty("StrokeWidth").GetInt32() is < 1 or > 256)
            throw new ArgumentException("판서 스트로크 속성이 잘못되었습니다.");
        foreach (var component in new[] { "R", "G", "B", "A" })
            if (stroke.GetProperty(component).GetInt32() is < 0 or > 255)
                throw new ArgumentException("판서 색상이 잘못되었습니다.");
        _ = stroke.GetProperty("IsVisible").GetBoolean();
        var points = stroke.GetProperty("Points");
        if (points.ValueKind != JsonValueKind.Array || points.GetArrayLength() is < 2 or > MaxPointsPerStroke)
            throw new ArgumentException("판서 좌표 수가 잘못되었습니다.");
        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2)
                throw new ArgumentException("판서 좌표는 X/Y 쌍이어야 합니다.");
            // 다중 모니터의 음수 원점을 허용합니다. 표시 영역 대조는 3번 렌더러의 책임입니다.
            _ = point[0].GetInt32();
            _ = point[1].GetInt32();
        }
        return id;
    }

    private static JsonDocument Parse(string json)
    {
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        try { RejectDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("중복 JSON 속성입니다.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    public override string ToString() =>
        $"AnnotationTransportNotice {{ SessionId = {SessionId}, SharingId = {SharingId}, Sequence = {Sequence}, PayloadJson = *** }}";
}
