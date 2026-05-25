using System.IO;
using EduStream.Core.Factories;
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

    public FileDistributor(PacketSerializer serializer, ILogSink logSink)
    {
        _serializer = serializer;
        _logSink = logSink;
    }

    public async Task<FilePacket> BuildFilePacketAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("파일 경로가 비어 있습니다.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("전송할 파일을 찾을 수 없습니다.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException("전송할 파일이 비어 있습니다.");
        }

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

    public async Task<IReadOnlyList<FilePacket>> BuildFilePacketsAsync(string filePath, int chunkSize = FileTransferRules.DefaultChunkSize)
    {
        return await BuildFilePacketsAsync(filePath, "Server", null, chunkSize);
    }

    public async Task<IReadOnlyList<FilePacket>> BuildFilePacketsAsync(
        string filePath,
        string senderId,
        Guid? sessionId,
        int chunkSize = FileTransferRules.DefaultChunkSize)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("파일 경로가 비어 있습니다.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("전송할 파일을 찾을 수 없습니다.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException("전송할 파일이 비어 있습니다.");
        }

        var content = await File.ReadAllBytesAsync(filePath);
        FileTransferUtility.ValidateChunkSize(chunkSize);
        var checksum = ChecksumUtility.ComputeSha256(content);
        var transferId = Guid.NewGuid();
        var totalChunks = FileTransferUtility.CalculateTotalChunks(content.LongLength, chunkSize);
        var packets = new List<FilePacket>(totalChunks);

        for (var index = 0; index < totalChunks; index++)
        {
            var offset = index * chunkSize;
            var length = FileTransferUtility.GetChunkLength(content.LongLength, chunkSize, index);
            var chunk = new byte[length];
            Array.Copy(content, offset, chunk, 0, length);

            var packet = PacketFactory.CreateFileChunk(
                senderId: senderId,
                fileName: Path.GetFileName(filePath),
                fileSize: content.LongLength,
                checksum: checksum,
                transferId: transferId,
                chunkIndex: index,
                totalChunks: totalChunks,
                content: chunk,
                sessionId: sessionId);

            FileTransferUtility.ValidatePacketMetadata(packet);
            packet.DataLength = _serializer.Serialize(packet).Length;
            packets.Add(packet);
        }

        _logSink.Write($"파일 청크 패킷 생성: {Path.GetFileName(filePath)}, 청크 수={totalChunks}, 청크 크기={chunkSize} byte");
        return packets;
    }
}
