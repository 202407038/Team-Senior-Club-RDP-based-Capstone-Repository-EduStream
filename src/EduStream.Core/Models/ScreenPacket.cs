namespace EduStream.Core.Models;

/// <summary>
/// 화면 프레임을 전달하기 위한 패킷입니다.
/// </summary>
public sealed class ScreenPacket : BasePacket
{
    public ScreenPacket()
    {
        MessageType = PacketType.Screen;
    }

    public int FrameIndex { get; set; }

    public string FrameDescription { get; set; } = string.Empty;

    public byte[] Content { get; set; } = [];
}
