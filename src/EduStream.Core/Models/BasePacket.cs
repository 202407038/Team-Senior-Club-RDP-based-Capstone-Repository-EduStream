namespace EduStream.Core.Models;

/// <summary>
/// 모든 전송 데이터가 공통으로 가지는 기본 헤더 정보입니다.
/// </summary>
public abstract class BasePacket
{
    public PacketType MessageType { get; init; }

    public int DataLength { get; set; }

    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
