using System.ComponentModel;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using AxRDPCOMAPILib;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Utils;
using EduStream.ShareViewer;

namespace EduStream.Client.Services;

/// <summary>표시 컨트롤과 COM 연결을 같은 UI STA에서 관리하며 연결마다 새 컨트롤을 생성합니다.</summary>
public sealed class RdpViewerService : IRdpViewerService
{
    private readonly ILogSink? _log;
    private WindowsFormsHost? _host;
    private AxRDPViewer? _viewer;
    private ViewerZoomSurface? _surface;
    private ProfessorViewerConnection? _connection;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private RdpConnectionStatus? _status;
    private DispatcherTimer? _timeout;
    private int _generation;
    private bool _disposed;
    // 공유 화면의 가로/세로 비율. 0이면 아직 모른다. 알면 표시 영역을 이 비율로 맞춰 늘어나 보이지 않게 한다.
    private double _aspect;
    public event Action<RdpConnectionStatus>? StatusChanged;

    public RdpViewerService(ILogSink? logSink = null) => _log = logSink;

    public void AttachTo(WindowsFormsHost host)
    {
        host.Dispatcher.VerifyAccess();
        if (_host is not null && !ReferenceEquals(_host, host))
            throw new InvalidOperationException("이미 다른 표시 영역에 연결됐습니다.");
        _host = host;
        if (host.Parent is System.Windows.FrameworkElement parent)
            parent.SizeChanged += (_, _) => FitHost();
    }

    /// <summary>
    /// 공유 데스크톱 크기를 받아 표시 비율을 갱신합니다. 연결 직후 임시 크기가 먼저 올 수 있어 마지막 값을 따릅니다.
    /// 뷰어는 공유 영역 변경(OnSharedRectChanged)은 알려 주지 않고, 데스크톱 설정 변경(OnSharedDesktopSettingsChanged)으로 크기를 알려 줍니다.
    /// </summary>
    private void ApplySharedSize(int generation, int width, int height, string source)
    {
        if (generation != _generation || width <= 0 || height <= 0) return;
        _aspect = (double)width / height;
        _surface?.SetSourceSize(width, height);
        FitHost();
    }

    /// <summary>
    /// 표시 컨트롤을 공유 화면의 원래 비율로 바깥 영역 안에 맞춥니다(남는 쪽은 여백).
    /// SmartSizing은 영역을 가득 채우도록 늘리기만 하므로, 컨트롤 자체를 원본 비율로 만들어 납작하게 보이지 않게 합니다.
    /// </summary>
    private void FitHost()
    {
        if (_host is not { Parent: System.Windows.FrameworkElement parent } host) return;
        if (_aspect <= 0 || parent.ActualWidth <= 0 || parent.ActualHeight <= 0)
        {
            host.Width = double.NaN;
            host.Height = double.NaN;
            return;
        }
        var width = parent.ActualWidth;
        var height = width / _aspect;
        if (height > parent.ActualHeight)
        {
            height = parent.ActualHeight;
            width = height * _aspect;
        }
        host.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        host.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        host.Width = width;
        host.Height = height;
    }

    public async Task ConnectAsync(RdpInvitationPacket invitation, string invitationPassword, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RdpInvitationContract.Validate(invitation, invitation.SessionId ?? Guid.Empty,
            invitation.ParticipantId, invitation.ConnectionId, DateTimeOffset.UtcNow);
        if (string.IsNullOrEmpty(invitationPassword)) throw new ArgumentException("별도로 전달받은 초대 비밀번호를 입력해 주세요.");
        var host = _host ?? throw new InvalidOperationException("RDP 표시 영역이 준비되지 않았습니다.");
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        await host.Dispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ResetViewerAsync(cancellationToken);
            var generation = _generation;
            _status = RdpConnectionStatus.Create(invitation.SessionId!.Value, invitation.ParticipantId).BeginConnect(invitation.ConnectionId);
            Publish();
            try
            {
                var viewer = new AxRDPViewer();
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
                _surface = new ViewerZoomSurface();
                host.Child = _surface;
                _surface.Controls.Add(viewer);
                ((ISupportInitialize)viewer).EndInit();
                viewer.CreateControl();
                _surface.Attach(viewer);
                // 공유 화면 전체가 창 크기에 맞게 축소되어 스크롤 없이 보이게 한다(기본 화면 맞춤).
                viewer.SmartSizing = true;
                viewer.OnSharedRectChanged += (_, e) =>
                    ApplySharedSize(generation, e.right - e.left, e.bottom - e.top, "SharedRect");
                viewer.OnSharedDesktopSettingsChanged += (_, e) =>
                    ApplySharedSize(generation, e.width, e.height, "DesktopSettings");
                _timeout = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background,
                    (_, _) => Fail(generation, RdpFailureReason.HostUnavailable), host.Dispatcher);
                _connection = new ProfessorViewerConnection(viewer);
                _connection.Connect(invitation.ConnectionString, invitation.ParticipantId, invitationPassword);
            }
            catch (Exception ex)
            {
                _log?.Write($"[RDP] 연결 시작 실패: {ex.GetType().Name}");
                Fail(generation, RdpFailureReason.Unknown);
            }
        }, DispatcherPriority.Normal, cancellationToken).Task.Unwrap().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
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
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        await _host.Dispatcher.InvokeAsync(async () =>
        {
            await ResetViewerAsync(cancellationToken);
            if (_status is not null) { _status = _status.Close(); Publish(); }
        }, DispatcherPriority.Normal, cancellationToken).Task.Unwrap().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ResetViewerAsync(CancellationToken cancellationToken)
    {
        ++_generation; // 해제 중 발생하는 COM 이벤트와 이전 연결 알림을 먼저 무효화한다.
        _aspect = 0;
        FitHost();
        _timeout?.Stop();
        _timeout = null;
        var viewer = _viewer;
        if (viewer is null) return;
        // Disconnect 반환만으로 COM 컨트롤을 폐기하지 않는다. 실제 종료 이벤트를 기다린다.
        // 실패 시 참조를 보존하여 다음 종료에서 재시도한다.
        if (_connection is not null) await _connection.ReleaseAfterSharingStoppedAsync(cancellationToken);
        else viewer.Dispose();
        _connection = null; _viewer = null;
        if (_host is not null) _host.Child = null;
        _surface?.Dispose(); _surface = null;
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
