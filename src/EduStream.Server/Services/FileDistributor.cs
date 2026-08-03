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
    private readonly ILogSink _logSink;

    public FileDistributor(PacketSerializer serializer, ILogSink logSink)
    {
        ArgumentNullException.ThrowIfNull(serializer);
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

        // 💡 [수정] 0byte 파일인 경우 예외를 던지지 않고 빈 패킷 생성
        if (fileInfo.Length == 0)
        {
            var emptyContent = Array.Empty<byte>();
            var emptyPacket = PacketFactory.CreateFileChunk(
                senderId: "Server",
                fileName: Path.GetFileName(filePath),
                fileSize: 0,
                checksum: ChecksumUtility.ComputeSha256(emptyContent),
                transferId: Guid.NewGuid(),
                chunkIndex: 0,
                totalChunks: 1,
                content: emptyContent);

            FileTransferUtility.ValidatePacketMetadata(emptyPacket);
            _logSink.Write($"빈 파일 패킷 생성: {emptyPacket.FileName}, 크기=0 byte");
            return emptyPacket;
        }

        var content = await File.ReadAllBytesAsync(filePath);
        var packet = PacketFactory.CreateFileChunk(
            senderId: "Server",
            fileName: Path.GetFileName(filePath),
            fileSize: content.LongLength,
            checksum: ChecksumUtility.ComputeSha256(content),
            transferId: Guid.NewGuid(),
            chunkIndex: 0,
            totalChunks: 1,
            content: content);

        FileTransferUtility.ValidatePacketMetadata(packet);
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

        FileTransferUtility.ValidateChunkSize(chunkSize);
        var fileInfo = new FileInfo(filePath);

        // 💡 [수정] 0byte 파일인 경우 단일 0byte 청크 패킷 리스트 반환
        if (fileInfo.Length == 0)
        {
            var emptyContent = Array.Empty<byte>();
            var emptyPacket = PacketFactory.CreateFileChunk(
                senderId: senderId,
                fileName: Path.GetFileName(filePath),
                fileSize: 0,
                checksum: ChecksumUtility.ComputeSha256(emptyContent),
                transferId: Guid.NewGuid(),
                chunkIndex: 0,
                totalChunks: 1,
                content: emptyContent,
                sessionId: sessionId);

            FileTransferUtility.ValidatePacketMetadata(emptyPacket);
            _logSink.Write($"빈 파일 청크 패킷 생성: {Path.GetFileName(filePath)}, 청크 수=1, 청크 크기=0 byte");
            return new List<FilePacket> { emptyPacket };
        }

        var content = await File.ReadAllBytesAsync(filePath);
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
            packets.Add(packet);
        }

        _logSink.Write($"파일 청크 패킷 생성: {Path.GetFileName(filePath)}, 청크 수={totalChunks}, 청크 크기={chunkSize} byte");
        return packets;
    }
}
