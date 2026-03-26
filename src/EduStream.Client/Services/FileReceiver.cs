using System.Collections.Generic;
using System.IO;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Utils;

namespace EduStream.Client.Services;

/// <summary>
/// 파일 수신 후 체크섬 검증과 저장 경로 결정을 담당합니다.
/// </summary>
public sealed class FileReceiver
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, FileTransferBuffer> _buffers = new();

    private sealed class FileTransferBuffer
    {
        public required string FileName { get; init; }
        public required long FileSize { get; init; }
        public required string Checksum { get; init; }
        public required int TotalChunks { get; init; }
        public required byte[][] Chunks { get; init; }
        public required bool[] Received { get; init; }
        public int ReceivedCount { get; set; }
    }

    public async Task<string> SaveAsync(FilePacket packet, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        if (!packet.IsChunkedTransfer)
        {
            var savePath = Path.Combine(targetDirectory, packet.FileName);
            if (!ChecksumUtility.VerifySha256(packet.Content, packet.Checksum))
            {
                throw new InvalidOperationException(ErrorCodes.ChecksumMismatch);
            }

            await File.WriteAllBytesAsync(savePath, packet.Content);
            return savePath;
        }

        FileTransferBuffer? bufferToFinalize = null;
        lock (_syncRoot)
        {
            if (packet.TotalChunks <= 1)
            {
                // safe-guard: chunked-transfer 판정과 불일치하면 처리하지 않습니다.
                throw new InvalidOperationException(ErrorCodes.InvalidChunkSize);
            }

            if (packet.ChunkIndex < 0 || packet.ChunkIndex >= packet.TotalChunks)
            {
                throw new InvalidOperationException(ErrorCodes.InvalidChunkSize);
            }

            if (!_buffers.TryGetValue(packet.TransferId, out var buffer))
            {
                buffer = new FileTransferBuffer
                {
                    FileName = packet.FileName,
                    FileSize = packet.FileSize,
                    Checksum = packet.Checksum,
                    TotalChunks = packet.TotalChunks,
                    Chunks = new byte[packet.TotalChunks][],
                    Received = new bool[packet.TotalChunks],
                    ReceivedCount = 0
                };
                _buffers.Add(packet.TransferId, buffer);
            }
            else
            {
                // 메타데이터가 청크 사이에서 달라지면 조립이 불가능합니다.
                if (!string.Equals(buffer.FileName, packet.FileName, StringComparison.Ordinal) ||
                    buffer.FileSize != packet.FileSize ||
                    !string.Equals(buffer.Checksum, packet.Checksum, StringComparison.OrdinalIgnoreCase) ||
                    buffer.TotalChunks != packet.TotalChunks)
                {
                    throw new InvalidOperationException("Inconsistent file transfer metadata across chunks.");
                }
            }

            if (!buffer.Received[packet.ChunkIndex])
            {
                buffer.Received[packet.ChunkIndex] = true;
                buffer.Chunks[packet.ChunkIndex] = packet.Content;
                buffer.ReceivedCount++;
            }

            if (buffer.ReceivedCount == buffer.TotalChunks)
            {
                // 마지막 청크 도착 시 조립/검증/저장을 수행합니다.
                bufferToFinalize = buffer;
                _buffers.Remove(packet.TransferId);
            }
        }

        if (bufferToFinalize is null)
        {
            // 중간 청크 단계입니다.
            return string.Empty;
        }

        if (bufferToFinalize.FileSize > int.MaxValue)
        {
            throw new NotSupportedException("File size is too large to assemble in memory.");
        }

        var combined = new byte[bufferToFinalize.FileSize];
        var offset = 0L;
        for (var i = 0; i < bufferToFinalize.TotalChunks; i++)
        {
            var chunk = bufferToFinalize.Chunks[i] ?? throw new InvalidOperationException("Missing chunk while finalizing transfer.");
            Buffer.BlockCopy(chunk, 0, combined, (int)offset, chunk.Length);
            offset += chunk.Length;
        }

        if (offset != bufferToFinalize.FileSize)
        {
            throw new InvalidOperationException("Combined file size does not match expected file size.");
        }

        if (!ChecksumUtility.VerifySha256(combined, bufferToFinalize.Checksum))
        {
            throw new InvalidOperationException(ErrorCodes.ChecksumMismatch);
        }

        var finalPath = Path.Combine(targetDirectory, bufferToFinalize.FileName);
        var tempPath = Path.Combine(targetDirectory, $"{bufferToFinalize.FileName}.{packet.TransferId:N}.part");
        await File.WriteAllBytesAsync(tempPath, combined);

        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }

        File.Move(tempPath, finalPath);
        return finalPath;
    }
}
