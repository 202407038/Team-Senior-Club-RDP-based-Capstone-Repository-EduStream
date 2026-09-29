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
/// 2번 구현: 학생 앱의 보호 채널 연결. 교수자 인증서는 체인이 아니라 학생이 입력한 접속 코드와의 일치로만 신뢰합니다.
/// 코드가 틀리거나 비어 있으면 연결하지 않으며, 평문 연결로 대체하지 않습니다.
/// </summary>
public static class SecureCollaborationConnector
{
    // SNI용 고정 이름. 신뢰 판단에는 쓰지 않는다.
    private const string TargetHost = "edustream-professor";

    /// <exception cref="CollaborationException">
    /// InvalidRequest: 접속 코드 형식 오류, NotAuthorized: 코드 불일치(다른 PC이거나 중간 가로채기),
    /// UnsupportedCapability: 교수자 앱 버전 불일치.
    /// </exception>
    public static async Task<SecureCollaborationConnection> ConnectAsync(string host, int port, string connectionCode,
        ILogSink logSink, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(logSink);
        if (!ConnectionCode.TryNormalize(connectionCode, out var expectedCode))
            throw new CollaborationException(CollaborationError.InvalidRequest);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? CollaborationHandshake.DefaultTimeout);

        var client = new TcpClient();
        SslStream? ssl = null;
        var codeMismatch = false;
        try
        {
            await client.ConnectAsync(host, port, deadline.Token);
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = TargetHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    // 자체 서명이라 체인·이름 오류는 항상 난다. 대신 접속 코드(지문)가 맞아야만 통과시킨다.
                    var matches = certificate is not null &&
                        ConnectionCode.Matches(expectedCode, certificate as X509Certificate2 ?? new X509Certificate2(certificate));
                    codeMismatch = !matches;
                    return matches;
                }
            }, deadline.Token);

            await CollaborationHandshake.SendAsync(ssl, ParticipantRole.Student, deadline.Token);
            await CollaborationHandshake.ReceiveAsync(ssl, ParticipantRole.Professor, deadline.Token);

            var connection = new SecureCollaborationConnection(ssl, client, logSink);
            logSink.Write($"[Secure] 교수자 보호 채널 연결: {host}:{port}");
            return connection;
        }
        catch (Exception ex)
        {
            try { ssl?.Dispose(); } catch { }
            client.Dispose();
            if (ex is AuthenticationException && codeMismatch)
            {
                logSink.Write("[Secure] 접속 코드 불일치로 연결 중단");
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
