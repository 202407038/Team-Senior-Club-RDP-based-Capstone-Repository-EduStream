using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EduStream.Client.Services;

/// <summary>
/// 수동 코드 없는 LAN 연결의 최소 TLS 인증서 검사입니다.
/// 유효한 서버용 인증서인지 검사할 뿐, 그 인증서의 소유자가 교수자인지 보증하지 않습니다.
/// 공개 인터넷/비신뢰 LAN용 신원 검증 정책으로 사용하지 않습니다.
/// </summary>
public static class LanServerCertificatePolicy
{
    public static bool IsUsable(X509Certificate? certificate)
    {
        if (certificate is null) return false;
        try
        {
            using var server = new X509Certificate2(certificate);
            var now = DateTime.UtcNow;
            if (now < server.NotBefore.ToUniversalTime() || now >= server.NotAfter.ToUniversalTime())
                return false;
            var purposes = server.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
            if (purposes.Length != 1 ||
                !purposes[0].EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"))
                return false;
            if (server.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority))
                return false;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
