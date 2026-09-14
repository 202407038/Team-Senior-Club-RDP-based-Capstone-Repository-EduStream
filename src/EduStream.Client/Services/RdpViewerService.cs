using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using AxRDPCOMAPILib;

namespace EduStream.Client.Services;

/// <summary>
/// WDS RDPViewer ActiveX를 감싸 IRdpViewerService 계약을 구현합니다.
/// COM/ActiveX는 STA 스레드가 필요하므로 전용 스레드에서 컨트롤을 생성/제어합니다.
/// AxRDPViewer가 Connect/Disconnect를 직접 노출하므로 별도 RDPViewerClass는 만들지 않습니다.
/// </summary>
public sealed class RdpViewerService : IRdpViewerService
{
    private readonly ILogSink? _logSink;
    private Thread? _staThread;
    private System.Windows.Threading.Dispatcher? _staDispatcher;
    private WindowsFormsHost? _pendingHost;
    private AxRDPViewer? _axHost;
    private RdpConnectionStatus? _status;
    private Guid _sessionId;
    private string _participantId = string.Empty;

    public event Action<RdpConnectionStatus>? StatusChanged;

    public RdpViewerService(ILogSink? logSink = null)
    {
        _logSink = logSink;
    }

    /// <summary>
    /// WindowsFormsHost에 ActiveX 표시 영역을 생성해 부착합니다.
    /// Core 계약에는 없는 Client 전용 메서드입니다.
    /// </summary>
    public void AttachTo(WindowsFormsHost host)
    {
        _pendingHost = host;

        EnsureStaThread();

        AxRDPViewer? createdViewer = null;

        _staDispatcher!.Invoke(() =>
        {
            var axViewer = new AxRDPViewer();
            axViewer.CreateControl();

            axViewer.OnConnectionEstablished += (object? sender, EventArgs e) =>
            {
                if (_status is null) return;
                _status = _status.Connected(_status.ConnectionId);
                Publish(_status);
                _logSink?.Write("[RDP] 연결 성공 (OnConnectionEstablished)");
            };

            // TODO(통합 대기): OnConnectionFailed / OnConnectionTerminated / OnError 이벤트는
            // 실제 RDP 연결 환경에서 정확한 델리게이트 시그니처를 확인한 뒤 등록합니다.

            createdViewer = axViewer;
        });

        _axHost = createdViewer;

        // host.Child 대입은 WPF UI 스레드에서 실행 (WindowsFormsHost가 UI 스레드 소유이므로)
        host.Child = createdViewer;
    }

    public Task ConnectAsync(RdpInvitationPacket invitation, string invitationPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        _sessionId = invitation.SessionId ?? throw new ArgumentException("초대 패킷에 SessionId가 없습니다.", nameof(invitation));
        _participantId = invitation.ParticipantId;

        var connectionId = invitation.ConnectionId;
        _status = RdpConnectionStatus.Create(_sessionId, _participantId).BeginConnect(connectionId);
        Publish(_status);

        if (_axHost is null)
        {
            _status = _status.Fail(connectionId, RdpFailureReason.Unknown);
            Publish(_status);
            _logSink?.Write("[RDP] ConnectAsync 실패: AttachTo가 먼저 호출되지 않았습니다.");
            return Task.CompletedTask;
        }

        _staDispatcher!.Invoke(() =>
        {
            try
            {
                _axHost!.Connect(invitation.ConnectionString, _participantId, invitationPassword);
            }
            catch (Exception ex)
            {
                _status = _status!.Fail(connectionId, RdpFailureReason.Unknown);
                Publish(_status);
                _logSink?.Write($"[RDP] Connect 실패: {ex.Message}");
            }
        });

        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_staDispatcher is null || _axHost is null)
        {
            return Task.CompletedTask;
        }

        _staDispatcher.Invoke(() =>
        {
            try
            {
                _axHost!.Disconnect();
            }
            catch (Exception ex)
            {
                _logSink?.Write($"[RDP] Disconnect 실패: {ex.Message}");
            }
        });

        if (_status is not null)
        {
            _status = _status.Close();
            Publish(_status);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        ShutdownStaThread();
    }

    private void Publish(RdpConnectionStatus status) => StatusChanged?.Invoke(status);

    private void EnsureStaThread()
    {
        if (_staThread is not null) return;

        var ready = new ManualResetEventSlim(false);
        _staThread = new Thread(() =>
        {
            _staDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        });
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Start();
        ready.Wait();
    }

    private void ShutdownStaThread()
    {
        _staDispatcher?.InvokeShutdown();
        _staThread = null;
    }
}