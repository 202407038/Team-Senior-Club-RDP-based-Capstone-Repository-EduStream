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

    public ClientViewModel()
    {
        _sessionClient = new SessionClient(_logSink);
        _screenRenderer = new ScreenRenderer();
        _fileReceiver = new FileReceiver();
        _tcpClient = new TcpClientService(_logSink, _serializer);

        _tcpClient.PacketReceived += OnPacketReceivedAsync;
        _tcpClient.Disconnected += OnDisconnectedAsync;

        JoinSessionCommand = new RelayCommand(() => _ = JoinSessionAsync(), () => !IsConnected);
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

    public ObservableCollection<string> ActivityLogs { get; } = [];

    public ObservableCollection<string> ChatMessages { get; } = [];

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
            ConnectionState = "연결 중...";
            _logSink.Write($"서버 연결 시도: {HostAddress}:{Port}");
            SyncLogs();

            // TCP 연결
            await _tcpClient.ConnectAsync(HostAddress, Port);

            // Join 패킷 전송
            var joinRequest = _sessionClient.CreateJoinRequest(HostAddress, Port, DisplayName);
            await _tcpClient.SendAsync(joinRequest);

            _logSink.Write($"세션 참가 요청 전송: {DisplayName} -> {HostAddress}:{Port}");
            SyncLogs();
        }
        catch (Exception ex)
        {
            ApplyJoinError(_sessionClient.CreateJoinError(HostAddress, Port, ex.Message));
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

            ChatMessages.Insert(0, $"[시스템] {DisplayName} 님이 세션에서 나갔습니다.");
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
                ConnectionState = "연결됨";
                SessionSummary = $"{_sessionClient.CurrentSession?.SessionName} / {HostAddress}:{Port}";
                LastSuccessMessage = "세션 참가 성공";
                LastErrorMessage = "오류 없음";
                ChatStatus = "채팅 가능";
                ChatMessages.Insert(0, $"[시스템] {DisplayName} 님이 세션에 참가했습니다.");
            }
            else if (packet.AckCode == AckCodes.SessionLeft)
            {
                IsConnected = false;
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
            var prefix = packet.IsSystemMessage ? "[시스템]" : packet.Sender;
            ChatMessages.Insert(0, $"{prefix}: {packet.Message}");
            ChatStatus = packet.IsSystemMessage
                ? $"시스템 안내 수신: {packet.Message}"
                : $"최근 수신: {packet.Sender} - {packet.Message}";
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
                _logSink.Write($"화면 프레임 수신: #{packet.FrameIndex}, {packet.Width}x{packet.Height}");
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
                _logSink.Write($"화면 수신 실패: {ex.Message}");
                SyncLogs();
            });
        }
    }

    private async Task HandleFileAsync(FilePacket packet)
    {
        try
        {
            var result = await _fileReceiver.TrySaveAsync(packet, Path.Combine(Path.GetTempPath(), "EduStreamClient"));

            if (result.Pending)
            {
                RunOnUiThread(() =>
                {
                    DownloadStatus = "파일 청크 수신 중";
                    FileTransferDetail = $"{packet.FileName} / 청크 {packet.ChunkIndex + 1} of {packet.TotalChunks}";
                    LastServerMessage = "파일을 수신 중입니다.";
                    _logSink.Write($"파일 청크 수신 중: transfer={packet.TransferId}, chunk={packet.ChunkIndex + 1}/{packet.TotalChunks}");
                    SyncLogs();
                });

                return;
            }

            if (!result.Success || string.IsNullOrWhiteSpace(result.FilePath))
            {
                var code = string.IsNullOrWhiteSpace(result.ErrorCode) ? "UNKNOWN_ERROR" : result.ErrorCode;
                var message = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "알 수 없는 파일 수신 오류" : result.ErrorMessage;
                throw new InvalidOperationException($"{code}: {message}");
            }

            var path = result.FilePath;

            RunOnUiThread(() =>
            {
                DownloadedFiles.Insert(0, Path.GetFileName(path));
                DownloadStatus = $"{Path.GetFileName(path)} 저장 완료";
                LastServerMessage = "파일 수신이 완료되었습니다.";
                LastSuccessMessage = $"{Path.GetFileName(path)} 저장 성공";
                FileTransferDetail = $"{packet.FileName} / 총 {packet.TotalChunks}개 청크 / 저장 위치 {path}";
                _logSink.Write($"파일 저장 완료: {path}");
                SyncLogs();
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                DownloadStatus = $"파일 저장 실패: {ex.Message}";
                FileTransferDetail = $"{packet.FileName} 저장 실패";
                LastErrorMessage = $"FILE_RECEIVE_FAILED: {ex.Message}";
                _logSink.Write($"파일 저장 실패: {ex.Message}");
                SyncLogs();
            });
        }
    }

    private Task OnDisconnectedAsync(string reason)
    {
        RunOnUiThread(() =>
        {
            if (IsConnected)
            {
                IsConnected = false;
                ConnectionState = "연결 끊김";
                LastServerMessage = reason;
                ChatStatus = "채팅 대기 중";
                ChatMessages.Insert(0, $"[시스템] 서버와의 연결이 끊어졌습니다.");
                _logSink.Write($"서버 연결 끊김: {reason}");
                SyncLogs();

                _ = _sessionClient.DisconnectAsync(reason);
            }
        });

        return Task.CompletedTask;
    }

    private void ApplyJoinError(ErrorPacket error)
    {
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
