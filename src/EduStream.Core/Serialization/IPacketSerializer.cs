using EduStream.Core.Models;

namespace EduStream.Core.Serialization;

/// <summary>
/// 직렬화 방식을 JSON에서 MessagePack으로 교체하더라도 호출부가 바뀌지 않도록 분리한 인터페이스입니다.
/// </summary>
public interface IPacketSerializer
{
    byte[] Serialize<TPacket>(TPacket packet) where TPacket : BasePacket;

    TPacket? Deserialize<TPacket>(byte[] payload) where TPacket : BasePacket;
}
