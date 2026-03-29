using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using EduStream.Client.Services;
using EduStream.Core.Common;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Utils;

namespace EduStream.Client.ViewModels;

/// <summary>
/// 수강생 클라이언트의 세션/채팅/파일/화면 수신 상태를 화면에 표시하기 위한 ViewModel입니다.
/// 실제 네트워크 수신이 붙기 전까지는 최소 시뮬레이션으로 UI 흐름을 검증합니다.
/// </summary>
public sealed class ClientViewModel : ObservableObject
{
    private readonly InMemoryLogSink _logSink = new();
    private readonly SessionClient _sessionClient;
    private readonly ScreenRenderer _screenRenderer;
    private readonly FileReceiver _fileReceiver;
    private string _hostAddress = "127.0.0.1";
    private int _port = 5000;
    private string _displayName = "StudentDemo";
    private string _connectionState = "연결 전";
    private string _sessionSummary = "아직 참가한 세션이 없습니다.";
    private string _lastServerMessage = "서버 응답을 기다리는 중입니다.";
    private string _lastErrorMessage = "오류 없음";
    private string _renderStatus = "화면 프레임을 아직 받지 않았습니다.";
    private string _downloadStatus = "다운로드 대기 중";
    private string _chatInput = string.Empty;
    private bool _isConnected;

    public ClientViewModel()
    {
        _sessionClient = new SessionClient(_logSink);
        _screenRenderer = new ScreenRenderer();
        _fileReceiver = new FileReceiver();

        JoinSessionCommand = new RelayCommand(() => _ = JoinSessionAsync(), () => !IsConnected);
        DisconnectCommand = new RelayCommand(() => _ = DisconnectAsync(), () => IsConnected);
        SendChatCommand = new RelayCommand(SendChat, () => IsConnected && !string.IsNullOrWhiteSpace(ChatInput));
        SimulateScreenRenderCommand = new RelayCommand(SimulateScreenRender, () => IsConnected);
        SimulateFileReceiveCommand = new RelayCommand(() => _ = SimulateFileReceiveAsync(), () => IsConnected);

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

    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        private set => SetProperty(ref _lastErrorMessage, value);
    }

    public string RenderStatus
    {
        get => _renderStatus;
        private set => SetProperty(ref _renderStatus, value);
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
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
                SimulateScreenRenderCommand.RaiseCanExecuteChanged();
                SimulateFileReceiveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<string> ActivityLogs { get; } = [];

    public ObservableCollection<string> ChatMessages { get; } = [];

    public ObservableCollection<string> DownloadedFiles { get; } = [];

    public RelayCommand JoinSessionCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand SendChatCommand { get; }

    public RelayCommand SimulateScreenRenderCommand { get; }

    public RelayCommand SimulateFileReceiveCommand { get; }

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

        var joinRequest = _sessionClient.CreateJoinRequest(HostAddress, Port, DisplayName);
        var ack = new AckPacket
        {
            SessionId = Guid.NewGuid(),
            SenderId = "Server",
            AckCode = AckCodes.SessionJoined,
            Message = $"{DisplayName} 님의 세션 참가 요청이 승인되었습니다."
        };

        var session = await _sessionClient.ApplyJoinAckAsync(ack, HostAddress, Port);
        IsConnected = true;
        ConnectionState = "연결됨";
        SessionSummary = $"{session.SessionName} / {session.DisplayAddress}";
        LastServerMessage = ack.Message;
        LastErrorMessage = "오류 없음";

        ChatMessages.Insert(0, $"[시스템] {DisplayName} 님이 세션에 참가했습니다.");
        _logSink.Write($"세션 참가 요청 생성: {joinRequest.DisplayName} -> {joinRequest.TargetAddress}:{joinRequest.TargetPort}");
        SyncLogs();
    }

    private async Task DisconnectAsync()
    {
        var leavePacket = _sessionClient.CreateLeaveRequest(DisplayName, "사용자 요청으로 연결 종료");
        await _sessionClient.DisconnectAsync(leavePacket.Reason);

        IsConnected = false;
        ConnectionState = "연결 종료";
        SessionSummary = "아직 참가한 세션이 없습니다.";
        LastServerMessage = "세션 종료 요청을 전송했습니다.";
        LastErrorMessage = "오류 없음";
        RenderStatus = "화면 프레임을 아직 받지 않았습니다.";
        DownloadStatus = "다운로드 대기 중";

        ChatMessages.Insert(0, $"[시스템] {DisplayName} 님이 세션에서 나갔습니다.");
        _logSink.Write($"세션 이탈 요청 생성: {leavePacket.SenderId}, reason={leavePacket.Reason}");
        SyncLogs();
    }

    private void SendChat()
    {
        var trimmedMessage = ChatInput.Trim();
        if (string.IsNullOrWhiteSpace(trimmedMessage))
        {
            return;
        }

        ChatMessages.Insert(0, $"{DisplayName}: {trimmedMessage}");
        LastServerMessage = "채팅 메시지를 전송했습니다.";
        _logSink.Write($"채팅 전송: {trimmedMessage}");
        ChatInput = string.Empty;
        SyncLogs();
    }

    private void SimulateScreenRender()
    {
        var packet = new ScreenPacket
        {
            FrameIndex = 1,
            FrameDescription = "Professor sample preview frame",
            Width = 1280,
            Height = 720,
            Encoding = ScreenEncodings.Png,
            Content = Encoding.UTF8.GetBytes("preview-bytes")
        };

        packet.DataLength = packet.ContentLength;
        RenderStatus = _screenRenderer.Render(packet);
        _logSink.Write("화면 수신 상태를 테스트하기 위한 프레임 메타데이터를 반영했습니다.");
        SyncLogs();
    }

    private async Task SimulateFileReceiveAsync()
    {
        var content = Encoding.UTF8.GetBytes("EduStream received sample file");
        var packet = new FilePacket
        {
            FileName = "received-sample.txt",
            FileSize = content.LongLength,
            Content = content,
            Checksum = ChecksumUtility.ComputeSha256(content)
        };

        packet.DataLength = packet.ContentLength;

        var path = await _fileReceiver.SaveAsync(packet, Path.Combine(Path.GetTempPath(), "EduStreamClient"));
        DownloadedFiles.Insert(0, Path.GetFileName(path));
        DownloadStatus = $"{Path.GetFileName(path)} 저장 완료";
        LastServerMessage = "파일 수신 상태를 갱신했습니다.";
        _logSink.Write($"샘플 파일 저장 완료: {path}");
        SyncLogs();
    }

    private void ApplyJoinError(ErrorPacket error)
    {
        ConnectionState = "연결 실패";
        LastServerMessage = "세션 참가 요청이 거절되었습니다.";
        LastErrorMessage = $"{error.ErrorCode}: {error.Message}";
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
}
