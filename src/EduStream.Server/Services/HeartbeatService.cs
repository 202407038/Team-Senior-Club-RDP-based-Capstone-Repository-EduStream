using EduStream.Core.Logging;
using EduStream.Core.Protocols;

namespace EduStream.Server.Services;

/// <summary>
/// 주기적으로 하트비트 패킷을 브로드캐스트하여 클라이언트 연결 상태를 확인하고,
/// 일정 시간 동안 어떤 패킷도 보내지 않은 클라이언트는 강제로 끊습니다.
/// 송신 루프와 비활성 검사 루프를 동시에 돌립니다.
/// </summary>
public sealed class HeartbeatService
{
    private readonly SessionManager _sessionManager;
    private readonly TcpServerService _tcpServer;
    private readonly ILogSink _logSink;
    private readonly TimeSpan _sendInterval;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _staleCheckInterval;

    private CancellationTokenSource? _cts;

    public HeartbeatService(
        SessionManager sessionManager,
        TcpServerService tcpServer,
        ILogSink logSink,
        TimeSpan? sendInterval = null,
        TimeSpan? timeout = null,
        TimeSpan? staleCheckInterval = null)
    {
        _sessionManager = sessionManager;
        _tcpServer = tcpServer;
        _logSink = logSink;
        _sendInterval = sendInterval ?? HeartbeatPolicy.SendInterval;
        _timeout = timeout ?? HeartbeatPolicy.Timeout;
        _staleCheckInterval = staleCheckInterval ?? HeartbeatPolicy.StaleCheckInterval;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = SendLoopAsync(_cts.Token);
        _ = StaleCheckLoopAsync(_cts.Token);
        _logSink.Write(
            $"[Heartbeat] 시작: 송신={_sendInterval.TotalSeconds}s, 타임아웃={_timeout.TotalSeconds}s, 검사={_staleCheckInterval.TotalSeconds}s");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _logSink.Write("[Heartbeat] 중지");
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_sendInterval, ct);

                if (!_sessionManager.IsSessionOpen || _sessionManager.ParticipantCount == 0)
                    continue;

                var heartbeat = _sessionManager.CreateHeartbeat();
                await _tcpServer.BroadcastAsync(heartbeat);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logSink.Write($"[Heartbeat] 전송 오류: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task StaleCheckLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_staleCheckInterval, ct);

                if (!_sessionManager.IsSessionOpen)
                    continue;

                var stale = _sessionManager.GetStaleClientIds(_timeout);
                foreach (var clientId in stale)
                {
                    _logSink.Write($"[Heartbeat] 타임아웃 disconnect: clientId={clientId}, timeout={_timeout.TotalSeconds}s");
                    await _tcpServer.DisconnectClientAsync(clientId);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logSink.Write($"[Heartbeat] 비활성 검사 오류: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
