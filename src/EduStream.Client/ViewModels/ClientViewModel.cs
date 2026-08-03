using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using EduStream.Client.Services;
using EduStream.Core.Common;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Core.Utils;

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

    private string _hostAddress = "127.0.0.1";
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
    private string _chatInput = string.Empty;
    private bool _isConnected;

    // 💡 UI 연동을 위해 새로 추가한 백엔드 필드들
    private bool _isConnecting;
    private bool _isStatusError;
    private string _statusMessage = string.Empty;

    public ClientViewModel()
    {
        _sessionClient = new SessionClient(_logSink);
        _screenRenderer = new ScreenRenderer();
        _fileReceiver = new FileReceiver();
        _tcpClient = new TcpClientService(_logSink, _serializer);

        _tcpClient.PacketReceived += OnPacketReceivedAsync;
        _tcpClient.Disconnected += OnDisconnectedAsync;

        JoinSessionCommand = new RelayCommand(() => _ = JoinSessionAsync(), () => !IsConnected && !IsConnecting);
        DisconnectCommand = new RelayCommand(() => _ = DisconnectAsync(), () => IsConnected);
        SendChatCommand = new RelayCommand(() => _ = SendChatAsync(), () => IsConnected && !string.IsNullOrWhiteSpace(ChatInput));

        SyncLogs();
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

    // 💡 UI의 로딩창(Visibility)과 연결되는 프로퍼티
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

    private async Task DisconnectAsync()
    {
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
                    HandleScreen(screenPacket);
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
        }
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
            // 💡 변경: Error 우선순위 적용 (isError: true)
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
            // 💡 팀 지침대로 ChatLine.System()과 User() 팩토리 메서드로 분기 처리
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

    private void HandleScreen(ScreenPacket packet)
    {
        try
        {
            var renderStatus = _screenRenderer.Render(packet);
            RunOnUiThread(() =>
            {
                RenderStatus = renderStatus;
                LastServerMessage = "화면 프레임을 수신했습니다.";
                LastSuccessMessage = $"화면 프레임 #{packet.FrameIndex} 수신 성공";
                ScreenDetail = $"{packet.Width}x{packet.Height} / {packet.Encoding} / {packet.ContentLength} bytes / {packet.CapturedAt:HH:mm:ss}";

                // 💡 낮은 우선순위(Info) 적용 -> 파일 수신 중이나 오류 문구를 가리지 않음
                UpdateStatus("화면 프레임 수신 중", StatusPriority.Info);

                _logSink.Write($"[Screen] 화면 프레임 수신: #{packet.FrameIndex}, {packet.Width}x{packet.Height}, {packet.Encoding}");
                SyncLogs();
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                RenderStatus = $"화면 수신 실패: {ex.Message}";
                ScreenDetail = "프레임 메타데이터 검증 실패";
                LastErrorMessage = $"SCREEN_RENDER_FAILED: {ex.Message}";

                // 💡 catch 블록: 화면 수신 실패 시 Error 우선순위(isError: true)로 갱신
                UpdateStatus($"화면 수신 실패: {ex.Message}", StatusPriority.Error, isError: true);

                _logSink.Write($"화면 수신 실패: {ex.Message}");
                SyncLogs();
            });
        }
    }
    private int _lastProgressPercent = -1;

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
                        UpdateStatus($"파일 수신 중: {result.ProgressPercent}% ({packet.FileName})", StatusPriority.Progress);

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
                UpdateStatus($"파일 수신 완료: {Path.GetFileName(path)} (100%)", StatusPriority.Success);

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

                UpdateStatus($"파일 저장 실패: {ex.Message}", StatusPriority.Error, isError: true);

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

    private Task OnDisconnectedAsync(string reason)
    {
        RunOnUiThread(() =>
        {
            if (IsConnected)
            {
                IsConnected = false;
                IsConnecting = false;

                // 💡 UpdateStatus 내부에서 IsStatusError = true 처리까지 완료됩니다.
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

        return Task.CompletedTask;
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

    /// <summary>
    /// 우선순위에 따라 시스템 상태 메시지를 안전하게 갱신합니다.
    /// </summary>
    private void UpdateStatus(string message, StatusPriority priority, bool isError = false)
    {
        RunOnUiThread(() =>
        {
            // 현재 표기 중인 상태보다 낮거나 같은 우선순위의 단순 정보는 덮어쓰지 않음
            // (단, 같은 우선순위의 Error나 Progress, Success는 최신 내용으로 갱신)
            if (priority < _currentStatusPriority)
            {
                return;
            }

            _currentStatusPriority = priority;
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