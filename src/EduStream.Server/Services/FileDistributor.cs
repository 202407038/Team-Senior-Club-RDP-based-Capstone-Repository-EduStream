using System.IO;
using System.Security.Cryptography;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Serialization;

namespace EduStream.Server.Services;

/// <summary>
/// 파일 전송 전 체크섬 생성과 패킷 래핑을 담당합니다.
/// </summary>
public sealed class FileDistributor
{
    private readonly PacketSerializer _serializer;
    private readonly ILogSink _logSink;
    private const int DefaultChunkSize = 64 * 1024;

    public FileDistributor(PacketSerializer serializer, ILogSink logSink)
    {
        _serializer = serializer;
        _logSink = logSink;
    }

    public async Task<FilePacket> BuildFilePacketAsync(string filePath)
    {
        var content = await File.ReadAllBytesAsync(filePath);
        var packet = new FilePacket
        {
            FileName = Path.GetFileName(filePath),
            FileSize = content.LongLength,
            Content = content,
            Checksum = Convert.ToHexString(SHA256.HashData(content))
        };

        packet.DataLength = _serializer.Serialize(packet).Length;
        _logSink.Write($"파일 패킷 생성: {packet.FileName}, 크기={packet.FileSize} byte");
        return packet;
    }

    public async Task<IReadOnlyList<FilePacket>> BuildFilePacketsAsync(string filePath, int chunkSize = DefaultChunkSize)
    {
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "청크 크기는 1 이상이어야 합니다.");
        }

        var content = await File.ReadAllBytesAsync(filePath);
        var checksum = Convert.ToHexString(SHA256.HashData(content));
        var transferId = Guid.NewGuid();
        var totalChunks = (int)Math.Ceiling(content.Length / (double)chunkSize);
        var packets = new List<FilePacket>(totalChunks);

        for (var index = 0; index < totalChunks; index++)
        {
            var offset = index * chunkSize;
            var length = Math.Min(chunkSize, content.Length - offset);
            var chunk = new byte[length];
            Array.Copy(content, offset, chunk, 0, length);

            var packet = new FilePacket
            {
                FileName = Path.GetFileName(filePath),
                FileSize = content.LongLength,
                Checksum = checksum,
                TransferId = transferId,
                ChunkIndex = index,
                TotalChunks = totalChunks,
                Content = chunk
            };

            packet.DataLength = _serializer.Serialize(packet).Length;
            packets.Add(packet);
        }

        _logSink.Write($"파일 청크 패킷 생성: {Path.GetFileName(filePath)}, 청크 수={totalChunks}, 청크 크기={chunkSize} byte");
        return packets;
    }
}
