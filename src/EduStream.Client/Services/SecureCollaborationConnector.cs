using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.Core.Network;

namespace EduStream.Client.Services;

/// <summary>
/// 신뢰하는 교실 LAN에서 입력한 IP로 TLS 연결합니다. 별도 접속 코드나 신뢰 확인창은 사용하지 않습니다.
/// TLS 암호화는 유지하지만 인증서 지문 대조는 하지 않으므로 능동적 중간자 공격에 대한 서버 신원 보장은 없습니다.
/// 방 비밀번호·참가 티켓·참가자 권한 검증은 상위 계층에서 유지하며 평문으로 대체하지 않습니다.
/// </summary>
public static class SecureCollaborationConnector
{
    // SNI용 고정 이름. LAN 주소의 서버 신원을 증명하는 이름이 아닙니다.
    private const string TargetHost = "edustream-professor";

    /// <exception cref="CollaborationException">
    /// InvalidRequest: 주소/포트 형식 오류, NotAuthorized: 사용할 수 없는 TLS 인증서,
    /// UnsupportedCapability: 교수자 앱 버전 불일치.
    /// </exception>
    public static async Task<SecureCollaborationConnection> ConnectAsync(string host, int port,
        ILogSink logSink, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        if (!System.Net.IPAddress.TryParse(host, out var address) ||
            address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any) ||
            address.Equals(System.Net.IPAddress.Broadcast) || port is < 1 or > 65535)
            throw new CollaborationException(CollaborationError.InvalidRequest);

        // 2026-10-04 이전 수동 코드 방식(사용자 요청으로 주석 보존, 실행하지 않음):
        // if (!ConnectionCode.TryNormalize(connectionCode, out var expectedCode))
        //     throw new CollaborationException(CollaborationError.InvalidRequest);
        // 이전 RemoteCertificateValidationCallback의 지문 대조:
        // var matches = certificate is not null &&
        //     ConnectionCode.Matches(expectedCode, certificate as X509Certificate2 ?? new X509Certificate2(certificate));
        // codeMismatch = !matches;
        // return matches;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? CollaborationHandshake.DefaultTimeout);

        var client = new TcpClient();
        SslStream? ssl = null;
        var invalidCertificate = false;
        try
        {
            await client.ConnectAsync(address, port, deadline.Token);
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = TargetHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    // 자체 서명 LAN 인증서의 유효기간·서버 용도는 검사하되, 알려진 교수자 신원이라고 주장하지 않습니다.
                    var usable = LanServerCertificatePolicy.IsUsable(certificate);
                    invalidCertificate = !usable;
                    return usable;
                }
            }, deadline.Token);

            await CollaborationHandshake.SendAsync(ssl, ParticipantRole.Student, deadline.Token);
            await CollaborationHandshake.ReceiveAsync(ssl, ParticipantRole.Professor, deadline.Token);

            var connection = new SecureCollaborationConnection(ssl, client, logSink);
            logSink.Write($"[Secure] LAN TLS 연결: {host}:{port} (접속 코드 신원 대조 없음)");
            return connection;
        }
        catch (Exception ex)
        {
            try { ssl?.Dispose(); } catch { }
            client.Dispose();
            if (ex is AuthenticationException && invalidCertificate)
            {
                logSink.Write("[Secure] 유효하지 않은 TLS 서버 인증서로 연결 중단");
                throw new CollaborationException(CollaborationError.NotAuthorized);
            }
            if (ex is IOException && ssl is not null && ssl.IsAuthenticated)
            {
                // 협상 중 교수자가 연결을 닫음: capability 거부로 본다.
                throw new CollaborationException(CollaborationError.UnsupportedCapability);
            }
            throw;
        }
    }
}
