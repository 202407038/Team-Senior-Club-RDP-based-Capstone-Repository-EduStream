using EduStream.Core.Logging;

namespace EduStream.Server.Services;

/// <summary>
/// 주기적으로 하트비트 패킷을 브로드캐스트하여 클라이언트 연결 상태를 확인합니다.
/// 전송 실패 시 TcpServerService가 해당 클라이언트를 자동 제거합니다.
/// </summary>
public sealed class HeartbeatService
{
    private readonly SessionManager _sessionManager;
    private readonly TcpServerService _tcpServer;
    private readonly ILogSink _logSink;
    private readonly TimeSpan _interval;

    private CancellationTokenSource? _cts;

    public HeartbeatService(SessionManager sessionManager, TcpServerService tcpServer, ILogSink logSink, TimeSpan? interval = null)
    {
        _sessionManager = sessionManager;
        _tcpServer = tcpServer;
        _logSink = logSink;
        _interval = interval ?? TimeSpan.FromSeconds(10);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
        _logSink.Write($"[Heartbeat] 시작: 간격={_interval.TotalSeconds}초");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _logSink.Write("[Heartbeat] 중지");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct);

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
}
