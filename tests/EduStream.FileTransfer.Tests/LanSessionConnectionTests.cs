using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EduStream.Client.Services;
using EduStream.Core.Logging;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class LanSessionConnectionTests
{
    [Fact]
    public void LanPolicy_RejectsMissingCertificate()
        => Assert.False(LanServerCertificatePolicy.IsUsable(null));

    [Theory]
    [InlineData(-2, -1, "1.3.6.1.5.5.7.3.1", false)]
    [InlineData(1, 2, "1.3.6.1.5.5.7.3.1", false)]
    [InlineData(-1, 1, "1.3.6.1.5.5.7.3.2", false)]
    [InlineData(-1, 1, "", false)]
    [InlineData(-1, 1, "1.3.6.1.5.5.7.3.1", true)]
    public void LanPolicy_RejectsInvalidDatesPurposeOrCa(int fromDays, int toDays, string purpose, bool ca)
    {
        using var certificate = CreateCertificate(fromDays, toDays, purpose, ca);
        Assert.False(LanServerCertificatePolicy.IsUsable(certificate));
    }

    [Fact]
    public void LanPolicy_AcceptsDifferentValidSelfSignedServers_WithoutPretendingIdentityPinning()
    {
        using var first = ProfessorCertificateStore.CreateEphemeral();
        using var second = ProfessorCertificateStore.CreateEphemeral();
        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
        Assert.True(LanServerCertificatePolicy.IsUsable(first));
        Assert.True(LanServerCertificatePolicy.IsUsable(second));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65535)]
    [InlineData(-1)]
    public async Task InvalidSessionPort_IsRejectedBeforePasswordTransmission(int port)
    {
        var error = await Assert.ThrowsAsync<SecureJoinException>(() =>
            SecureRoomJoinClient.AuthenticateAsync("127.0.0.1", port, "Alice", "unused-password".AsMemory(),
                new InMemoryLogSink()));
        Assert.Equal(SecureJoinFailure.InvalidAddress, error.Failure);
    }

    [Fact]
    public async Task ExpiredServerCertificate_StopsRealTlsBeforeRoomPassword()
    {
        using var certificate = CreateCertificate(-2, -1, "1.3.6.1.5.5.7.3.1", false);
        var log = new InMemoryLogSink();
        await using var listener = new SecureCollaborationListener(certificate, log);
        var accepted = 0;
        listener.ConnectionAccepted += _ => { Interlocked.Increment(ref accepted); return Task.CompletedTask; };
        listener.Start(0);
        var error = await Assert.ThrowsAsync<SecureJoinException>(() =>
            SecureRoomJoinClient.AuthenticateAsync("127.0.0.1", listener.Port - 1, "Alice",
                "never-send-this".AsMemory(), log, TimeSpan.FromSeconds(5)));
        Assert.Equal(SecureJoinFailure.InvalidCertificate, error.Failure);
        Assert.Equal(0, Volatile.Read(ref accepted));
        Assert.DoesNotContain(log.Snapshot(), line => line.Contains("never-send-this"));
    }

    private static X509Certificate2 CreateCertificate(int fromDays, int toDays, string purpose, bool ca)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=LAN Test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        if (purpose.Length != 0)
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(purpose) }, false));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(fromDays), now.AddDays(toDays));
        return new X509Certificate2(created.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet);
    }
}
