using System.IO;
using System.Security.Cryptography;
using EduStream.Core.Models;

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
        var actualChecksum = Convert.ToHexString(SHA256.HashData(packet.Content));

        if (!string.Equals(actualChecksum, packet.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("체크섬 검증에 실패했습니다.");
        }

        await File.WriteAllBytesAsync(savePath, packet.Content);
        return savePath;
    }
}
