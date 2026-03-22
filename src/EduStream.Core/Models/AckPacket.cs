namespace EduStream.Core.Models;

/// <summary>
/// 특정 요청이나 전송이 정상 처리되었음을 알리는 응답 패킷입니다.
/// </summary>
public sealed class AckPacket : BasePacket
{
    public AckPacket()
    {
        MessageType = PacketType.Ack;
    }

    public string AckCode { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
