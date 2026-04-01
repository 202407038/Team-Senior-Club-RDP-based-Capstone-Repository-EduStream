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
    public async Task<string> SaveAsync(FilePacket packet, string targetDirectory)
    {
        var result = await TrySaveAsync(packet, targetDirectory);
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
            FileTransferUtility.ValidatePacketMetadata(packet);

            if (!ChecksumUtility.VerifySha256(packet.Content, packet.Checksum))
            {
                return FileReceiveResult.CreateFailure(ErrorCodes.ChecksumMismatch, "체크섬 불일치. 데이터가 손상되었거나 중복 전송이 발생했습니다.");
            }

            Directory.CreateDirectory(targetDirectory);
            var savePath = Path.Combine(targetDirectory, packet.FileName);

            await File.WriteAllBytesAsync(savePath, packet.Content);
            return FileReceiveResult.CreateSuccess(savePath);
        }
        catch (ArgumentNullException ex)
        {
            return FileReceiveResult.CreateFailure(ErrorCodes.InvalidFileName, ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message == ErrorCodes.ChecksumMismatch ||
                                                  ex.Message == ErrorCodes.ChecksumRequired ||
                                                  ex.Message == ErrorCodes.InvalidFileName ||
                                                  ex.Message == ErrorCodes.InvalidFileSize ||
                                                  ex.Message == ErrorCodes.InvalidTotalChunks ||
                                                  ex.Message == ErrorCodes.InvalidChunkIndex ||
                                                  ex.Message == ErrorCodes.EmptyChunkPayload)
        {
            return FileReceiveResult.CreateFailure(ex.Message, "파일 패킷 메타데이터 검증 실패 또는 유효하지 않은 청크 정보입니다.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return FileReceiveResult.CreateFailure("FILE_WRITE_PERMISSION_DENIED", ex.Message);
        }
        catch (PathTooLongException ex)
        {
            return FileReceiveResult.CreateFailure("FILE_PATH_TOO_LONG", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return FileReceiveResult.CreateFailure("DIRECTORY_NOT_FOUND", ex.Message);
        }
        catch (IOException ex)
        {
            return FileReceiveResult.CreateFailure("FILE_IO_ERROR", ex.Message);
        }
        catch (Exception ex)
        {
            return FileReceiveResult.CreateFailure("UNKNOWN_ERROR", ex.Message);
        }
    }
}
