using EduStream.Core.Protocols;

namespace EduStream.Core.Models;

/// <summary>
/// 화면 프레임을 전달하기 위한 공통 패킷입니다.
/// 화면 데이터 본문과 해석에 필요한 메타데이터를 함께 전달합니다.
/// </summary>
public sealed class ScreenPacket : BasePacket
{
    public ScreenPacket()
    {
        MessageType = PacketType.Screen;
    }

    public int FrameIndex { get; set; }

    public string FrameDescription { get; set; } = string.Empty;

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    public int Width { get; set; }

    public int Height { get; set; }

    public string Encoding { get; set; } = ScreenEncodings.Raw;

    public byte[] Content { get; set; } = [];

    public int ContentLength => Content.Length;
}
