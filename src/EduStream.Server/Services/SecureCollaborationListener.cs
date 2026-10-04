using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.Core.Network;

namespace EduStream.Server.Services;

/// <summary>
/// 2번 구현: 보호 채널 수락. TLS 인증과 capability 협상을 마친 연결만 <see cref="ConnectionAccepted"/>로 넘깁니다.
/// 평문 연결·다른 capability·제한 시간 안에 협상을 끝내지 못한 연결은 조용히 닫습니다.
/// </summary>
public sealed class SecureCollaborationListener : IAsyncDisposable
{
    // 인증 전 연결이 자원을 붙잡는 것을 막는 상한. 수용 인원 보장이 아닙니다.
    public const int MaxPendingHandshakes = 16;
    public const int MaxConnections = 64;

    private readonly X509Certificate2 _certificate;
    private readonly ILogSink _logSink;
    private readonly TimeSpan _handshakeTimeout;
    private readonly object _gate = new();
    private readonly HashSet<SecureCollaborationConnection> _connections = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private int _pendingHandshakes;

    /// <summary>협상까지 끝난 연결. 처리기는 <see cref="SecureCollaborationConnection.Start"/>를 호출해야 합니다.</summary>
    public event Func<SecureCollaborationConnection, Task>? ConnectionAccepted;

    public SecureCollaborationListener(X509Certificate2 certificate, ILogSink logSink, TimeSpan? handshakeTimeout = null)
    {
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        if (!certificate.HasPrivateKey) throw new ArgumentException("개인 키가 있는 인증서가 필요합니다.", nameof(certificate));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _handshakeTimeout = handshakeTimeout ?? CollaborationHandshake.DefaultTimeout;
        ConnectionCode = Core.Network.ConnectionCode.FromCertificate(certificate);
    }

    /// <summary>교수자 화면에 표시할 접속 코드입니다.</summary>
    public string ConnectionCode { get; }

    /// <summary>실제 수신 포트입니다. 0으로 시작하면 OS가 고른 포트입니다.</summary>
    public int Port { get; private set; }

    public int ConnectionCount
    {
        get { lock (_gate) return _connections.Count; }
    }

    public void Start(int port)
    {
        lock (_gate)
        {
            if (_listener is not null) throw new InvalidOperationException("이미 시작되었습니다.");
            // 포트 사용 중 등으로 실패하면 상태를 남기지 않아 다른 포트로 다시 시작할 수 있게 한다.
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            _listener = listener;
            _lifetime = new CancellationTokenSource();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        _logSink.Write($"[Secure] 보호 채널 시작: 포트={Port}");
        _ = AcceptLoopAsync(_listener, _lifetime.Token);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _logSink.Write($"[Secure] 수락 오류: {ex.SocketErrorCode}");
                continue;
            }

            if (Interlocked.Increment(ref _pendingHandshakes) > MaxPendingHandshakes || ConnectionCount >= MaxConnections)
            {
                Interlocked.Decrement(ref _pendingHandshakes);
                client.Dispose();
                _logSink.Write("[Secure] 연결 상한 초과로 거부");
                continue;
            }
            _ = HandshakeAsync(client, cancellationToken);
        }
    }

    private async Task HandshakeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        var remote = remoteEndPoint?.ToString() ?? "unknown";
        SslStream? ssl = null;
        SecureCollaborationConnection? connection = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_handshakeTimeout);

            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token);

            await CollaborationHandshake.ReceiveAsync(ssl, ParticipantRole.Student, timeout.Token);
            await CollaborationHandshake.SendAsync(ssl, ParticipantRole.Professor, timeout.Token);

            connection = new SecureCollaborationConnection(ssl, client, _logSink, remoteEndPoint?.Address.ToString());
            lock (_gate)
            {
                if (_lifetime is null || _lifetime.IsCancellationRequested)
                    throw new OperationCanceledException();
                _connections.Add(connection);
            }
            _ = connection.Completion.ContinueWith(_ => { lock (_gate) _connections.Remove(connection); },
                TaskScheduler.Default);
            _logSink.Write($"[Secure] 연결 수락: connection={connection.Id}, 원격={remote}");
        }
        catch (Exception ex)
        {
            // 인증 실패 사유는 로컬 로그에만 남긴다. 상대에게는 연결 종료 외에 알려 주지 않는다.
            var reason = ex switch
            {
                OperationCanceledException => "제한 시간 초과 또는 종료",
                AuthenticationException => "TLS 협상 실패",
                CollaborationException collaboration => $"capability 거부({collaboration.Code})",
                _ => ex.GetType().Name
            };
            _logSink.Write($"[Secure] 연결 거부: 원격={remote}, 사유={reason}");
            if (connection is not null) await connection.DisposeAsync();
            else
            {
                try { ssl?.Dispose(); } catch { }
                client.Dispose();
            }
            return;
        }
        finally
        {
            Interlocked.Decrement(ref _pendingHandshakes);
        }

        var handler = ConnectionAccepted;
        if (handler is null)
        {
            await connection.DisposeAsync();
            return;
        }
        try
        {
            await handler(connection);
        }
        catch (Exception ex)
        {
            _logSink.Write($"[Secure] 연결 처리기 오류: connection={connection.Id}, {ex.GetType().Name}");
            await connection.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        SecureCollaborationConnection[] connections;
        lock (_gate)
        {
            _lifetime?.Cancel();
            _listener?.Stop();
            _listener = null;
            connections = _connections.ToArray();
            _connections.Clear();
        }
        foreach (var connection in connections) await connection.DisposeAsync();
        _logSink.Write("[Secure] 보호 채널 중지");
    }
}
