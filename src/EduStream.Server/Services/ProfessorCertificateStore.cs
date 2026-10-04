using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EduStream.Server.Services;

/// <summary>
/// 2번 구현: 보호 채널용 교수자 TLS 인증서를 관리합니다. 자체 서명 인증서를 현재 사용자 인증서 저장소(My)에 두어
/// 앱을 다시 켜도 같은 접속 코드가 유지되게 합니다. 신뢰 루트(Root)에는 등록하지 않습니다.
/// </summary>
/// <remarks>
/// 학생 쪽 신뢰는 인증서 체인이 아니라 교수자 화면의 접속 코드(인증서 지문) 비교로만 결정합니다.
/// </remarks>
public static class ProfessorCertificateStore
{
    public const string SubjectName = "CN=EduStream Professor";
    private static readonly TimeSpan Validity = TimeSpan.FromDays(365 * 5);
    // 만료가 가까우면 강의 도중 만료되지 않게 미리 새로 만든다(접속 코드가 바뀐다).
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    /// <summary>저장소의 유효한 인증서를 쓰고, 없거나 만료가 가까우면 새로 만들어 저장합니다.</summary>
    public static X509Certificate2 LoadOrCreate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var now = DateTime.Now;
        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, SubjectName, validOnly: false)
            .Where(certificate => certificate.HasPrivateKey && certificate.NotAfter - RenewBefore > now && certificate.NotBefore <= now)
            .OrderByDescending(certificate => certificate.NotAfter)
            .FirstOrDefault();
        if (existing is not null) return existing;

        var created = Create(X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        store.Add(created);
        return created;
    }

    /// <summary>
    /// 저장소에 남기지 않는 인증서입니다. 테스트와 저장소 접근 실패 시 대체용이며, 앱을 다시 켜면 접속 코드가 바뀝니다.
    /// </summary>
    /// <remarks>SChannel은 EphemeralKeySet 키를 쓰지 못해 PersistKeySet 없이 가져옵니다. 키는 Dispose 때 지워집니다.</remarks>
    public static X509Certificate2 CreateEphemeral() => Create(X509KeyStorageFlags.UserKeySet);

    private static X509Certificate2 Create(X509KeyStorageFlags storageFlags)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(SubjectName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // 서버 인증
        var now = DateTimeOffset.Now;
        using var generated = request.CreateSelfSigned(now.AddMinutes(-5), now.Add(Validity));
        // Windows SslStream은 CreateSelfSigned의 임시 키를 쓰지 못하므로 PFX로 한 번 다시 가져온다.
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pfx = generated.Export(X509ContentType.Pfx, password);
        try
        {
            return new X509Certificate2(pfx, password, storageFlags);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }
}
