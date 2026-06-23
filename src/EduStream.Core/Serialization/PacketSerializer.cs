using System.Text;
using System.Text.Json;
using EduStream.Core.Models;

namespace EduStream.Core.Serialization;

/// <summary>
/// 현재 단계에서는 JSON 기반으로 패킷 직렬화를 담당합니다.
/// 나중에 MessagePack 등으로 교체하기 쉽게 별도 클래스로 분리합니다.
/// </summary>
public sealed class PacketSerializer : IPacketSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public byte[] Serialize<TPacket>(TPacket packet) where TPacket : BasePacket
    {
        ArgumentNullException.ThrowIfNull(packet);

        var json = JsonSerializer.Serialize(packet, packet.GetType(), JsonOptions);
        return Encoding.UTF8.GetBytes(json);
    }

    public TPacket? Deserialize<TPacket>(byte[] payload) where TPacket : BasePacket
    {
        ArgumentNullException.ThrowIfNull(payload);

        return JsonSerializer.Deserialize<TPacket>(payload, JsonOptions);
    }
}
