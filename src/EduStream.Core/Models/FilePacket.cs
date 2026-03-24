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

    /// <summary>
    /// 현재 파일 전송이 여러 청크로 분할된 전송인지 나타냅니다.
    /// </summary>
    public bool IsChunkedTransfer => TotalChunks > 1;

    /// <summary>
    /// 현재 패킷이 마지막 청크인지 확인합니다.
    /// </summary>
    public bool IsLastChunk => TotalChunks <= 0 || ChunkIndex >= TotalChunks - 1;

    /// <summary>
    /// 현재 패킷 본문 길이를 바이트 단위로 반환합니다.
    /// </summary>
    public int ContentLength => Content.Length;
}
