using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Media;
using EduStream.Client.Services;
using EduStream.Core.Common;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Utils;

namespace EduStream.Client.ViewModels;

/// <summary>
/// 수강생 대시보드 상태와 명령을 관리합니다.
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
    private string _connectionState = "연결 대기 중";
    private string _renderStatus = "아직 수신된 화면 프레임이 없습니다.";
    private string _downloadStatus = "다운로드 대기 중";
    private string _statusMessage = "서버 주소를 입력한 뒤 세션에 참여하세요.";
    private string _chatInput = string.Empty;
    private bool _isConnected;
    private bool _isStatusError;
    private bool _hasRemoteFrame;
    private ImageSource? _displaySource;
    private int _frameIndex;

    public ClientViewModel()
    {
        _sessionClient = new SessionClient(_logSink);
        _screenRenderer = new ScreenRenderer();
        _fileReceiver = new FileReceiver();

        JoinSessionCommand = new RelayCommand(() => _ = JoinSessionAsync(), CanJoinSession);
        DisconnectCommand = new RelayCommand(() => _ = DisconnectAsync(), () => IsConnected);
        SendChatCommand = new RelayCommand(SendChat, () => IsConnected && !string.IsNullOrWhiteSpace(ChatInput));
        SimulateFileReceiveCommand = new RelayCommand(() => _ = SimulateFileReceiveAsync(), () => IsConnected);
        SimulateScreenRenderCommand = new RelayCommand(SimulateScreenRender, () => IsConnected);
    }

    public string HostAddress
    {
        get => _hostAddress;
        set
        {
            if (SetProperty(ref _hostAddress, value))
            {
                JoinSessionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
            {
                JoinSessionCommand.RaiseCanExecuteChanged();
            }
        }
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

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsStatusError
    {
        get => _isStatusError;
        private set => SetProperty(ref _isStatusError, value);
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

    public bool HasRemoteFrame
    {
        get => _hasRemoteFrame;
        private set => SetProperty(ref _hasRemoteFrame, value);
    }

    public ImageSource? DisplaySource
    {
        get => _displaySource;
        private set => SetProperty(ref _displaySource, value);
    }

    public string PlaceholderTitle => IsConnected ? "화면 수신 대기 중" : "연결되지 않음";

    public string PlaceholderSubtitle =>
        IsConnected
            ? "교수자가 화면 공유를 시작하면 이 영역에 표시됩니다."
            : "호스트 주소와 포트를 입력한 뒤 세션에 참여하세요.";

    public ObservableCollection<string> ActivityLogs { get; } = [];

    public ObservableCollection<string> ChatMessages { get; } = [];

    public ObservableCollection<string> DownloadedFiles { get; } = [];

    public RelayCommand JoinSessionCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand SendChatCommand { get; }

    public RelayCommand SimulateFileReceiveCommand { get; }

    public RelayCommand SimulateScreenRenderCommand { get; }

    private bool CanJoinSession()
    {
        return !IsConnected
            && !string.IsNullOrWhiteSpace(HostAddress)
            && !string.IsNullOrWhiteSpace(DisplayName);
    }

    private async Task JoinSessionAsync()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ApplyError(ErrorCodes.DisplayNameRequired, "표시 이름을 입력해 주세요.");
            return;
        }

        var joinRequest = _sessionClient.CreateJoinRequest(HostAddress, Port, DisplayName);
        var ack = new AckPacket
        {
            SessionId = Guid.NewGuid(),
            SenderId = "Server",
            AckCode = AckCodes.SessionJoined,
            Message = $"{DisplayName}님, 세션 참여가 승인되었습니다."
        };

        await _sessionClient.ApplyJoinAckAsync(ack, HostAddress, Port);
        IsConnected = true;
        ConnectionState = $"연결됨 · {HostAddress}:{Port}";
        ApplyAck(ack);
        ChatMessages.Insert(0, "시스템: 세션에 참여했습니다.");
        _logSink.Write($"세션 참여 요청: {joinRequest.DisplayName} -> {joinRequest.TargetAddress}:{joinRequest.TargetPort}");
        NotifyPlaceholderChanged();
        SyncLogs();
    }

    private async Task DisconnectAsync()
    {
        var leavePacket = _sessionClient.CreateLeaveRequest(DisplayName, "사용자가 연결을 종료했습니다.");
        _logSink.Write($"세션 종료 요청: {leavePacket.SenderId}, reason={leavePacket.Reason}");
        await _sessionClient.DisconnectAsync(leavePacket.Reason);

        IsConnected = false;
        ConnectionState = "연결 해제됨";
        RenderStatus = "화면 수신이 중지되었습니다.";
        StatusMessage = "세션에서 나갔습니다.";
        IsStatusError = false;
        HasRemoteFrame = false;
        DisplaySource = null;
        ChatMessages.Insert(0, "시스템: 세션 연결이 종료되었습니다.");
        NotifyPlaceholderChanged();
        SyncLogs();
    }

    private void SendChat()
    {
        var message = ChatInput.Trim();
        ChatMessages.Insert(0, $"{DisplayName}: {message}");
        _logSink.Write($"채팅 전송: {message}");
        ChatInput = string.Empty;
        SyncLogs();
    }

    private void SimulateScreenRender()
    {
        _frameIndex++;
        var packet = new ScreenPacket
        {
            FrameIndex = _frameIndex,
            FrameDescription = "교수자 샘플 강의 화면"
        };

        ApplyScreenPacket(packet, isDemo: true);
        _logSink.Write("샘플 화면 프레임을 렌더링했습니다.");
        SyncLogs();
    }

    public void ApplyScreenPacket(ScreenPacket packet, bool isDemo = false)
    {
        if (isDemo && packet.ContentLength == 0)
        {
            DisplaySource = _screenRenderer.CreateDemoFrame(packet.FrameIndex, packet.FrameDescription);
            HasRemoteFrame = true;
            RenderStatus = $"프레임 #{packet.FrameIndex} 수신 (960x540, 샘플)";
            return;
        }

        var image = _screenRenderer.TryCreateDisplayImage(packet);
        if (image is not null)
        {
            DisplaySource = image;
            HasRemoteFrame = true;
        }

        RenderStatus = _screenRenderer.Render(packet);
    }

    public void ApplyAck(AckPacket packet)
    {
        StatusMessage = string.IsNullOrWhiteSpace(packet.Message)
            ? $"Ack: {packet.AckCode}"
            : packet.Message;
        IsStatusError = false;
        _logSink.Write($"Ack 수신: {packet.AckCode} — {StatusMessage}");
    }

    public void ApplyError(string errorCode, string message)
    {
        StatusMessage = message;
        IsStatusError = true;
        ConnectionState = "연결 실패";
        _logSink.Write($"Error 수신: {errorCode} — {message}");
        SyncLogs();
    }

    private async Task SimulateFileReceiveAsync()
    {
        var content = Encoding.UTF8.GetBytes("EduStream sample file content");
        var packet = new FilePacket
        {
            FileName = "lecture-note-sample.txt",
            FileSize = content.LongLength,
            Content = content,
            Checksum = ChecksumUtility.ComputeSha256(content)
        };

        var path = await _fileReceiver.SaveAsync(packet, Path.Combine(Path.GetTempPath(), "EduStreamClient"));
        var fileName = Path.GetFileName(path);
        DownloadedFiles.Insert(0, fileName);
        DownloadStatus = $"{fileName} 저장 완료";
        StatusMessage = $"파일이 저장되었습니다: {path}";
        IsStatusError = false;
        _logSink.Write($"파일 저장 완료: {path}");
        SyncLogs();
    }

    private void NotifyPlaceholderChanged()
    {
        OnPropertyChanged(nameof(PlaceholderTitle));
        OnPropertyChanged(nameof(PlaceholderSubtitle));
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
