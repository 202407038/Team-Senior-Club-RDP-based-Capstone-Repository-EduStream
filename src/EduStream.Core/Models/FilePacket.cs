namespace EduStream.Core.Models;

/// <summary>
/// 파일 메타데이터와 본문 일부를 담는 패킷입니다.
/// </summary>
public sealed class FilePacket : BasePacket
{
    public FilePacket()
    {
        MessageType = PacketType.File;
    }

    public string FileName { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public string Checksum { get; set; } = string.Empty;

    public byte[] Content { get; set; } = [];
}
