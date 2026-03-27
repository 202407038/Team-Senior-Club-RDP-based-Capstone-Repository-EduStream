using System.IO;
using System.Threading;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Core.Utils;

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
            Checksum = ChecksumUtility.ComputeSha256(content)
        };

        packet.DataLength = _serializer.Serialize(packet).Length;
        _logSink.Write($"파일 패킷 생성: {packet.FileName}, 크기={packet.FileSize} byte");
        return packet;
    }

    public async Task<IReadOnlyList<FilePacket>> BuildFilePacketsAsync(string filePath, int chunkSize = DefaultChunkSize)
    {
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), ErrorCodes.InvalidChunkSize);
        }

        var content = await File.ReadAllBytesAsync(filePath);
        var checksum = ChecksumUtility.ComputeSha256(content);
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

    /// <summary>
    /// 파일을 청크로 분할한 뒤, 청크를 생성 순서대로 전달합니다.
    /// </summary>
    public async Task DistributeFileAsync(
        string filePath,
        Func<FilePacket, Task> sendPacketAsync,
        int chunkSize = DefaultChunkSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sendPacketAsync);

        var packets = await BuildFilePacketsAsync(filePath, chunkSize);
        foreach (var packet in packets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sendPacketAsync(packet);
        }
    }
}
