using System.ComponentModel;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using AxRDPCOMAPILib;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Utils;

namespace EduStream.Client.Services;

/// <summary>표시 컨트롤과 COM 연결을 같은 UI STA에서 관리하며 연결마다 새 컨트롤을 생성합니다.</summary>
public sealed class RdpViewerService : IRdpViewerService
{
    private readonly ILogSink? _log;
    private WindowsFormsHost? _host;
    private AxRDPViewer? _viewer;
    private RdpConnectionStatus? _status;
    private DispatcherTimer? _timeout;
    private int _generation;
    private bool _disposed;
    public event Action<RdpConnectionStatus>? StatusChanged;

    public RdpViewerService(ILogSink? logSink = null) => _log = logSink;

    public void AttachTo(WindowsFormsHost host)
    {
        host.Dispatcher.VerifyAccess();
        if (_host is not null && !ReferenceEquals(_host, host))
            throw new InvalidOperationException("이미 다른 표시 영역에 연결됐습니다.");
        _host = host;
    }

    public async Task ConnectAsync(RdpInvitationPacket invitation, string invitationPassword, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RdpInvitationContract.Validate(invitation, invitation.SessionId ?? Guid.Empty,
            invitation.ParticipantId, invitation.ConnectionId, DateTimeOffset.UtcNow);
        if (string.IsNullOrEmpty(invitationPassword)) throw new ArgumentException("별도로 전달받은 초대 비밀번호를 입력해 주세요.");
        var host = _host ?? throw new InvalidOperationException("RDP 표시 영역이 준비되지 않았습니다.");
        await host.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResetViewer();
            var generation = _generation;
            _status = RdpConnectionStatus.Create(invitation.SessionId!.Value, invitation.ParticipantId).BeginConnect(invitation.ConnectionId);
            Publish();
            try
            {
                var viewer = new AxRDPViewer { Dock = DockStyle.Fill };
                _viewer = viewer;
                viewer.OnConnectionEstablished += (_, _) =>
                {
                    if (generation != _generation || _status?.State != RdpConnectionState.Connecting) return;
                    _timeout?.Stop();
                    _status = _status.Connected(invitation.ConnectionId);
                    Publish();
                };
                viewer.OnConnectionFailed += (_, _) => Fail(generation, RdpFailureReason.HostUnavailable);
                viewer.OnConnectionTerminated += (_, _) => Fail(generation, RdpFailureReason.NetworkInterrupted);
                viewer.OnError += (_, _) => Fail(generation, RdpFailureReason.Unknown);
                ((ISupportInitialize)viewer).BeginInit();
                host.Child = viewer;
                ((ISupportInitialize)viewer).EndInit();
                viewer.CreateControl();
                _timeout = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background,
                    (_, _) => Fail(generation, RdpFailureReason.HostUnavailable), host.Dispatcher);
                viewer.Connect(invitation.ConnectionString, invitation.ParticipantId, invitationPassword);
            }
            catch (Exception ex)
            {
                _log?.Write($"[RDP] 연결 시작 실패: {ex.GetType().Name}");
                Fail(generation, RdpFailureReason.Unknown);
            }
        }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
    }

    private void Fail(int generation, RdpFailureReason reason)
    {
        if (generation != _generation || _status is null ||
            _status.State is not (RdpConnectionState.Connecting or RdpConnectionState.Connected or RdpConnectionState.Reconnecting)) return;
        _timeout?.Stop();
        _status = _status.Fail(_status.ConnectionId, reason);
        Publish();
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_host is null) return;
        await _host.Dispatcher.InvokeAsync(() =>
        {
            ResetViewer();
            if (_status is not null) { _status = _status.Close(); Publish(); }
        }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
    }

    private void ResetViewer()
    {
        ++_generation; // 해제 중 발생하는 COM 이벤트와 이전 연결 알림을 먼저 무효화한다.
        _timeout?.Stop();
        _timeout = null;
        var viewer = _viewer;
        _viewer = null;
        if (viewer is null) return;
        try { viewer.Disconnect(); }
        catch (Exception ex) { _log?.Write($"[RDP] 연결 해제: {ex.GetType().Name}"); }
        finally
        {
            if (_host?.Child == viewer) _host.Child = null;
            viewer.Dispose();
        }
    }
    private void Publish()
    {
        if (_status is not null) StatusChanged?.Invoke(_status);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await DisconnectAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
