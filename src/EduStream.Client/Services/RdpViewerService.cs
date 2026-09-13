using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using RDPCOMAPILib;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace EduStream.Client.Services;

/// <summary>
/// WDS RDPViewer ActiveX를 감싸 IRdpViewerService 계약을 구현합니다.
/// COM/ActiveX는 STA 스레드가 필요하므로 전용 스레드에서 컨트롤을 생성/제어합니다.
/// </summary>
public sealed class RdpViewerService : IRdpViewerService
{
    private readonly ILogSink? _logSink;
    private Thread? _staThread;
    private AxHost? _axHost; // AxRDPViewer 컨트롤 (WindowsFormsHost에 부착)
    private dynamic? _viewer; // RDPViewerClass 인스턴스 (동적 바인딩: 이벤트 등록은 별도 처리)
    private RdpConnectionStatus? _status;
    private Guid _sessionId;
    private string _participantId = string.Empty;

    public event Action<RdpConnectionStatus>? StatusChanged;

    /// <summary>
    /// WindowsFormsHost에 ActiveX 표시 영역을 생성해 부착합니다.
    /// Core 계약에는 없는 Client 전용 메서드입니다.
    /// </summary>
    public void AttachTo(WindowsFormsHost host)
    {
        // 실제 컨트롤 생성/부착은 STA 스레드에서 이뤄져야 하므로,
        // ConnectAsync가 처음 호출될 때 스레드를 만들고 여기서 host 참조만 보관합니다.
        _pendingHost = host;
    }

    private WindowsFormsHost? _pendingHost;

    public Task ConnectAsync(RdpInvitationPacket invitation, string invitationPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        _sessionId = invitation.SessionId ?? throw new ArgumentException("초대 패킷에 SessionId가 없습니다.", nameof(invitation));
        _participantId = invitation.ParticipantId;

        var connectionId = invitation.ConnectionId;
        _status = RdpConnectionStatus.Create(_sessionId, _participantId).BeginConnect(connectionId);
        Publish(_status);

        EnsureStaThread();

        // STA 스레드에서 실제 COM 호출을 수행
        _staDispatcher!.Invoke(() =>
        {
            try
            {
                CreateViewerIfNeeded();
                _viewer!.Connect(invitation.ConnectionString, _participantId, invitationPassword);
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
        if (_staDispatcher is null || _viewer is null)
        {
            return Task.CompletedTask;
        }

        _staDispatcher.Invoke(() =>
        {
            try
            {
                _viewer!.Disconnect();
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

    // ---- STA 스레드 관리 (뼈대만, 다음 단계에서 채움) ----
    private System.Windows.Threading.Dispatcher? _staDispatcher;

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

    private void CreateViewerIfNeeded()
    {
        if (_viewer is not null) return;

        dynamic viewer = new RDPViewerClass();

        viewer.OnConnectionEstablished += (Action)(() =>
        {
            if (_status is null) return;
            _status = _status.Connected(_status.ConnectionId);
            Publish(_status);
            _logSink?.Write("[RDP] 연결 성공 (OnConnectionEstablished)");
        });

        // TODO(통합 대기): OnConnectionFailed / OnConnectionTerminated / OnError 이벤트는
        // 실제 RDP 연결 환경에서 정확한 델리게이트 시그니처를 확인한 뒤 등록합니다.

        _viewer = viewer;
    }
    private static RdpFailureReason MapFailure(int failureCode)
    {
        // TODO: 실제 RDPCOMAPILib 실패 코드 상수와 대조해 세분화.
        // 현재는 계약의 CanRetry 대상(NetworkInterrupted/HostUnavailable) 중
        // 일반적인 연결 실패를 NetworkInterrupted로 보수적으로 매핑합니다.
        return RdpFailureReason.NetworkInterrupted;

    }
}
  