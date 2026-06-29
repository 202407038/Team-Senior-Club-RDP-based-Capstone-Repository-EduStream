using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.Core.Utils;

/// <summary>
/// 파일 전송 공통 규칙을 한 곳에 모아 서버/클라이언트가 같은 방식으로 검증하도록 돕습니다.
/// </summary>
public static class FileTransferUtility
{
    public static void ValidateChunkSize(int chunkSize)
    {
        if (chunkSize < FileTransferRules.MinChunkSize || chunkSize > FileTransferRules.MaxChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), ErrorCodes.InvalidChunkSize);
        }
    }

    public static int CalculateTotalChunks(long fileSize, int chunkSize)
    {
        ValidateChunkSize(chunkSize);

        if (fileSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSize), ErrorCodes.InvalidFileSize);
        }

        if (fileSize == 0)
        {
            return 1;
        }

        return (int)Math.Ceiling(fileSize / (double)chunkSize);
    }

    public static int GetChunkLength(long fileSize, int chunkSize, int chunkIndex)
    {
        ValidateChunkSize(chunkSize);

        var totalChunks = CalculateTotalChunks(fileSize, chunkSize);
        ValidateChunkOrder(chunkIndex, totalChunks);

        var offset = chunkIndex * (long)chunkSize;
        var remaining = fileSize - offset;
        return remaining <= 0 ? 0 : (int)Math.Min(chunkSize, remaining);
    }

    public static void ValidatePacketMetadata(FilePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (string.IsNullOrWhiteSpace(packet.FileName))
        {
            throw new InvalidOperationException(ErrorCodes.InvalidFileName);
        }

        if (packet.FileSize < 0)
        {
            throw new InvalidOperationException(ErrorCodes.InvalidFileSize);
        }

        if (packet.TotalChunks <= 0)
        {
            throw new InvalidOperationException(ErrorCodes.InvalidTotalChunks);
        }

        ValidateChunkOrder(packet.ChunkIndex, packet.TotalChunks);

        if (packet.Content.Length == 0 && packet.FileSize > 0)
        {
            throw new InvalidOperationException(ErrorCodes.EmptyChunkPayload);
        }

        if (packet.DataLength != packet.Content.Length)
        {
            throw new InvalidOperationException(ErrorCodes.InvalidFilePayloadLength);
        }

        if (string.IsNullOrWhiteSpace(packet.Checksum))
        {
            throw new InvalidOperationException(ErrorCodes.ChecksumRequired);
        }
    }

    public static void ValidateChunkOrder(int chunkIndex, int totalChunks)
    {
        if (totalChunks <= 0)
        {
            throw new InvalidOperationException(ErrorCodes.InvalidTotalChunks);
        }

        if (chunkIndex < 0 || chunkIndex >= totalChunks)
        {
            throw new InvalidOperationException(ErrorCodes.InvalidChunkIndex);
        }
    }
}
