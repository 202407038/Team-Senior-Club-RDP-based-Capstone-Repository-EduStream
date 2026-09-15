using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using EduStream.Client.Services;
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
    private readonly SessionClient _sessionClient;
    private readonly ScreenRenderer _screenRenderer;
    private readonly FileReceiver _fileReceiver;
    private readonly TcpClientService _tcpClient;
    private readonly IPacketSerializer _serializer = new PacketSerializer();
    private readonly IRdpViewerService _rdpViewerService;
    private RdpInvitationPacket? _activeRdpInvitation;
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
        DisconnectCommand = new RelayCommand(() => _ = DisconnectAsync(), () => IsConnected);
        SendChatCommand = new RelayCommand(() => _ = SendChatAsync(), () => IsConnected && !string.IsNullOrWhiteSpace(ChatInput));
        SimulateScreenRenderCommand = new RelayCommand(() => _ = SimulateScreenRenderAsync());
        SimulateFileReceiveCommand = new RelayCommand(() => _ = SimulateFileReceiveAsync());
        ReconnectRdpCommand = new RelayCommand(() => _ = SendRdpInvitationRequestAsync());
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
                JoinSessionCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                SendChatCommand.RaiseCanExecuteChanged();
            }
        }
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

    public RelayCommand JoinSessionCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand SendChatCommand { get; }

    public RelayCommand SimulateScreenRenderCommand { get; }

    public RelayCommand SimulateFileReceiveCommand { get; }

    public RelayCommand ReconnectRdpCommand { get; }
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

            // TCP 연결 (만약 서버가 꺼져있으면 여기서 catch 블록으로 튕깁니다)
            await _tcpClient.ConnectAsync(HostAddress, Port);

            // Join 패킷 전송
            var joinRequest = _sessionClient.CreateJoinRequest(HostAddress, Port, DisplayName);
            await _tcpClient.SendAsync(joinRequest);

            _logSink.Write($"세션 참가 요청 전송: {DisplayName} -> {HostAddress}:{Port}");
            SyncLogs();

            StatusMessage = "서버의 세션 참여 승인을 대기하고 있습니다.";
        }
        catch (Exception)
        {
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

            ChatMessages.Insert(0, ChatLine.System($"{DisplayName} 님이 세션에서 나갔습니다."));
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
            _currentRdpConnectionId = Guid.NewGuid();
            _rdpSessionId = _sessionClient.CurrentSession.SessionId;
            _rdpParticipant = DisplayName;
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
            RdpInvitationContract.Validate(invitation, _rdpSessionId, _rdpParticipant,
                _currentRdpConnectionId, DateTimeOffset.UtcNow);
            if (_disposing || !IsConnected) return;
            _activeRdpInvitation = invitation;
            RunOnUiThread(() => RdpStatusText = "초대 수신: 설정 탭에서 별도로 전달받은 비밀번호를 입력해 주세요.");
        }
        catch (ArgumentException) { _logSink.Write("[RDP] 유효하지 않거나 이전 연결의 초대 무시"); }
        await Task.CompletedTask;
    }

    public async Task ConnectRdpWithPasswordAsync(string password)
    {
        try
        {
            var invitation = _activeRdpInvitation ?? throw new InvalidOperationException("유효한 초대를 먼저 받아 주세요.");
            RdpInvitationContract.Validate(invitation, _rdpSessionId, _rdpParticipant,
                _currentRdpConnectionId, DateTimeOffset.UtcNow);
            if (!IsConnected || _disposing) throw new InvalidOperationException("강의 연결이 종료됐습니다.");
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
            _logSink.Write($"[RDP] 상태 변경: {status.State}");
            SyncLogs();
        });
    }
    private async Task HandleAckAsync(AckPacket packet)
    {
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
                ChatMessages.Insert(0, ChatLine.System($"{DisplayName} 님이 세션에 참가했습니다."));
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
                ChatMessages.Insert(0, ChatLine.System(packet.Message));
                ChatStatus = $"시스템 안내 수신: {packet.Message}";
            }
            else
            {
                ChatMessages.Insert(0, ChatLine.User(packet.Sender, packet.Message));
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
        Interlocked.Increment(ref _frameGeneration);
        await ResetRdpAsync();
        RunOnUiThread(() =>
        {
            if (IsConnected)
            {
                IsConnected = false;
                IsConnecting = false;
                HasRemoteFrame = false;
                DisplaySource = null;
                UpdateStatus($"서버와의 연결이 차단되었습니다: {reason}", StatusPriority.Error, isError: true);

                ConnectionState = "연결 끊김";
                LastServerMessage = reason;
                ChatStatus = "채팅 대기 중";
                ChatMessages.Insert(0, ChatLine.System("서버와의 연결이 끊어졌습니다."));
                _logSink.Write($"서버 연결 끊김: {reason}");
                SyncLogs();

                _ = _sessionClient.DisconnectAsync(reason);
            }
        });

    }

    private async Task ResetRdpAsync()
    {
        _currentRdpConnectionId = Guid.Empty;
        _activeRdpInvitation = null;
        _rdpSessionId = Guid.Empty;
        _rdpParticipant = string.Empty;
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
        ActivityLogs.Clear();
        foreach (var entry in _logSink.Snapshot().Reverse())
        {
            ActivityLogs.Add(entry);
        }
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
