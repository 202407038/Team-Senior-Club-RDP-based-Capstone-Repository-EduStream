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

    public string ContentType { get; set; } = "application/octet-stream";

    public string Checksum { get; set; } = string.Empty;

    public Guid TransferId { get; set; } = Guid.NewGuid();

    public int ChunkIndex { get; set; }

    public int TotalChunks { get; set; } = 1;

    public byte[] Content { get; set; } = [];
}
