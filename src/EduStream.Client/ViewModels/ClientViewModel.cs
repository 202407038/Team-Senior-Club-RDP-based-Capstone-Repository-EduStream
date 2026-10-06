using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Core.Common;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Core.Utils;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;


namespace EduStream.Client.ViewModels;

/// <summary>
/// 수강생 클라이언트의 세션/채팅/파일/화면 수신 상태를 화면에 표시하기 위한 ViewModel입니다.
/// TcpClientService를 통해 실제 서버와 통신합니다.
/// </summary>
public sealed class ClientViewModel : ObservableObject
{
    private readonly InMemoryLogSink _logSink = new();
    private readonly object _activityLogSync = new();
    private readonly SessionClient _sessionClient;
    private readonly ScreenRenderer _screenRenderer;
    private readonly FileReceiver _fileReceiver;
    private readonly TcpClientService _tcpClient;
    private readonly IPacketSerializer _serializer = new PacketSerializer();
    private readonly IRdpViewerService _rdpViewerService;
    private SecureSessionChannel? _secureChannel;
    private StudentStatusClient? _statusClient;
    // 자동 재연결(U03): 마지막 참가 정보와 교수자가 준 일회용 토큰. 비밀번호는 보관하지 않는다.
    private sealed record JoinTarget(string Host, int Port, string DisplayName);
    private JoinTarget? _lastJoin;
    private string? _reconnectToken;
    private TimeSpan _reconnectWindow = ReconnectRules.DefaultWindow;
    private CancellationTokenSource? _reconnectCts;
    private volatile bool _userLeaving;
    private volatile bool _sessionEnded;
    private SessionFileRequestClient? _fileClient;
    private ReverseCollaborationClient? _reverseClient;
    private StudentReverseShareService? _reverseShare;
    // 참가 요청을 보낸 뒤 서버의 참가 승인(SessionJoined)을 기다리는 시도. 승인·거부·끊김·시간 초과 중 먼저 온 결과로 끝난다.
    private enum JoinAckResult { Joined, Rejected, Disconnected, TimedOut }
    private TaskCompletionSource<JoinAckResult>? _pendingJoinAck;
    private StudentStatus _studentStatus = StudentStatus.Initial;
    private bool _permissionNoticeShown;
    private RdpInvitationPacket? _activeRdpInvitation;
    // 초대(TCP)와 비밀번호(보호 채널)는 도착 순서가 정해져 있지 않아, 둘이 같은 초대로 짝지어질 때까지 보관한다.
    private readonly object _rdpAutoConnectLock = new();
    private RdpInvitationSecretNotice? _pendingRdpSecret;
    // 비밀번호를 비우는 것과 별개로, 현재 요청에서 이미 시작한 초대는 실패한 경우에도 자동 재실행하지 않는다.
    private readonly HashSet<(Guid Session, Guid Connection, Guid Invitation)> _startedRdpAutoConnections = new();
    private Guid _rdpSessionId;
    private string _rdpParticipant = string.Empty;
    private bool _isRdpActive;
    private readonly System.Windows.Threading.DispatcherTimer _freshnessTimer;
    private bool _disposing;
    private int _frameGeneration;
    public bool IsRdpActive { get => _isRdpActive; private set => SetProperty(ref _isRdpActive, value); }
    private Guid _currentRdpConnectionId;
    private string _rdpStatusText = "RDP 대기 중";

    public string RdpStatusText
    {
        get => _rdpStatusText;
        private set => SetProperty(ref _rdpStatusText, value);
    }
    private string _hostAddress = "127.0.0.1";
    private ImageSource? _displaySource;
    private bool _hasRemoteFrame;
    private string _placeholderTitle = "연결 대기 중";
    private string _placeholderSubtitle = "세션에 참여하면 화면이 표시됩니다.";
    private int _lastProgressPercent = -1;
    private int _port = 5000;
    private string _displayName = "StudentDemo";
    private string _connectionState = "연결 전";
    private string _sessionSummary = "아직 참가한 세션이 없습니다.";
    private string _lastServerMessage = "서버 응답을 기다리는 중입니다.";
    private string _lastSuccessMessage = "아직 성공한 작업이 없습니다.";
    private string _lastErrorMessage = "오류 없음";
    private string _chatStatus = "채팅 대기 중";
    private string _renderStatus = "화면 프레임을 아직 받지 않았습니다.";
    private string _screenDetail = "프레임 메타데이터를 아직 받지 않았습니다.";
    private string _downloadStatus = "다운로드 대기 중";
    private string _fileTransferDetail = "파일 수신 이벤트가 없습니다.";
    private DateTimeOffset? _lastFrameReceivedAt;
    private string _frameFreshness = "프레임 없음";
    private string _chatInput = string.Empty;
    private bool _isConnected;
    private int? _lastFrameIndex;

    // 💡 UI 연동을 위해 새로 추가한 백엔드 필드들
    private bool _isConnecting;
    private bool _isStatusError;
    private string _statusMessage = string.Empty;

    public ClientViewModel(IRdpViewerService? rdpViewerService = null)
    {
        _rdpViewerService = rdpViewerService ?? new RdpViewerService();
        _sessionClient = new SessionClient(_logSink);
        _screenRenderer = new ScreenRenderer();
        _fileReceiver = new FileReceiver();
        _tcpClient = new TcpClientService(_logSink, _serializer);

        _tcpClient.PacketReceived += OnPacketReceivedAsync;
        _tcpClient.Disconnected += OnDisconnectedAsync;
        _rdpViewerService.StatusChanged += OnRdpStatusChanged;
        JoinSessionCommand = new RelayCommand(() => _ = JoinSessionAsync(), () => !IsConnected && !IsConnecting);
        // 참가 승인 대기·자동 재연결 중에도 사용자가 중단할 수 있어야 한다.
        DisconnectCommand = new RelayCommand(() => _ = DisconnectAsync(), () => IsConnected || IsConnecting);
        SendChatCommand = new RelayCommand(() => _ = SendChatAsync(), () => IsConnected && !string.IsNullOrWhiteSpace(ChatInput));
        SimulateScreenRenderCommand = new RelayCommand(() => _ = SimulateScreenRenderAsync());
        SimulateFileReceiveCommand = new RelayCommand(() => _ = SimulateFileReceiveAsync());
        ReconnectRdpCommand = new RelayCommand(() => _ = SendRdpInvitationRequestAsync());
        ToggleControlPermissionCommand = new RelayCommand(
            () => _ = ChangePermissionsAsync(_studentStatus.AllowViewing, !_studentStatus.AllowControl),
            () => _statusClient is not null && _studentStatus.AllowViewing);
        ToggleViewingPermissionCommand = new RelayCommand(
            () => _ = ChangePermissionsAsync(!_studentStatus.AllowViewing, _studentStatus.AllowControl),
            () => _statusClient is not null);
        StopControlNowCommand = new RelayCommand(
            () => _ = ChangePermissionsAsync(_studentStatus.AllowViewing, false),
            () => _statusClient is not null && _studentStatus.AllowControl);
        _freshnessTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _freshnessTimer.Tick += (_, _) => UpdateFrameFreshness();
        _freshnessTimer.Start();

        SyncLogs();
    }
    private void UpdateFrameFreshness()
    {
        if (_lastFrameReceivedAt is null)
        {
            FrameFreshness = "프레임 없음";
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _lastFrameReceivedAt.Value;
        FrameFreshness = elapsed.TotalSeconds < 1.5
            ? "방금 갱신됨"
            : $"{elapsed.TotalSeconds:F1}초 전 갱신";
    }

    public void AttachRdpHost(System.Windows.Forms.Integration.WindowsFormsHost host)
    {
        if (_rdpViewerService is RdpViewerService viewer) viewer.AttachTo(host);
    }
    public string FrameFreshness
    {
        get => _frameFreshness;
        private set => SetProperty(ref _frameFreshness, value);
    }
    public string HostAddress
    {
        get => _hostAddress;
        set => SetProperty(ref _hostAddress, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    private bool _roomPasswordRequired;

    /// <summary>방 비밀번호 입력칸을 보여 줄지 여부입니다. 비밀번호가 필요한 방으로 확인되면 켜집니다.</summary>
    public bool RoomPasswordRequired
    {
        get => _roomPasswordRequired;
        set => SetProperty(ref _roomPasswordRequired, value);
    }

    /// <summary>참가 요청 뒤 서버의 참가 승인을 기다리는 최대 시간입니다. 넘기면 참가 실패로 정리합니다.</summary>
    public TimeSpan JoinAckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 방 비밀번호 입력칸을 읽고 비우는 함수입니다. 비밀번호를 ViewModel 속성에 보관하지 않기 위해 View가 제공합니다.
    /// </summary>
    public Func<string>? RoomPasswordProvider { get; set; }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string ConnectionState
    {
        get => _connectionState;
        private set => SetProperty(ref _connectionState, value);
    }

    public string SessionSummary
    {
        get => _sessionSummary;
        private set => SetProperty(ref _sessionSummary, value);
    }

    public string LastServerMessage
    {
        get => _lastServerMessage;
        private set => SetProperty(ref _lastServerMessage, value);
    }

    public string LastSuccessMessage
    {
        get => _lastSuccessMessage;
        private set => SetProperty(ref _lastSuccessMessage, value);
    }

    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        private set => SetProperty(ref _lastErrorMessage, value);
    }

    public string ChatStatus
    {
        get => _chatStatus;
        private set => SetProperty(ref _chatStatus, value);
    }

    public string RenderStatus
    {
        get => _renderStatus;
        private set => SetProperty(ref _renderStatus, value);
    }

    public string ScreenDetail
    {
        get => _screenDetail;
        private set => SetProperty(ref _screenDetail, value);
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
    }

    public string FileTransferDetail
    {
        get => _fileTransferDetail;
        private set => SetProperty(ref _fileTransferDetail, value);
    }

    public string ChatInput
    {
        get => _chatInput;
        set
        {
            if (SetProperty(ref _chatInput, value))
            {
                SendChatCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                UpdatePlaceholder();
                OnPropertyChanged(nameof(IsLectureViewActive));
                JoinSessionCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                SendChatCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _isReconnecting;

    /// <summary>참가 중이거나 자동 재연결 중이면 강의 화면을, 아니면 참가 화면을 보여 주기 위한 값입니다.</summary>
    public bool IsLectureViewActive => IsConnected || _isReconnecting;

    private void SetReconnecting(bool value)
    {
        if (_isReconnecting == value) return;
        _isReconnecting = value;
        UpdatePlaceholder();
        OnPropertyChanged(nameof(IsLectureViewActive));
    }

    private void UpdatePlaceholder()
    {
        PlaceholderTitle = IsConnected ? "공유 화면 대기 중" :
            _isReconnecting ? "세션 재연결 중" : "연결 대기 중";
        PlaceholderSubtitle = IsConnected ? "세션에 참가했습니다. 공유 화면 연결 상태는 오른쪽 안내를 확인해 주세요." :
            _isReconnecting ? "교수자 세션에 다시 연결하고 있습니다." : "세션에 참여하면 화면이 표시됩니다.";
    }

    public ImageSource? DisplaySource
    {
        get => _displaySource;
        private set => SetProperty(ref _displaySource, value);
    }

    public bool HasRemoteFrame
    {
        get => _hasRemoteFrame;
        private set => SetProperty(ref _hasRemoteFrame, value);
    }

    public string PlaceholderTitle
    {
        get => _placeholderTitle;
        private set => SetProperty(ref _placeholderTitle, value);
    }

    public string PlaceholderSubtitle
    {
        get => _placeholderSubtitle;
        private set => SetProperty(ref _placeholderSubtitle, value);
    }
    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                JoinSessionCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // 💡 UI 시스템 알림창의 빨간색 에러 트리거와 연결되는 프로퍼티
    public bool IsStatusError
    {
        get => _isStatusError;
        private set => SetProperty(ref _isStatusError, value);
    }

    // 💡 UI 시스템 알림창의 텍스트와 연결되는 프로퍼티
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ObservableCollection<string> ActivityLogs { get; } = [];

    public ObservableCollection<ChatLine> ChatMessages { get; } = [];

    public ObservableCollection<string> DownloadedFiles { get; } = [];

    /// <summary>교수자가 등록한 강의 파일 목록입니다. 항목의 다운로드 버튼으로 골라 받습니다(U08).</summary>
    public ObservableCollection<SessionFileItem> SessionFiles { get; } = [];

    public RelayCommand JoinSessionCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand SendChatCommand { get; }

    public RelayCommand SimulateScreenRenderCommand { get; }

    public RelayCommand SimulateFileReceiveCommand { get; }

    public RelayCommand ReconnectRdpCommand { get; }

    /// <summary>교수자 원격 제어 허용 켜기/끄기(U07). 화면 표시는 교수자가 반영한 상태로만 바뀝니다.</summary>
    public RelayCommand ToggleControlPermissionCommand { get; }

    /// <summary>교수자의 내 화면 보기 허용 켜기/끄기. 보기를 끄면 제어 허용도 함께 꺼집니다.</summary>
    public RelayCommand ToggleViewingPermissionCommand { get; }

    /// <summary>진행 중인 원격 제어를 즉시 끝내고 제어 허용을 끕니다.</summary>
    public RelayCommand StopControlNowCommand { get; }

    public string PermissionSummary =>
        $"내 화면 보기 허용: {(_studentStatus.AllowViewing ? "ON" : "OFF")} · 원격 제어 허용: {(_studentStatus.AllowControl ? "ON" : "OFF")}";

    public string ControlStatusText => _studentStatus.ControlPhase switch
    {
        ControlPhase.Active => "교수자가 내 PC를 제어하고 있습니다.",
        ControlPhase.Requested => "교수자가 원격 제어를 시작하는 중입니다.",
        _ => "원격 제어 중이 아닙니다."
    };

    public bool IsUnderControl => _studentStatus.UnderControl;

    /// <summary>
    /// 현재 보호 채널의 역방향 초대 송신·판서 수신 처리기입니다. 참가 전·퇴장 후에는 null입니다.
    /// 3번 학생 호스트는 Target으로 초대를 만들어 SendInvitationAsync로 보내고, 판서 렌더러는 AnnotationRenderer에 연결합니다.
    /// </summary>
    public ReverseCollaborationClient? ReverseCollaboration => _reverseClient;

    /// <summary>교수자 원격 제어 허용 여부입니다. 강의 화면의 허용 표시(ON/OFF)에 씁니다.</summary>
    public bool AllowControl => _studentStatus.AllowControl;

    /// <summary>교수자가 내 화면을 볼 수 있는지 여부입니다.</summary>
    public bool AllowViewing => _studentStatus.AllowViewing;

    public string ControlToggleLabel => _studentStatus.AllowControl ? "원격 제어 허용 끄기" : "원격 제어 허용 켜기";

    public string ViewingToggleLabel => _studentStatus.AllowViewing ? "내 화면 보기 허용 끄기" : "내 화면 보기 허용 켜기";
    private async Task JoinSessionAsync()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ApplyJoinError(_sessionClient.CreateJoinError(HostAddress, Port, "표시 이름을 입력해야 합니다."));
            return;
        }

        if (string.IsNullOrWhiteSpace(HostAddress) || Port <= 0)
        {
            ApplyJoinError(_sessionClient.CreateJoinError(HostAddress, Port, "접속 주소와 포트를 확인해 주세요."));
            return;
        }

        try
        {
            // 💡 연결 시도 시 에러 상태 초기화 및 로딩 가동
            IsConnecting = true;
            IsStatusError = false;
            StatusMessage = "서버에 연결을 시도하는 중입니다...";

            ConnectionState = "연결 중...";
            _logSink.Write($"서버 연결 시도: {HostAddress}:{Port}");
            SyncLogs();

            // 새 참가는 이전 세션의 재연결 상태를 이어받지 않는다.
            CancelReconnect();
            _reconnectToken = null;
            _userLeaving = false;
            _sessionEnded = false;
            _lastJoin = new JoinTarget(HostAddress, Port, DisplayName);

            // 입력한 교수자 IP로 TLS 보호 채널을 연 뒤 방 비밀번호를 보내고 참가 티켓을 받는다.
            StatusMessage = "교수자 PC에 연결하는 중입니다...";
            var roomPassword = RoomPasswordProvider?.Invoke() ?? string.Empty;
            SecureSessionChannel secure;
            try
            {
                secure = await SecureRoomJoinClient.AuthenticateAsync(
                    HostAddress, Port, DisplayName, roomPassword.AsMemory(), _logSink);
            }
            catch (SecureJoinException ex)
            {
                var message = DescribeSecureJoinFailure(ex.Failure);
                if (ex.Failure is SecureJoinFailure.PasswordRejected or SecureJoinFailure.LockedOut)
                {
                    // 비밀번호가 걸린 방인데 칸을 열지 않았다면 입력칸을 보여 주고 안내한다.
                    if (ex.Failure == SecureJoinFailure.PasswordRejected && roomPassword.Length == 0 && !RoomPasswordRequired)
                        message = "이 방은 방 비밀번호가 필요합니다. 아래 칸에 교수자가 알려 준 비밀번호를 입력해 주세요.";
                    RoomPasswordRequired = true;
                }
                ApplyJoinError(_sessionClient.CreateJoinError(HostAddress, Port, message));
                return;
            }
            if (_userLeaving || _disposing)
            {
                await secure.DisposeAsync();
                return;
            }
            await ReplaceSecureChannelAsync(secure);
            // 교수자는 TCP 참가 직후 보호 채널로 상태를 보내므로, 참가 요청 전에 수신 처리기를 붙인다.
            AttachStudentStatus(secure);

            StatusMessage = "서버의 세션 참여 승인을 대기하고 있습니다.";
            // TCP 연결 (만약 서버가 꺼져있으면 여기서 catch 블록으로 튕깁니다)
            var result = await SendJoinAndAwaitAckAsync(HostAddress, Port, DisplayName, secure.JoinTicket, CancellationToken.None);
            SyncLogs();
            if (result == JoinAckResult.Joined || _userLeaving || _disposing) return;

            // 승인 전에 끝났다. 거부 사유는 서버 오류 처리에서 이미 안내했으므로 연결만 정리한다.
            await AbandonJoinAsync();
            if (result != JoinAckResult.Rejected)
            {
                ApplyJoinError(_sessionClient.CreateJoinError(HostAddress, Port, result == JoinAckResult.TimedOut
                    ? "서버가 참가 요청에 응답하지 않습니다. 잠시 뒤 다시 참가해 주세요."
                    : "참가 승인 전에 서버와의 연결이 끊어졌습니다. 다시 참가해 주세요."));
            }
        }
        catch (Exception)
        {
            await AbandonJoinAsync();
            if (_userLeaving || _disposing) return;
            // 💡 서버가 닫혀있을 때 명확하게 에러 메시지 주입
            ApplyJoinError(_sessionClient.CreateJoinError(HostAddress, Port, "서버를 찾을 수 없습니다. 호스트 주소와 포트 혹은 서버 구동 상태를 확인해 주세요."));
        }
    }

    private async Task SimulateScreenRenderAsync()
    {

        // 즉석에서 400x300 단색 PNG 생성
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(60, 40, 120)), null, new Rect(0, 0, 400, 300));
            var text = new FormattedText(
                "테스트 프레임",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                28,
                Brushes.White,
                1.0);
            dc.DrawText(text, new Point(20, 20));
        }

        var bitmap = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        var pngBytes = ms.ToArray();
        var fakePacket = new ScreenPacket
        {
            FrameIndex = new Random().Next(1, 9999),
            Width = 400,
            Height = 300,
            Encoding = ScreenEncodings.Png,
            Content = pngBytes,
            CapturedAt = DateTimeOffset.UtcNow
        };

        await HandleScreenAsync(fakePacket);
    }

    private async Task SimulateFileReceiveAsync()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("테스트 파일 내용입니다.");
        var checksum = ChecksumUtility.ComputeSha256(content);

        var fakePacket = new FilePacket
        {
            FileName = $"테스트파일_{DateTime.Now:HHmmss}.txt",
            FileSize = content.Length,
            Content = content,
            Checksum = checksum,
            TransferId = Guid.NewGuid(),
            ChunkIndex = 0,
            TotalChunks = 1,
            DataLength = content.Length
        };

        await HandleFileAsync(fakePacket);
    }

    private async Task DisconnectAsync()
    {
        // 사용자가 직접 나간 경우는 자동 재연결하지 않는다.
        _userLeaving = true;
        CancelReconnect();
        _reconnectToken = null;
        Interlocked.Increment(ref _frameGeneration);
        await ResetRdpAsync();
        try
        {
            // Leave 패킷 전송
            var leavePacket = _sessionClient.CreateLeaveRequest(DisplayName, "사용자 요청으로 연결 종료");
            await _tcpClient.SendAsync(leavePacket);
        }
        catch { }

        await _tcpClient.DisconnectAsync();
        await ReplaceSecureChannelAsync(null);
        await _sessionClient.DisconnectAsync("사용자 요청으로 연결 종료");

        RunOnUiThread(() =>
        {
            IsConnected = false;
            IsConnecting = false;
            IsStatusError = false;
            StatusMessage = "세션 연결을 안전하게 종료했습니다.";

            ConnectionState = "연결 종료";
            SessionSummary = "아직 참가한 세션이 없습니다.";
            LastServerMessage = "세션 종료 요청을 전송했습니다.";
            LastSuccessMessage = "세션 종료 요청을 정상적으로 보냈습니다.";
            LastErrorMessage = "오류 없음";
            ChatStatus = "채팅 대기 중";
            RenderStatus = "화면 프레임을 아직 받지 않았습니다.";
            HasRemoteFrame = false;
            DisplaySource = null;
            ScreenDetail = "프레임 메타데이터를 아직 받지 않았습니다.";
            DownloadStatus = "다운로드 대기 중";
            FileTransferDetail = "파일 수신 이벤트가 없습니다.";

            ChatMessages.Add(ChatLine.System($"{DisplayName} 님이 세션에서 나갔습니다."));
            _logSink.Write("세션 연결을 종료했습니다.");
            SyncLogs();
        });
    }

    private async Task SendChatAsync()
    {
        var trimmedMessage = ChatInput.Trim();
        if (string.IsNullOrWhiteSpace(trimmedMessage))
        {
            return;
        }

        var chatPacket = PacketFactory.CreateChat(
            senderId: DisplayName,
            sender: DisplayName,
            message: trimmedMessage,
            sessionId: _sessionClient.CurrentSession?.SessionId);

        try
        {
            await _tcpClient.SendAsync(chatPacket);
            _logSink.Write($"채팅 전송: {trimmedMessage}");
            LastServerMessage = "채팅 메시지를 서버로 전송했습니다.";
            LastSuccessMessage = "채팅 전송 성공";
            ChatStatus = $"최근 전송: {trimmedMessage}";
            ChatInput = string.Empty;
            SyncLogs();
        }
        catch (Exception ex)
        {
            _logSink.Write($"채팅 전송 실패: {ex.Message}");
            LastErrorMessage = $"CHAT_SEND_FAILED: {ex.Message}";
            ChatStatus = "채팅 전송 실패";
            SyncLogs();
        }
    }

    /// <summary>
    /// 서버로부터 수신한 패킷을 타입별로 처리합니다.
    /// </summary>
    private async Task OnPacketReceivedAsync(PacketType packetType, byte[] payload)
    {
        switch (packetType)
        {
            case PacketType.Ack:
                var ackPacket = JsonSerializer.Deserialize<AckPacket>(payload);
                if (ackPacket is not null)
                {
                    await HandleAckAsync(ackPacket);
                }
                break;

            case PacketType.Error:
                var errorPacket = JsonSerializer.Deserialize<ErrorPacket>(payload);
                if (errorPacket is not null)
                {
                    await HandleErrorAsync(errorPacket);
                }
                break;

            case PacketType.Chat:
                var chatPacket = JsonSerializer.Deserialize<ChatPacket>(payload);
                if (chatPacket is not null)
                {
                    HandleChat(chatPacket);
                }
                break;

            case PacketType.Heartbeat:
                var heartbeatResponse = PacketFactory.CreateHeartbeat(
                    senderId: DisplayName,
                    sessionId: _sessionClient.CurrentSession?.SessionId);
                try
                {
                    await _tcpClient.SendAsync(heartbeatResponse);
                }
                catch { }
                break;

            case PacketType.Screen:
                var screenPacket = JsonSerializer.Deserialize<ScreenPacket>(payload);
                if (screenPacket is not null)
                {
                    await HandleScreenAsync(screenPacket);
                }
                break;

            case PacketType.File:
                var filePacket = JsonSerializer.Deserialize<FilePacket>(payload);
                if (filePacket is not null)
                {
                    await HandleFileAsync(filePacket);
                }
                break;

            default:
                _logSink.Write($"알 수 없는 패킷 타입 수신: {packetType}");
                break;
            case PacketType.RdpInvitation:
                var rdpInvitation = JsonSerializer.Deserialize<RdpInvitationPacket>(payload);
                if (rdpInvitation is not null)
                {
                    await HandleRdpInvitationAsync(rdpInvitation);
                }
                break;

            case PacketType.RdpInvitationRevoked:
                var rdpRevoked = JsonSerializer.Deserialize<RdpInvitationRevokedPacket>(payload);
                if (rdpRevoked is not null)
                {
                    await HandleRdpInvitationRevokedAsync(rdpRevoked);
                }
                break;
        }
    }
    private async Task SendRdpInvitationRequestAsync()
    {
        try
        {
            await ResetRdpAsync();
            if (_disposing || !IsConnected || _sessionClient.CurrentSession is null)
                throw new InvalidOperationException("강의 세션에 먼저 참여해 주세요.");
            lock (_rdpAutoConnectLock)
            {
                _currentRdpConnectionId = Guid.NewGuid();
                _rdpSessionId = _sessionClient.CurrentSession.SessionId;
                _rdpParticipant = DisplayName;
            }
            var requestPacket = PacketFactory.CreateRdpInvitationRequest(
                _rdpParticipant, _rdpSessionId, _rdpParticipant, _currentRdpConnectionId);
            await _tcpClient.SendAsync(requestPacket);
            _logSink.Write("[RDP] 초대 요청 전송");
        }
        catch (Exception ex)
        {
            _logSink.Write($"[RDP] 초대 요청 실패: {ex.Message}");
            RunOnUiThread(() => RdpStatusText = $"RDP 초대 요청 실패: {ex.Message}");
        }
    }

    private async Task HandleRdpInvitationAsync(RdpInvitationPacket invitation)
    {
        try
        {
            lock (_rdpAutoConnectLock)
            {
                RdpInvitationContract.Validate(invitation, _rdpSessionId, _rdpParticipant,
                    _currentRdpConnectionId, DateTimeOffset.UtcNow);
                if (_disposing || !IsConnected || _startedRdpAutoConnections.Contains(
                    (_rdpSessionId, invitation.ConnectionId, invitation.InvitationId))) return;
                _activeRdpInvitation = invitation;
            }
            // 보호 채널이 있으면 비밀번호가 그쪽으로 오므로 입력을 요구하지 않는다.
            RunOnUiThread(() => RdpStatusText = _secureChannel is not null
                ? "초대 수신: 교수자 화면 연결 정보를 확인하는 중입니다."
                : "초대 수신: 설정 탭에서 별도로 전달받은 비밀번호를 입력해 주세요.");
            TryStartRdpAutoConnect();
        }
        catch (ArgumentException) { _logSink.Write("[RDP] 유효하지 않거나 이전 연결의 초대 무시"); }
        await Task.CompletedTask;
    }

    /// <summary>보호 채널로 받은 초대 비밀번호입니다. 지금 기다리는 초대 요청(연결 ID)에 대한 것만 보관합니다.</summary>
    private void OnRdpInvitationSecret(RdpInvitationSecretNotice secret)
    {
        lock (_rdpAutoConnectLock)
        {
            if (_disposing || !IsConnected || secret.SessionId != _rdpSessionId ||
                secret.ConnectionId != _currentRdpConnectionId)
            {
                _logSink.Write("[RDP] 현재 초대 요청과 맞지 않는 초대 비밀번호 무시");
                return;
            }
            if (_startedRdpAutoConnections.Contains((secret.SessionId, secret.ConnectionId, secret.InvitationId))) return;
            _pendingRdpSecret = secret;
        }
        TryStartRdpAutoConnect();
    }

    /// <summary>
    /// 초대와 비밀번호가 같은 초대(InvitationId·ConnectionId)로 짝지어지면 한 번만 자동 연결합니다.
    /// 참가 직후·공유 재시작·재참가 모두 초대 재요청을 거치므로 이 경로로 화면이 자동 복귀합니다.
    /// </summary>
    private void TryStartRdpAutoConnect()
    {
        RdpInvitationSecretNotice secret;
        RdpInvitationPacket matchedInvitation;
        bool expired;
        lock (_rdpAutoConnectLock)
        {
            if (_disposing || !IsConnected ||
                _activeRdpInvitation is not { } invitation || _pendingRdpSecret is not { } pending ||
                pending.SessionId != _rdpSessionId || invitation.SessionId != pending.SessionId ||
                pending.ConnectionId != _currentRdpConnectionId ||
                pending.InvitationId != invitation.InvitationId || pending.ConnectionId != invitation.ConnectionId)
                return;
            secret = pending;
            matchedInvitation = invitation;
            _pendingRdpSecret = null;
            expired = secret.ExpiresAt <= DateTimeOffset.UtcNow || invitation.ExpiresAt <= DateTimeOffset.UtcNow;
            // 접속 완료를 기다리기 전에 예약해야 동시에 도착한 알림도 한 번만 뷰어를 호출한다.
            if (!expired && !_startedRdpAutoConnections.Add(
                (secret.SessionId, secret.ConnectionId, secret.InvitationId))) return;
        }

        if (expired)
        {
            _logSink.Write("[RDP] 만료된 초대 비밀번호로 자동 연결하지 않음");
            RunOnUiThread(() => RdpStatusText = "RDP 초대가 만료되었습니다. 다시 요청해 주세요.");
            return;
        }
        _logSink.Write("[RDP] 보호 채널로 받은 초대로 자동 연결 시작");
        RunOnUiThread(() => RdpStatusText = "교수자 화면에 자동으로 연결하는 중입니다.");
        _ = ConnectRdpInvitationAsync(matchedInvitation, secret.Password);
    }

    public Task ConnectRdpWithPasswordAsync(string password)
    {
        RdpInvitationPacket? invitation;
        lock (_rdpAutoConnectLock) invitation = _activeRdpInvitation;
        return ConnectRdpInvitationAsync(invitation, password);
    }

    private async Task ConnectRdpInvitationAsync(RdpInvitationPacket? invitation, string password)
    {
        try
        {
            lock (_rdpAutoConnectLock)
            {
                if (invitation is null) throw new InvalidOperationException("유효한 초대를 먼저 받아 주세요.");
                // 짝지은 초대의 스냅샷을 검증한다. 이후 새 초대가 도착해도 이전 비밀번호와 섞지 않는다.
                RdpInvitationContract.Validate(invitation, _rdpSessionId, _rdpParticipant,
                    _currentRdpConnectionId, DateTimeOffset.UtcNow);
                if (!IsConnected || _disposing) throw new InvalidOperationException("강의 연결이 종료됐습니다.");
            }
            await _rdpViewerService.ConnectAsync(invitation, password);
        }
        catch (Exception ex) { RunOnUiThread(() => RdpStatusText = $"RDP 연결 실패: {ex.Message}"); }
    }

    private async Task HandleRdpInvitationRevokedAsync(RdpInvitationRevokedPacket revoked)
    {
        if (_activeRdpInvitation is null || !RdpInvitationContract.AppliesTo(revoked, _activeRdpInvitation)) return;
        await ResetRdpAsync();
        RunOnUiThread(() =>
        {
            RdpStatusText = $"RDP 연결 종료됨: {revoked.Reason}";
            _logSink.Write($"[RDP] 초대 폐기: {revoked.Reason}");
            SyncLogs();
        });
    }

    private void OnRdpStatusChanged(RdpConnectionStatus status)
    {
        RunOnUiThread(() =>
        {
            if (_disposing || status.ConnectionId != _currentRdpConnectionId || status.SessionId != _rdpSessionId ||
                status.ParticipantId != _rdpParticipant) return;
            IsRdpActive = status.State is RdpConnectionState.Connecting or RdpConnectionState.Reconnecting or RdpConnectionState.Connected;
            RdpStatusText = $"RDP: {status.State}" + (status.Failure != RdpFailureReason.None ? $" ({status.Failure})" : "");
            if (status.State == RdpConnectionState.Connected)
            {
                ResetStatusPriority();
                UpdateStatus("교수자 화면에 연결되었습니다.", StatusPriority.Success);
            }
            else if (IsConnected && status.State == RdpConnectionState.Closed)
            {
                ResetStatusPriority();
                UpdateStatus("교수자 화면 공유가 끝났습니다. 다시 시작되면 자동으로 연결됩니다.", StatusPriority.Info);
            }
            else if (IsConnected && status.State == RdpConnectionState.Failed)
            {
                ResetStatusPriority();
                UpdateStatus($"교수자 화면에 연결하지 못했습니다. ({status.Failure})", StatusPriority.Error, isError: true);
            }
            _logSink.Write($"[RDP] 상태 변경: {status.State}");
            SyncLogs();
        });
    }
    private async Task HandleAckAsync(AckPacket packet)
    {
        // 참가 승인은 지금 기다리는 참가 요청에 대한 것만 받는다. 시간 초과·취소로 포기한 뒤 늦게 온 승인은 무시한다.
        if (packet.AckCode == AckCodes.SessionJoined &&
            !(Volatile.Read(ref _pendingJoinAck)?.TrySetResult(JoinAckResult.Joined) ?? false))
        {
            _logSink.Write("대기 중이 아닌 참가 승인 무시");
            return;
        }

        RunOnUiThread(() =>
        {
            LastServerMessage = packet.Message;

            if (packet.AckCode == AckCodes.SessionJoined)
            {
                _ = _sessionClient.ApplyJoinAckAsync(packet, HostAddress, Port);
                IsConnected = true;

                IsConnecting = false;
                // 💡 변경: Success 우선순위 적용
                UpdateStatus("강의 세션 연결에 성공했습니다. 실시간 스트리밍 및 채팅이 가능합니다.", StatusPriority.Success);

                ConnectionState = "연결됨";
               
                SessionSummary = $"{_sessionClient.CurrentSession?.SessionName} / {HostAddress}:{Port}";
                LastSuccessMessage = "세션 참가 성공";
                LastErrorMessage = "오류 없음";
                ChatStatus = "채팅 가능";
                ChatMessages.Add(ChatLine.System($"{DisplayName} 님이 세션에 참가했습니다."));
                _ = SendRdpInvitationRequestAsync();
            }
            else if (packet.AckCode == AckCodes.SessionLeft)
            {
                IsConnected = false;
                IsConnecting = false;
                // 💡 변경: Info 우선순위 적용
                UpdateStatus("세션에서 정상적으로 퇴장했습니다.", StatusPriority.Info);

                ConnectionState = "연결 종료";
                SessionSummary = "아직 참가한 세션이 없습니다.";
                LastSuccessMessage = "세션 이탈 처리 완료";
                ChatStatus = "채팅 대기 중";
            }
            else if (packet.AckCode == AckCodes.RdpSharingStarted)
            {
                // U03: 공유 재시작 시 학생 조작 없이 새 연결 ID로 초대를 다시 요청한다. 이미 받은 초대/연결은 건드리지 않는다.
                if (IsConnected && !_disposing && _activeRdpInvitation is null && !IsRdpActive &&
                    packet.SessionId == _sessionClient.CurrentSession?.SessionId)
                {
                    ResetStatusPriority();
                    UpdateStatus("교수자가 화면 공유를 시작했습니다. 연결하는 중입니다.", StatusPriority.Info);
                    _ = SendRdpInvitationRequestAsync();
                }
            }

            _logSink.Write($"서버 응답 수신: {packet.AckCode} - {packet.Message}");
            SyncLogs();
        });

        await Task.CompletedTask;
    }

    private async Task HandleErrorAsync(ErrorPacket packet)
    {
        RunOnUiThread(() =>
        {
            LastErrorMessage = $"{packet.ErrorCode}: {packet.Message}";
            LastServerMessage = packet.Message;

            IsConnecting = false;

            if (IsConnected && packet.ErrorCode == ErrorCodes.RdpSharingNotStarted)
            {
                // 참가 직후 교수자가 아직 공유 전이면 서버가 대기열에 올려 두고, 공유가 시작되면 자동으로 다시 연결한다.
                // 오류가 아니라 대기 상태이므로 지속되는 오류 표시로 남기지 않는다.
                ResetStatusPriority();
                UpdateStatus("교수자가 화면 공유를 시작하면 자동으로 연결됩니다.", StatusPriority.Info);
            }
            else
                UpdateStatus($"[서버 에러] {packet.Message}", StatusPriority.Error, isError: true);

            if (!IsConnected)
            {
                ConnectionState = "연결 실패";
            }
            _logSink.Write($"서버 오류 수신: {packet.ErrorCode} - {packet.Message}");
            SyncLogs();
        });

        if (!IsConnected)
        {
            Volatile.Read(ref _pendingJoinAck)?.TrySetResult(JoinAckResult.Rejected);
            await _tcpClient.DisconnectAsync();
            await _sessionClient.DisconnectAsync(packet.Message);
        }
    }

    private void HandleChat(ChatPacket packet)
    {
        RunOnUiThread(() =>
        {
    
            if (packet.IsSystemMessage)
            {
                ChatMessages.Add(ChatLine.System(packet.Message));
                ChatStatus = $"시스템 안내 수신: {packet.Message}";
            }
            else
            {
                ChatMessages.Add(ChatLine.User(packet.Sender, packet.Message, isSelf: packet.Sender == DisplayName));
                ChatStatus = $"최근 수신: {packet.Sender} - {packet.Message}";
            }

            _logSink.Write($"채팅 수신: {packet.Sender}");
            SyncLogs();
        });
    }

    private async Task HandleScreenAsync(ScreenPacket packet)
    {
        var frameGeneration = Volatile.Read(ref _frameGeneration);
        string renderStatus;
        try
        {
            renderStatus = _screenRenderer.Render(packet);
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                RenderStatus = $"화면 수신 실패: {ex.Message}";
                ScreenDetail = "프레임 메타데이터 검증 실패";
                LastErrorMessage = $"SCREEN_RENDER_FAILED: {ex.Message}";
                UpdateStatus($"화면 수신 실패: {ex.Message}", StatusPriority.Error, isError: true);
                _logSink.Write($"화면 수신 실패: {ex.Message}");
                SyncLogs();
            });
            return;
        }

        BitmapImage? bitmap = null;
        string? decodeError = null;
        try
        {
            bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(packet.Content);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            });
        }
        catch (Exception ex)
        {
            decodeError = ex.Message;
            _logSink.Write($"프레임 디코딩 실패: {ex.Message}");
        }

        RunOnUiThread(() =>
        {
            if (frameGeneration != Volatile.Read(ref _frameGeneration) || _disposing) return;
            // 프레임 신선도/누락 감지는 디코딩 성공 여부와 무관하게 기록 (패킷 자체는 도착했으므로)
            _lastFrameReceivedAt = DateTimeOffset.UtcNow;
            if (_lastFrameIndex.HasValue && packet.FrameIndex > _lastFrameIndex.Value + 1)
            {
                var missedCount = packet.FrameIndex - _lastFrameIndex.Value - 1;
                _logSink.Write($"[Screen] 프레임 누락 감지: #{_lastFrameIndex.Value} 이후 {missedCount}개 프레임 누락, 현재 #{packet.FrameIndex}");
            }
            _lastFrameIndex = packet.FrameIndex;

            if (decodeError is not null)
            {
                RenderStatus = $"프레임 #{packet.FrameIndex} 디코딩 실패: {decodeError}";
                ScreenDetail = $"{packet.Width}x{packet.Height} / {packet.Encoding} / {packet.ContentLength} bytes — 디코딩 실패";
                LastErrorMessage = $"SCREEN_DECODE_FAILED: {decodeError}";
                UpdateStatus($"화면 프레임 디코딩 실패 (#{packet.FrameIndex})", StatusPriority.Error, isError: true);
                _logSink.Write($"[Screen] 프레임 디코딩 실패: #{packet.FrameIndex}, {decodeError}");
                SyncLogs();
                return;
            }

            DisplaySource = bitmap;
            HasRemoteFrame = true;

            RenderStatus = renderStatus;
            LastServerMessage = "화면 프레임을 수신했습니다.";
            LastSuccessMessage = $"화면 프레임 #{packet.FrameIndex} 수신 성공";
            ScreenDetail = $"{packet.Width}x{packet.Height} / {packet.Encoding} / {packet.ContentLength} bytes / {packet.CapturedAt:HH:mm:ss}";
            UpdateStatus("화면 프레임 수신 중", StatusPriority.Info);

            _logSink.Write($"[Screen] 화면 프레임 수신: #{packet.FrameIndex}, {packet.Width}x{packet.Height}, {packet.Encoding}");
            SyncLogs();
        });
    }

    private async Task HandleFileAsync(FilePacket packet)
    {
        try
        {
            // 🟢 1. 클라이언트 프로세스 ID별 독립된 임시 폴더 생성
            string baseTempPath = Path.Combine(Path.GetTempPath(), "EduStreamClient", Environment.ProcessId.ToString());

            // 🟢 2. 파일 저장 처리(I/O)를 백그라운드 스레드에서 수행하여 UI Freeze(응답 없음) 방지
            var result = await Task.Run(() => _fileReceiver.TrySaveAsync(packet, baseTempPath));

            // 🟢 3. 진행 중 (Pending)
            if (result.Pending)
            {
                // UI 폭주 방지: 진행률(%)이 이전과 다를 때만 UI 업데이트 실행
                if (_lastProgressPercent != result.ProgressPercent)
                {
                    _lastProgressPercent = result.ProgressPercent;

                    RunOnUiThread(() =>
                    {
                        DownloadStatus = result.StatusMessage;
                        FileTransferDetail = BuildFileTransferDetail(packet, result);
                        LastServerMessage = "파일을 수신 중입니다.";

                        // 진행 중 status 반영
                        UpdateStatus($"파일 수신 중: {result.ProgressPercent}% ({packet.FileName})", StatusPriority.Progress, source: "file");

                        _logSink.Write($"파일 청크 수신 중: transfer={packet.TransferId}, progress={result.ReceivedChunkCount}/{result.TotalChunks}");
                        SyncLogs();
                    });
                }

                return;
            }

            // 🟢 4. 완료 처리 준비 (진행률 변수 초기화)
            _lastProgressPercent = -1;

            if (!result.Success || string.IsNullOrWhiteSpace(result.FilePath))
            {
                var code = string.IsNullOrWhiteSpace(result.ErrorCode) ? "UNKNOWN_ERROR" : result.ErrorCode;
                var message = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "알 수 없는 파일 수신 오류" : result.ErrorMessage;
                throw new InvalidOperationException($"{code}: {message}");
            }

            var path = result.FilePath;

            // 🟢 5. 수신 및 저장 완료 UI 반영
            RunOnUiThread(() =>
            {
                DownloadedFiles.Insert(0, Path.GetFileName(path));
                DownloadStatus = result.StatusMessage;
                LastServerMessage = "파일 수신이 완료되었습니다.";
                LastSuccessMessage = result.StatusMessage;
                FileTransferDetail = $"{BuildFileTransferDetail(packet, result)} / 저장 위치 {path}";

                // 수신 완료 메시지 확정
                UpdateStatus($"파일 수신 완료: {Path.GetFileName(path)} (100%)", StatusPriority.Success, source: "file");

                _logSink.Write($"파일 저장 완료: {path}");
                SyncLogs();
            });
        }
        catch (Exception ex)
        {
            _lastProgressPercent = -1; // 예외 발생 시 진행률 리셋

            RunOnUiThread(() =>
            {
                DownloadStatus = $"파일 저장 실패: {ex.Message}";
                FileTransferDetail = $"{packet.FileName} 저장 실패";
                LastErrorMessage = $"FILE_RECEIVE_FAILED: {ex.Message}";

                UpdateStatus($"파일 저장 실패: {ex.Message}", StatusPriority.Error, isError: true, source: "file");

                _logSink.Write($"파일 저장 실패: {ex.Message}");
                SyncLogs();
            });
        }
    }

    private static string BuildFileTransferDetail(FilePacket packet, FileReceiveResult result)
    {
        if (result.TotalChunks > 0)
        {
            return $"{packet.FileName} / {result.ReceivedChunkCount} of {result.TotalChunks} chunks / {result.ProgressPercent}%";
        }

        return $"{packet.FileName} / 청크 {packet.ChunkIndex + 1} of {packet.TotalChunks}";
    }

    private async Task OnDisconnectedAsync(string reason)
    {
        Volatile.Read(ref _pendingJoinAck)?.TrySetResult(JoinAckResult.Disconnected);
        Interlocked.Increment(ref _frameGeneration);
        await ResetRdpAsync();
        await ReplaceSecureChannelAsync(null);
        var wasJoined = false;
        RunOnUiThread(() =>
        {
            if (IsConnected)
            {
                wasJoined = true;
                // 자동 재연결 대상이면 끊김 순간에도 강의 화면을 유지한다.
                SetReconnecting(!_userLeaving && !_disposing && !_sessionEnded &&
                                _reconnectToken is not null && _lastJoin is not null);
                IsConnected = false;
                IsConnecting = false;
                HasRemoteFrame = false;
                DisplaySource = null;
                UpdateStatus($"서버와의 연결이 차단되었습니다: {reason}", StatusPriority.Error, isError: true);

                ConnectionState = "연결 끊김";
                LastServerMessage = reason;
                ChatStatus = "채팅 대기 중";
                ChatMessages.Add(ChatLine.System("서버와의 연결이 끊어졌습니다."));
                _logSink.Write($"서버 연결 끊김: {reason}");
                SyncLogs();

                _ = _sessionClient.DisconnectAsync(reason);
            }
        });

        if (wasJoined && !_userLeaving && !_disposing && !_sessionEnded &&
            Interlocked.Exchange(ref _reconnectToken, null) is { } token && _lastJoin is { } target)
            _ = RunReconnectAsync(target, token);
        else if (wasJoined)
            RunOnUiThread(() => SetReconnecting(false));
    }

    /// <summary>
    /// 비정상 끊김 뒤 교수자가 준 토큰으로 비밀번호 재입력 없이 다시 참가합니다. 토큰은 한 번만 쓸 수 있어,
    /// 교수자 PC에 닿지 않았을 때만 같은 토큰으로 다시 시도하고 거부되면 그만둡니다.
    /// </summary>
    private async Task RunReconnectAsync(JoinTarget target, string token)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _reconnectCts, cts)?.Cancel();
        RunOnUiThread(() =>
        {
            IsConnecting = true;
            ConnectionState = "재연결 중...";
            ChatMessages.Add(ChatLine.System("연결이 끊겨 자동으로 다시 연결합니다."));
        });

        bool rejoined;
        var currentToken = token;
        try
        {
            rejoined = await ReconnectScheduler.RunAsync(
                async (attempt, cancellationToken) =>
                {
                    var (outcome, nextToken) = await TryReconnectOnceAsync(target, currentToken, attempt, cancellationToken);
                    if (nextToken is not null) currentToken = nextToken;
                    return outcome;
                },
                _reconnectWindow, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            Interlocked.CompareExchange(ref _reconnectCts, null, cts);
            RunOnUiThread(() => SetReconnecting(false));
        }

        if (!rejoined && !_userLeaving && !_disposing)
        {
            RunOnUiThread(() =>
            {
                IsConnecting = false;
                ConnectionState = "연결 끊김";
                UpdateStatus("자동 재연결에 실패했습니다. 참가 정보를 확인하고 다시 참가해 주세요.", StatusPriority.Error, isError: true);
                SyncLogs();
            });
        }
    }

    /// <returns>시도 결과와, 서버가 이번 시도 중 새로 발급한 토큰(다음 시도에 쓸 것)입니다.</returns>
    private async Task<(ReconnectAttemptOutcome Outcome, string? NextToken)> TryReconnectOnceAsync(JoinTarget target,
        string token, int attempt, CancellationToken cancellationToken)
    {
        if (_userLeaving || _disposing || _sessionEnded) return (ReconnectAttemptOutcome.GiveUp, null);
        RunOnUiThread(() => StatusMessage = $"연결이 끊겨 다시 연결하는 중입니다... ({attempt}번째 시도)");

        SecureSessionChannel secure;
        try
        {
            secure = await SecureRoomJoinClient.AuthenticateAsync(target.Host, target.Port, target.DisplayName,
                ReadOnlyMemory<char>.Empty, _logSink, cancellationToken: cancellationToken, reconnectToken: token);
        }
        catch (SecureJoinException ex) when (ex.Failure == SecureJoinFailure.Unreachable)
        {
            return (ReconnectAttemptOutcome.RetryLater, null);
        }
        catch (SecureJoinException ex)
        {
            _logSink.Write($"[Reconnect] 재연결 거부: {ex.Failure}");
            return (ReconnectAttemptOutcome.GiveUp, null);
        }

        // 토큰은 이미 소비됐다. 서버의 참가 승인을 받기 전에 끝나면 같은 토큰으로는 다시 시도할 수 없다.
        JoinAckResult result;
        try
        {
            await ReplaceSecureChannelAsync(secure);
            AttachStudentStatus(secure);
            result = await SendJoinAndAwaitAckAsync(target.Host, target.Port, target.DisplayName, secure.JoinTicket, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logSink.Write($"[Reconnect] 재참가 요청 실패: {ex.GetType().Name}");
            result = JoinAckResult.Disconnected;
        }

        if (result == JoinAckResult.Joined)
        {
            _logSink.Write($"[Reconnect] 재참가 승인 ({attempt}번째 시도)");
            return (ReconnectAttemptOutcome.Succeeded, null);
        }

        _logSink.Write($"[Reconnect] 재참가 승인 실패: {result}");
        await AbandonJoinAsync();
        // 서버가 참가를 처리해 새 토큰을 보낸 뒤 끊긴 경우에만 그 토큰으로 이어서 시도한다.
        var next = Interlocked.Exchange(ref _reconnectToken, null);
        return result != JoinAckResult.Rejected && next is not null && next != token
            ? (ReconnectAttemptOutcome.RetryLater, next)
            : (ReconnectAttemptOutcome.GiveUp, null);
    }

    /// <summary>
    /// TCP로 참가 요청을 보내고 이 요청에 대한 서버의 참가 승인을 기다립니다. 요청 전송은 참가 성공이 아니므로
    /// 승인(Joined)을 받았을 때만 참가한 것으로 봅니다.
    /// </summary>
    private async Task<JoinAckResult> SendJoinAndAwaitAckAsync(string host, int port, string displayName, string joinTicket,
        CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<JoinAckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingJoinAck, pending)?.TrySetResult(JoinAckResult.Disconnected);
        try
        {
            await _tcpClient.ConnectAsync(host, port);
            await _tcpClient.SendAsync(_sessionClient.CreateJoinRequest(host, port, displayName, joinTicket));
            _logSink.Write($"세션 참가 요청 전송: {displayName} -> {host}:{port}");

            // 승인과 시간 초과·취소가 겹쳐도 먼저 기록된 한쪽만 인정한다.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(JoinAckTimeout);
            JoinAckResult result;
            using (timeout.Token.Register(() => pending.TrySetResult(JoinAckResult.TimedOut)))
                result = await pending.Task;
            if (result == JoinAckResult.TimedOut) cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingJoinAck, null, pending);
        }
    }

    /// <summary>승인받지 못한 참가 시도의 TCP 연결과 보호 채널을 닫습니다.</summary>
    private async Task AbandonJoinAsync()
    {
        await _tcpClient.DisconnectAsync();
        await ReplaceSecureChannelAsync(null);
    }

    private void CancelReconnect() => Interlocked.Exchange(ref _reconnectCts, null)?.Cancel();

    private async Task ResetRdpAsync()
    {
        lock (_rdpAutoConnectLock)
        {
            _pendingRdpSecret = null;
            _startedRdpAutoConnections.Clear();
            _currentRdpConnectionId = Guid.Empty;
            _activeRdpInvitation = null;
            _rdpSessionId = Guid.Empty;
            _rdpParticipant = string.Empty;
        }
        RunOnUiThread(() => { IsRdpActive = false; RdpStatusText = "RDP 대기 중"; });
        await _rdpViewerService.DisconnectAsync();
    }

    public async Task ShutdownAsync()
    {
        if (_disposing) return;
        _disposing = true;
        _freshnessTimer.Stop();
        await DisconnectAsync();
        await _rdpViewerService.DisposeAsync();
    }

    /// <summary>보호 채널은 참가 연결과 수명을 같이한다. 새 참가·퇴장·끊김 때 이전 채널을 닫는다.</summary>
    private async Task ReplaceSecureChannelAsync(SecureSessionChannel? next)
    {
        var previous = Interlocked.Exchange(ref _secureChannel, next);
        if (previous is not null && !ReferenceEquals(previous, next))
        {
            if (next is null) DetachStudentStatus();
            await previous.DisposeAsync();
        }
    }

    private void AttachStudentStatus(SecureSessionChannel secure)
    {
        var statusClient = new StudentStatusClient(secure.SessionId, secure.Connection, _logSink);
        statusClient.StatusChanged += status => RunOnUiThread(() => ApplyStudentStatus(status));
        var fileClient = new SessionFileRequestClient(secure.SessionId, secure.Connection, new SessionFileDownloader(), _logSink);
        fileClient.CatalogChanged += catalog => RunOnUiThread(() => ApplyFileCatalog(catalog));
        var reverseClient = new ReverseCollaborationClient(secure.SessionId, secure.Connection, _logSink);
        statusClient.RoomChanged += reverseClient.ApplyRoom;
        // 서버가 확정한 보기 허용과 교수자/본인 연결이 정해진 뒤에만 이 PC의 화면 공유를 연다(초기 표시값으로 시작하지 않음).
        var reverseShare = new StudentReverseShareService(reverseClient, _logSink);
        reverseShare.SharingChanged += (_, message) => RunOnUiThread(() => ChatMessages.Add(ChatLine.System(message)));
        statusClient.RoomChanged += _ => reverseShare.UpdateAsync(statusClient.Status.AllowViewing);
        secure.FrameReceived += frame =>
        {
            try
            {
                var kind = CollaborationFrameInspector.PeekKind(frame);
                if (StudentStatusClient.Handles(kind))
                    statusClient.HandleFrame(frame);
                else if (kind == CollaborationMessageKind.ReconnectGrant)
                {
                    var grant = CollaborationMessageCodec.Decode<ReconnectGrantNotice>(frame, out _);
                    _reconnectToken = grant.Token;
                    _reconnectWindow = TimeSpan.FromSeconds(grant.WindowSeconds);
                }
                else if (kind == CollaborationMessageKind.SessionEnded)
                {
                    // 교수자가 세션을 끝냈으므로 이후 끊김은 자동 재연결 대상이 아니다.
                    CollaborationMessageCodec.Decode<SessionEndedNotice>(frame, out _);
                    _sessionEnded = true;
                    _reconnectToken = null;
                }
                else if (kind == CollaborationMessageKind.RdpInvitationSecret)
                {
                    var secret = CollaborationMessageCodec.Decode<RdpInvitationSecretNotice>(frame, out _);
                    if (secret.SessionId == secure.SessionId) OnRdpInvitationSecret(secret);
                }
                else if (ReverseCollaborationClient.Handles(kind))
                    // 판서는 렌더러 적용이 끝난 뒤 다음 번호를 받아야 하므로 수신 루프에서 await한다.
                    return reverseClient.HandleFrameAsync(frame);
                else if (kind == CollaborationMessageKind.Failure &&
                         reverseClient.HandleFailure(CollaborationMessageCodec.Decode<CollaborationFailureNotice>(frame, out _)))
                    return Task.CompletedTask;
                else if (kind is CollaborationMessageKind.FileCatalog or CollaborationMessageKind.FileChunk
                         or CollaborationMessageKind.Failure)
                    // 청크는 저장이 따라올 때까지 기다려야 하므로 수신 루프에서 await한다.
                    return fileClient.HandleFrameAsync(frame);
            }
            catch (CollaborationException ex)
            {
                _logSink.Write($"[Secure] 잘못된 메시지 무시: 사유={ex.Code}");
            }
            return Task.CompletedTask;
        };
        _statusClient = statusClient;
        _fileClient = fileClient;
        _reverseClient = reverseClient;
        StopReverseShare(Interlocked.Exchange(ref _reverseShare, reverseShare));
        _permissionNoticeShown = false;
        RunOnUiThread(() => ApplyStudentStatus(StudentStatus.Initial));
    }

    private void DetachStudentStatus()
    {
        _statusClient = null;
        _reverseClient = null;
        StopReverseShare(Interlocked.Exchange(ref _reverseShare, null));
        // 진행 중인 다운로드는 임시 파일을 지우고 실패로 끝난다.
        Interlocked.Exchange(ref _fileClient, null)?.ConnectionClosed();
        RunOnUiThread(() =>
        {
            ApplyStudentStatus(StudentStatus.Initial);
            SessionFiles.Clear();
        });
    }

    /// <summary>이전 연결의 역방향 공유는 새 연결과 섞이지 않도록 닫는다. 정리 실패는 서비스가 로그로 남긴다.</summary>
    private static void StopReverseShare(StudentReverseShareService? share)
    {
        if (share is not null) _ = share.DisposeAsync().AsTask();
    }

    /// <summary>목록이 바뀌어도 받는 중인 항목의 진행 표시는 유지하고, 사라진 파일만 내립니다.</summary>
    private void ApplyFileCatalog(SessionFileCatalogSnapshot catalog)
    {
        var current = SessionFiles.ToDictionary(item => (item.File.FileId, item.File.Revision));
        var next = catalog.Files.Select(file => (file.FileId, file.Revision)).ToHashSet();
        foreach (var item in SessionFiles.Where(item => !next.Contains((item.File.FileId, item.File.Revision))).ToArray())
            SessionFiles.Remove(item);
        foreach (var file in catalog.Files)
        {
            if (!current.ContainsKey((file.FileId, file.Revision)))
                SessionFiles.Add(new SessionFileItem(file, DownloadSessionFileAsync));
        }
    }

    private async Task DownloadSessionFileAsync(SessionFileItem item)
    {
        var fileClient = _fileClient;
        if (fileClient is null || item.IsDownloading) return;
        item.IsDownloading = true;
        item.Status = "받는 중 0%";
        // UI 스레드에서 만든 Progress는 보고를 UI 스레드로 돌려준다.
        var progress = new Progress<DownloadProgress>(report =>
            item.Status = report.TotalBytes == 0 ? "받는 중" : $"받는 중 {report.ReceivedBytes * 100 / report.TotalBytes}%");
        try
        {
            var receipt = await fileClient.DownloadAsync(item.File.FileId, progress);
            item.Status = "저장됨 (다운로드 폴더)";
            DownloadedFiles.Insert(0, Path.GetFileName(receipt.LocalPath));
            DownloadStatus = $"{item.File.FileName}을(를) 다운로드 폴더에 저장했습니다.";
        }
        catch (Exception ex)
        {
            var failure = CollaborationErrorCatalog.FromException(ex);
            item.Status = "실패: " + failure.UserMessage;
            DownloadStatus = $"{item.File.FileName} 다운로드 실패: {failure.UserMessage}";
            _logSink.Write($"[FileRoute] 다운로드 실패: {failure.Code}");
        }
        finally
        {
            item.IsDownloading = false;
            SyncLogs();
        }
    }

    private void ApplyStudentStatus(StudentStatus status)
    {
        var wasUnderControl = _studentStatus.UnderControl;
        _studentStatus = status;
        OnPropertyChanged(nameof(PermissionSummary));
        OnPropertyChanged(nameof(ControlStatusText));
        OnPropertyChanged(nameof(IsUnderControl));
        OnPropertyChanged(nameof(AllowControl));
        OnPropertyChanged(nameof(AllowViewing));
        OnPropertyChanged(nameof(ControlToggleLabel));
        OnPropertyChanged(nameof(ViewingToggleLabel));
        ToggleControlPermissionCommand.RaiseCanExecuteChanged();
        ToggleViewingPermissionCommand.RaiseCanExecuteChanged();
        StopControlNowCommand.RaiseCanExecuteChanged();

        if (_statusClient is null) return;
        if (!_permissionNoticeShown && status.AllowControl)
        {
            // U07: 제어 허용이 기본 ON이라는 사실을 참가 시 알린다.
            _permissionNoticeShown = true;
            ChatMessages.Add(ChatLine.System("교수자 원격 제어 허용이 켜져 있습니다. 접속 상태 옆에서 언제든 끌 수 있습니다."));
        }
        if (wasUnderControl != status.UnderControl)
            ChatMessages.Add(ChatLine.System(status.UnderControl ? "교수자가 원격 제어를 시작했습니다." : "원격 제어가 끝났습니다."));
    }

    private async Task ChangePermissionsAsync(bool allowViewing, bool allowControl)
    {
        var statusClient = _statusClient;
        if (statusClient is null) return;
        try
        {
            await statusClient.SetPermissionsAsync(allowViewing, allowControl);
        }
        catch (Exception ex)
        {
            _logSink.Write($"[Status] 허용 변경 전송 실패: {ex.GetType().Name}");
            RunOnUiThread(() => UpdateStatus("허용 상태를 바꾸지 못했습니다. 연결 상태를 확인해 주세요.", StatusPriority.Error, isError: true));
        }
    }

    private static string DescribeSecureJoinFailure(SecureJoinFailure failure) => failure switch
    {
        SecureJoinFailure.InvalidAddress => "교수자 IP 형식이 올바르지 않습니다. 교수자 화면에 표시된 IP와 고급 설정의 포트(기본 5000)를 확인해 주세요.",
        SecureJoinFailure.InvalidCertificate => "교수자 앱의 TLS 인증서가 유효하지 않습니다. 교수자 PC의 시간과 인증서를 확인해 주세요.",
        SecureJoinFailure.PasswordRejected => "방 비밀번호가 올바르지 않습니다.",
        SecureJoinFailure.LockedOut => "비밀번호를 여러 번 틀려 잠시 참가할 수 없습니다. 1분 뒤 다시 시도해 주세요.",
        SecureJoinFailure.VersionMismatch => "교수자 앱과 버전이 맞지 않습니다. 같은 버전의 앱을 사용해 주세요.",
        _ => "교수자 PC에 연결하지 못했습니다. 호스트 주소와 포트, 교수자 세션 상태를 확인해 주세요."
    };

    private void ApplyJoinError(ErrorPacket error)
    {
        IsConnecting = false;

        // 💡 직접 할당 대신 UpdateStatus를 호출하여 최우선순위(Error)로 에러 메시지 주입
        UpdateStatus(error.Message, StatusPriority.Error, isError: true);

        ConnectionState = "연결 실패";
        LastServerMessage = "세션 참가 요청이 거절되었습니다.";
        LastSuccessMessage = "아직 성공한 작업이 없습니다.";
        LastErrorMessage = $"{error.ErrorCode}: {error.Message}";
        ChatStatus = "채팅 대기 중";
        _logSink.Write($"세션 참가 실패: {error.ErrorCode}, {error.Message}");
        SyncLogs();
    }

    private void SyncLogs()
    {
        // 실제 앱에서는 UI Dispatcher로 보내고, Application이 없는 테스트/호스트에서도
        // 종료와 수신 콜백이 같은 ObservableCollection을 Clear/Add로 동시에 바꾸지 않게 한다.
        // Dispatcher 대기 전에 잠금을 잡으면 UI 스레드와 교착할 수 있으므로 안쪽에서 직렬화한다.
        RunOnUiThread(() =>
        {
            lock (_activityLogSync)
            {
                ActivityLogs.Clear();
                foreach (var entry in _logSink.Snapshot().Reverse())
                    ActivityLogs.Add(entry);
            }
        });
    }

    /// <summary>
    /// UI 상단/하단 상태 메시지의 표시 우선순위를 정의합니다.
    /// 숫자가 높을수록 높은 우선순위를 가집니다.
    /// </summary>
    public enum StatusPriority
    {
        Idle = 0,         // 기본 대기 상태
        Info = 1,         // 일반 정보 (화면 프레임 수신, 단순 안내 등)
        Success = 2,      // 작업 완료/성공 (파일 저장 완료, 세션 참가 등)
        Progress = 3,     // 진행 중인 주요 작업 (파일 다운로드 중)
        Error = 4         // 오류 및 연결 끊김 (최우선 표시)
    }

    private StatusPriority _currentStatusPriority = StatusPriority.Idle;
    private string? _statusSource;

    /// <summary>
    /// 우선순위에 따라 시스템 상태 메시지를 안전하게 갱신합니다.
    /// </summary>
    private void UpdateStatus(string message, StatusPriority priority, bool isError = false, string? source = null)
    {
        RunOnUiThread(() =>
        {
            // 현재 표기 중인 상태보다 낮거나 같은 우선순위의 단순 정보는 덮어쓰지 않음
            // (단, 같은 우선순위의 Error나 Progress, Success는 최신 내용으로 갱신)
            // 같은 파일 흐름의 진행→완료, 실패→재시도 전이는 우선순위 하락이어도 반영한다.
            // 연결 끊김 등 다른 기능의 높은 우선순위 오류는 파일 완료로 덮지 않는다.
            if (priority < _currentStatusPriority && !(source is not null && source == _statusSource))
            {
                return;
            }

            _currentStatusPriority = priority;
            _statusSource = source;
            IsStatusError = isError;
            StatusMessage = message;
        });
    }

    /// <summary>
    /// 일정한 성공/완료 메시지 표시 후 기본 상태(Idle)로 복귀할 때 사용합니다.
    /// </summary>
    private void ResetStatusPriority()
    {
        _currentStatusPriority = StatusPriority.Idle;
    }
    private static void RunOnUiThread(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }
}
