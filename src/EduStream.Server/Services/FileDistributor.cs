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
        string filePath, string senderId, Guid? sessionId,
        int chunkSize = FileTransferRules.DefaultChunkSize)
    {
        var packets = new List<FilePacket>();
        await foreach (var packet in StreamFilePacketsAsync(filePath, senderId, sessionId, chunkSize))
            packets.Add(packet);
        return packets;
    }

    /// <summary>
    /// 파일 전체 복사 없이 청크를 순차 생성합니다. 호출자는 즉시 송신하고 보관하지 않아야 합니다.
    /// 기존 목록 API는 호환용이며, RDP 병행 송신 측은 이 API와 취소 토큰을 사용합니다.
    /// </summary>
    public async IAsyncEnumerable<FilePacket> StreamFilePacketsAsync(
        string filePath, string senderId, Guid? sessionId,
        int chunkSize = FileTransferRules.DefaultChunkSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("파일 경로가 비어 있습니다.", nameof(filePath));
        FileTransferUtility.ValidateChunkSize(chunkSize);
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, chunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var fileSize = stream.Length;
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
        var checksum = Convert.ToHexString(hash).ToLowerInvariant();
        stream.Position = 0;
        var transferId = Guid.NewGuid();
        var totalChunks = FileTransferUtility.CalculateTotalChunks(fileSize, chunkSize);
        for (var index = 0; index < totalChunks; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = new byte[FileTransferUtility.GetChunkLength(fileSize, chunkSize, index)];
            await stream.ReadExactlyAsync(content, cancellationToken);
            var packet = PacketFactory.CreateFileChunk(senderId, Path.GetFileName(filePath),
                fileSize, checksum, transferId, index, totalChunks, content, sessionId);
            FileTransferUtility.ValidatePacketMetadata(packet);
            yield return packet;
        }
        _logSink.Write($"파일 청크 생성 완료: {Path.GetFileName(filePath)}, 청크 수={totalChunks}");
    }
}
