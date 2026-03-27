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
        Directory.CreateDirectory(targetDirectory);
        var savePath = Path.Combine(targetDirectory, packet.FileName);
        if (!ChecksumUtility.VerifySha256(packet.Content, packet.Checksum))
        {
            throw new InvalidOperationException(ErrorCodes.ChecksumMismatch);
        }

        await File.WriteAllBytesAsync(savePath, packet.Content);
        return savePath;
    }
}
