using EduStream.Core.Protocols;

namespace EduStream.Core.Models;

/// <summary>
/// 모든 전송 데이터가 공통으로 가지는 기본 헤더 정보입니다.
/// </summary>
public abstract class BasePacket
{
    public string ProtocolVersion { get; init; } = ProtocolVersions.Current;

    public PacketType MessageType { get; init; }

    public Guid? SessionId { get; set; }

    public string SenderId { get; set; } = string.Empty;

    /// <summary>
    /// 패킷 본문(payload)의 바이트 길이입니다. 직렬화된 패킷 전체 길이가 아니라
    /// 파일/화면 본문 또는 텍스트 메시지의 UTF-8 바이트 길이를 기록합니다.
    /// </summary>
    public int DataLength { get; set; }

    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
