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
/// 학생용 화면에서 세션 참여와 수신 상태를 표현합니다.
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
    private string _connectionState = "연결 대기";
    private string _renderStatus = "화면을 아직 수신하지 않았습니다.";
    private string _downloadStatus = "다운로드 대기";
    private string _chatInput = "학생 질문: 오늘 과제 제출 기한이 언제인가요?";
    private bool _isConnected;

    public ClientViewModel()
    {
        _sessionClient = new SessionClient(_logSink);
        _screenRenderer = new ScreenRenderer();
        _fileReceiver = new FileReceiver();

        JoinSessionCommand = new RelayCommand(() => _ = JoinSessionAsync(), () => !IsConnected);
        DisconnectCommand = new RelayCommand(() => _ = DisconnectAsync(), () => IsConnected);
        SendChatCommand = new RelayCommand(SendChat, () => IsConnected && !string.IsNullOrWhiteSpace(ChatInput));
        SimulateFileReceiveCommand = new RelayCommand(() => _ = SimulateFileReceiveAsync(), () => IsConnected);
        SimulateScreenRenderCommand = new RelayCommand(SimulateScreenRender, () => IsConnected);
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
                SimulateFileReceiveCommand.RaiseCanExecuteChanged();
                SimulateScreenRenderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<string> ActivityLogs { get; } = [];

    public ObservableCollection<string> ChatMessages { get; } = [];

    public ObservableCollection<string> DownloadedFiles { get; } = [];

    public RelayCommand JoinSessionCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand SendChatCommand { get; }

    public RelayCommand SimulateFileReceiveCommand { get; }

    public RelayCommand SimulateScreenRenderCommand { get; }

    private async Task JoinSessionAsync()
    {
        var joinRequest = _sessionClient.CreateJoinRequest(HostAddress, Port, DisplayName);
        var ack = new AckPacket
        {
            SessionId = Guid.NewGuid(),
            SenderId = "Server",
            AckCode = AckCodes.SessionJoined,
            Message = $"{DisplayName}님 연결 승인"
        };

        await _sessionClient.ApplyJoinAckAsync(ack, HostAddress, Port);
        IsConnected = true;
        ConnectionState = "연결됨";
        _logSink.Write($"세션 참여 요청 생성: {joinRequest.DisplayName} -> {joinRequest.TargetAddress}:{joinRequest.TargetPort}");
        SyncLogs();
    }

    private async Task DisconnectAsync()
    {
        var leavePacket = _sessionClient.CreateLeaveRequest(DisplayName, "사용자 종료");
        _logSink.Write($"세션 이탈 요청 생성: {leavePacket.SenderId}, 사유={leavePacket.Reason}");
        await _sessionClient.DisconnectAsync(leavePacket.Reason);
        IsConnected = false;
        ConnectionState = "연결 종료";
        RenderStatus = "화면 수신 중지";
        SyncLogs();
    }

    private void SendChat()
    {
        ChatMessages.Insert(0, $"학생: {ChatInput}");
        _logSink.Write($"채팅 메시지를 전송했습니다: {ChatInput}");
        ChatInput = string.Empty;
        SyncLogs();
    }

    private void SimulateScreenRender()
    {
        var packet = new ScreenPacket
        {
            FrameIndex = 1,
            FrameDescription = "교수 화면 샘플 프레임"
        };

        RenderStatus = _screenRenderer.Render(packet);
        _logSink.Write("샘플 화면 프레임을 렌더링했습니다.");
        SyncLogs();
    }

    private async Task SimulateFileReceiveAsync()
    {
        var content = Encoding.UTF8.GetBytes("EduStream 수신 샘플 파일");
        var packet = new FilePacket
        {
            FileName = "received-sample.txt",
            FileSize = content.LongLength,
            Content = content,
            Checksum = ChecksumUtility.ComputeSha256(content)
        };

        var path = await _fileReceiver.SaveAsync(packet, Path.Combine(Path.GetTempPath(), "EduStreamClient"));
        DownloadedFiles.Insert(0, Path.GetFileName(path));
        DownloadStatus = $"{Path.GetFileName(path)} 저장 완료";
        _logSink.Write($"샘플 파일을 저장했습니다: {path}");
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
