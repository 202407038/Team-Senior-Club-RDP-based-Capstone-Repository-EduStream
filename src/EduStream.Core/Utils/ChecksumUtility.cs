using System.Security.Cryptography;

namespace EduStream.Core.Utils;

/// <summary>
/// 파일 및 바이너리 데이터의 SHA-256 체크섬 계산을 공통으로 담당합니다.
/// </summary>
public static class ChecksumUtility
{
    public static string ComputeSha256(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(content));
    }

    public static bool VerifySha256(byte[] content, string expectedChecksum)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (string.IsNullOrWhiteSpace(expectedChecksum))
        {
            return false;
        }

        var actualChecksum = ComputeSha256(content);
        return string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase);
    }
}
