using EduStream.Core.Logging;

namespace EduStream.Server.Services;

/// <summary>
/// 주기적으로 HeartbeatPacket을 브로드캐스트하여 클라이언트 연결 상태를 확인합니다.
/// 전송 실패 시 TcpServerService가 자동으로 해당 클라이언트를 정리합니다.
/// </summary>
public sealed class HeartbeatService : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ILogSink _logSink;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    public HeartbeatService(SessionManager sessionManager, ILogSink logSink, TimeSpan? interval = null)
    {
        _sessionManager = sessionManager;
        _logSink = logSink;
        _interval = interval ?? TimeSpan.FromSeconds(10);
    }

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;

    /// <summary>
    /// Heartbeat 루프를 시작합니다.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _logSink.Write($"Heartbeat 시작: 간격={_interval.TotalSeconds}초");
        _ = RunLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Heartbeat 루프를 중지합니다.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _logSink.Write("Heartbeat 중지");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct);

                if (_sessionManager.IsSessionOpen && _sessionManager.ParticipantCount > 0)
                {
                    var heartbeat = _sessionManager.CreateHeartbeat();
                    await _sessionManager.BroadcastPacketAsync(heartbeat);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logSink.Write($"Heartbeat 오류: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
