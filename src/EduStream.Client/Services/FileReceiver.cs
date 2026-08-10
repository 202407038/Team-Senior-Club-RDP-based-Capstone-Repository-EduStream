using System.IO;
using System.Linq;
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
    private readonly Dictionary<Guid, ChunkBuffer> _chunkBuffers = new();

    public async Task<string> SaveAsync(FilePacket packet, string targetDirectory)
    {
        var result = await TrySaveAsync(packet, targetDirectory);
        if (result.Pending)
        {
            throw new InvalidOperationException($"{ErrorCodes.FileChunkPending}: {result.ErrorMessage}");
        }

        if (!result.Success)
        {
            throw new InvalidOperationException($"{result.ErrorCode}: {result.ErrorMessage}");
        }

        return result.FilePath!;
    }

    public async Task<FileReceiveResult> TrySaveAsync(FilePacket packet, string targetDirectory)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(packet);
            FileTransferUtility.ValidatePacketMetadata(packet);
            var targetPath = ResolveTargetPath(targetDirectory, packet.FileName);

            if (packet.FileSize == 0)
            {
                if (!ChecksumUtility.VerifySha256(packet.Content, packet.Checksum))
                {
                    return FileReceiveResult.CreateFailure(
                        ErrorCodes.ChecksumMismatch,
                        "빈 파일 체크섬 검증에 실패했습니다.",
                        canRetry: true);
                }

                Directory.CreateDirectory(targetDirectory);
                await File.WriteAllBytesAsync(targetPath, Array.Empty<byte>());

                return FileReceiveResult.CreateSuccess(
                    targetPath,
                    $"{packet.FileName} (0byte) 저장 완료",
                    receivedChunkCount: 1,
                    totalChunks: 1);
            }

            if (!packet.IsChunkedTransfer)
            {
                if (!ChecksumUtility.VerifySha256(packet.Content, packet.Checksum))
                {
                    return FileReceiveResult.CreateFailure(
                        ErrorCodes.ChecksumMismatch,
                        "체크섬 불일치. 데이터가 손상되었거나 중복 전송이 발생했습니다.",
                        canRetry: true);
                }

                Directory.CreateDirectory(targetDirectory);
                await File.WriteAllBytesAsync(targetPath, packet.Content);
                return FileReceiveResult.CreateSuccess(
                    targetPath,
                    $"{packet.FileName} 저장 완료",
                    receivedChunkCount: 1,
                    totalChunks: 1);
            }

            var addResult = AddChunkAndTryAssemble(packet);
            if (!string.IsNullOrWhiteSpace(addResult.ErrorCode))
            {
                return FileReceiveResult.CreateFailure(
                    addResult.ErrorCode,
                    addResult.ErrorMessage ?? "청크 처리 중 오류가 발생했습니다.",
                    canRetry: true);
            }

            if (addResult.Pending)
            {
                return FileReceiveResult.CreatePending(
                    "파일 청크를 수신 중입니다.",
                    addResult.ReceivedChunkCount,
                    packet.TotalChunks);
            }

            if (addResult.AssembledContent is null)
            {
                RemoveChunkBuffer(packet.TransferId);
                return FileReceiveResult.CreateFailure(
                    ErrorCodes.FileAssemblyFailed,
                    "파일 조립 결과가 비어 있습니다.",
                    canRetry: true);
            }

            if (!ChecksumUtility.VerifySha256(addResult.AssembledContent, packet.Checksum))
            {
                RemoveChunkBuffer(packet.TransferId);
                return FileReceiveResult.CreateFailure(
                    ErrorCodes.ChecksumMismatch,
                    "청크 조립 후 체크섬 검증에 실패했습니다.",
                    canRetry: true,
                    receivedChunkCount: packet.TotalChunks,
                    totalChunks: packet.TotalChunks);
            }

            Directory.CreateDirectory(targetDirectory);
            await File.WriteAllBytesAsync(targetPath, addResult.AssembledContent);
            return FileReceiveResult.CreateSuccess(
                targetPath,
                $"{packet.FileName} 저장 완료",
                packet.TotalChunks,
                packet.TotalChunks);
        }
        catch (ArgumentNullException ex)
        {
            RemoveChunkBuffer(packet?.TransferId ?? Guid.Empty);
            return FileReceiveResult.CreateFailure(ErrorCodes.InvalidFileName, ex.Message);
        }
        catch (ArgumentException ex)
        {
            RemoveChunkBuffer(packet.TransferId);
            return FileReceiveResult.CreateFailure(ErrorCodes.InvalidFileName, ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message == ErrorCodes.ChecksumMismatch ||
                                                   ex.Message == ErrorCodes.ChecksumRequired ||
                                                   ex.Message == ErrorCodes.InvalidFileName ||
                                                   ex.Message == ErrorCodes.InvalidFileSize ||
                                                   ex.Message == ErrorCodes.InvalidTotalChunks ||
                                                   ex.Message == ErrorCodes.InvalidChunkIndex ||
                                                   ex.Message == ErrorCodes.InvalidFilePayloadLength ||
                                                   ex.Message == ErrorCodes.EmptyChunkPayload ||
                                                   ex.Message == ErrorCodes.FileChunkPending ||
                                                   ex.Message == ErrorCodes.FileChunkMetadataMismatch ||
                                                   ex.Message == ErrorCodes.FileAssemblyFailed)
        {
            RemoveChunkBuffer(packet.TransferId);
            return FileReceiveResult.CreateFailure(
                ex.Message,
                "파일 패킷 메타데이터 검증 실패 또는 유효하지 않은 청크 정보입니다.",
                canRetry: IsRetryable(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            RemoveChunkBuffer(packet.TransferId);
            return FileReceiveResult.CreateFailure("FILE_WRITE_PERMISSION_DENIED", ex.Message);
        }
        catch (PathTooLongException ex)
        {
            RemoveChunkBuffer(packet.TransferId);
            return FileReceiveResult.CreateFailure("FILE_PATH_TOO_LONG", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            RemoveChunkBuffer(packet.TransferId);
            return FileReceiveResult.CreateFailure("DIRECTORY_NOT_FOUND", ex.Message, canRetry: true);
        }
        catch (IOException ex)
        {
            RemoveChunkBuffer(packet.TransferId);
            return FileReceiveResult.CreateFailure("FILE_IO_ERROR", ex.Message, canRetry: true);
        }
        catch (Exception ex)
        {
            RemoveChunkBuffer(packet.TransferId);
            return FileReceiveResult.CreateFailure("UNKNOWN_ERROR", ex.Message);
        }
    }

    private static string ResolveTargetPath(string targetDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("대상 폴더가 비어 있습니다.", nameof(targetDirectory));
        }

        return Path.Combine(targetDirectory, fileName);
    }

    private static bool IsRetryable(string errorCode)
    {
        return errorCode is ErrorCodes.ChecksumMismatch
            or ErrorCodes.ChecksumRequired
            or ErrorCodes.InvalidChunkIndex
            or ErrorCodes.InvalidTotalChunks
            or ErrorCodes.InvalidFilePayloadLength
            or ErrorCodes.EmptyChunkPayload
            or ErrorCodes.FileChunkPending
            or ErrorCodes.FileChunkMetadataMismatch
            or ErrorCodes.FileAssemblyFailed;
    }

    private void RemoveChunkBuffer(Guid transferId)
    {
        if (transferId == Guid.Empty)
        {
            return;
        }

        lock (_syncRoot)
        {
            _chunkBuffers.Remove(transferId);
        }
    }

    private ChunkAddResult AddChunkAndTryAssemble(FilePacket packet)
    {
        lock (_syncRoot)
        {
            if (!_chunkBuffers.TryGetValue(packet.TransferId, out var buffer))
            {
                buffer = new ChunkBuffer(packet.FileName, packet.FileSize, packet.Checksum, packet.TotalChunks);
                _chunkBuffers[packet.TransferId] = buffer;
            }

            if (!string.Equals(buffer.FileName, packet.FileName, StringComparison.Ordinal) ||
                buffer.FileSize != packet.FileSize ||
                !string.Equals(buffer.Checksum, packet.Checksum, StringComparison.Ordinal) ||
                buffer.TotalChunks != packet.TotalChunks)
            {
                _chunkBuffers.Remove(packet.TransferId);
                return ChunkAddResult.Failure(ErrorCodes.FileChunkMetadataMismatch, "전송 메타데이터가 청크 사이에서 일치하지 않습니다.");
            }

            var existingChunk = buffer.Chunks[packet.ChunkIndex];
            if (existingChunk is not null)
            {
                if (!existingChunk.SequenceEqual(packet.Content))
                {
                    _chunkBuffers.Remove(packet.TransferId);
                    return ChunkAddResult.Failure(ErrorCodes.FileChunkMetadataMismatch, "동일 인덱스 청크가 서로 다른 내용으로 수신되었습니다.");
                }

                return ChunkAddResult.CreatePending(buffer.ReceivedChunkCount);
            }

            buffer.Chunks[packet.ChunkIndex] = packet.Content.ToArray();
            buffer.ReceivedChunkCount++;

            if (buffer.ReceivedChunkCount < buffer.TotalChunks)
            {
                return ChunkAddResult.CreatePending(buffer.ReceivedChunkCount);
            }

            try
            {
                var assembledContent = new byte[checked((int)buffer.FileSize)];
                var offset = 0;
                for (var index = 0; index < buffer.TotalChunks; index++)
                {
                    var chunk = buffer.Chunks[index] ?? Array.Empty<byte>();
                    Array.Copy(chunk, 0, assembledContent, offset, chunk.Length);
                    offset += chunk.Length;
                }

                if (offset != buffer.FileSize)
                {
                    _chunkBuffers.Remove(packet.TransferId);
                    return ChunkAddResult.Failure(ErrorCodes.FileAssemblyFailed, "조립된 파일 크기가 메타데이터와 일치하지 않습니다.");
                }

                _chunkBuffers.Remove(packet.TransferId);
                return ChunkAddResult.Completed(assembledContent, buffer.ReceivedChunkCount);
            }
            catch (OverflowException)
            {
                _chunkBuffers.Remove(packet.TransferId);
                return ChunkAddResult.Failure(ErrorCodes.FileAssemblyFailed, "파일 크기가 너무 커서 조립할 수 없습니다.");
            }
        }
    }

    private sealed class ChunkBuffer
    {
        public ChunkBuffer(string fileName, long fileSize, string checksum, int totalChunks)
        {
            FileName = fileName;
            FileSize = fileSize;
            Checksum = checksum;
            TotalChunks = totalChunks;
            Chunks = new byte[totalChunks][];
        }

        public string FileName { get; }
        public long FileSize { get; }
        public string Checksum { get; }
        public int TotalChunks { get; }
        public int ReceivedChunkCount { get; set; }
        public byte[][] Chunks { get; }
    }

    private readonly record struct ChunkAddResult(bool Pending, byte[]? AssembledContent, string? ErrorCode, string? ErrorMessage, int ReceivedChunkCount)
    {
        public static ChunkAddResult CreatePending(int receivedChunkCount) => new(true, null, null, null, receivedChunkCount);

        public static ChunkAddResult Completed(byte[] assembledContent, int receivedChunkCount) => new(false, assembledContent, null, null, receivedChunkCount);

        public static ChunkAddResult Failure(string errorCode, string message) => new(false, null, errorCode, message, 0);
    }
}
