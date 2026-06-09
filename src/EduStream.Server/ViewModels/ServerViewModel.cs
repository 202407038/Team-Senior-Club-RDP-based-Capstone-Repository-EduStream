using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Forms.Integration;
using EduStream.Core.Common;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.Server.ViewModels;

/// <summary>
/// 교수자 대시보드의 상태와 명령을 관리합니다.
/// RDP 테스트 대시보드와 세션 네트워크 흐름을 함께 연결합니다.
/// </summary>
public sealed class ServerViewModel : ObservableObject
{
    private readonly InMemoryLogSink _logSink = new();
    private readonly SessionManager _sessionManager;
    private readonly TcpServerService _tcpServer;
    private readonly HeartbeatService _heartbeatService;
    private readonly ScreenCapturer _screenCapturer;
    private readonly RdpHost _rdpHost;
    private readonly FileDistributor _fileDistributor;
    private string _sessionName = "Capstone Live Class";
    private int _port = 5000;
    private string _chatInput = "Announcement: today's lecture note has been uploaded.";
    private string _latestScreenStatus = "Screen sharing has not started yet.";
    private string _rdpServerAddress = "127.0.0.1";
    private string _rdpUserName = Environment.UserName;
    private string _rdpPassword = string.Empty;
    private string _rdpStatus = "RDP preview is idle.";
    private bool _isSessionOpen;
    private bool _isBusy;
    private string _sessionStatus = "세션 대기 중";
    private string _statusMessage = "세션 이름과 포트를 설정한 뒤 세션을 열어 주세요.";
    private bool _isStatusError;
    private int _participantCount;

    public ServerViewModel(RdpHost? rdpHost = null)
    {
        var serializer = new PacketSerializer();
        _tcpServer = new TcpServerService(serializer, _logSink);
        _sessionManager = new SessionManager(_tcpServer, _logSink);
        _heartbeatService = new HeartbeatService(_sessionManager, _logSink);
        _screenCapturer = new ScreenCapturer();
        _rdpHost = rdpHost ?? new RdpHost(_logSink);
        _fileDistributor = new FileDistributor(serializer, _logSink);

        _sessionManager.ParticipantsChanged += OnParticipantsChanged;

        OpenSessionCommand = new RelayCommand(() => _ = OpenSessionAsync(), () => !IsSessionOpen && !IsBusy);
        CloseSessionCommand = new RelayCommand(() => _ = CloseSessionAsync(), () => IsSessionOpen && !IsBusy);
        StartScreenShareCommand = new RelayCommand(() => _ = StartScreenShareAsync(), () => IsSessionOpen);
        SendSampleFileCommand = new RelayCommand(() => _ = SendSampleFileAsync(), () => IsSessionOpen);
        SendChatCommand = new RelayCommand(() => _ = SendChatAsync(), () => IsSessionOpen && !string.IsNullOrWhiteSpace(ChatInput));
        StartRdpPreviewCommand = new RelayCommand(() => _ = StartRdpPreviewAsync(), () => IsSessionOpen && _rdpHost.IsAttached);
        StopRdpPreviewCommand = new RelayCommand(() => _ = StopRdpPreviewAsync(), () => _rdpHost.IsAttached);
    }

    public string SessionName
    {
        get => _sessionName;
        set => SetProperty(ref _sessionName, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
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

    public string LatestScreenStatus
    {
        get => _latestScreenStatus;
        private set => SetProperty(ref _latestScreenStatus, value);
    }

    public string RdpServerAddress
    {
        get => _rdpServerAddress;
        set => SetProperty(ref _rdpServerAddress, value);
    }

    public string RdpUserName
    {
        get => _rdpUserName;
        set => SetProperty(ref _rdpUserName, value);
    }

    public string RdpPassword
    {
        get => _rdpPassword;
        set => SetProperty(ref _rdpPassword, value);
    }

    public string RdpStatus
    {
        get => _rdpStatus;
        private set => SetProperty(ref _rdpStatus, value);
    }

    public bool IsSessionOpen
    {
        get => _isSessionOpen;
        private set
        {
            if (SetProperty(ref _isSessionOpen, value))
            {
                OpenSessionCommand.RaiseCanExecuteChanged();
                CloseSessionCommand.RaiseCanExecuteChanged();
                StartScreenShareCommand.RaiseCanExecuteChanged();
                SendSampleFileCommand.RaiseCanExecuteChanged();
                SendChatCommand.RaiseCanExecuteChanged();
                StartRdpPreviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OpenSessionCommand.RaiseCanExecuteChanged();
                CloseSessionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SessionStatus
    {
        get => _sessionStatus;
        private set => SetProperty(ref _sessionStatus, value);
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

    public int ParticipantCount
    {
        get => _participantCount;
        private set => SetProperty(ref _participantCount, value);
    }

    public ObservableCollection<string> ActivityLogs { get; } = [];

    public ObservableCollection<string> SharedFiles { get; } = [];

    public ObservableCollection<ChatLine> ChatMessages { get; } = [];

    public RelayCommand OpenSessionCommand { get; }

    public RelayCommand CloseSessionCommand { get; }

    public RelayCommand StartScreenShareCommand { get; }

    public RelayCommand SendSampleFileCommand { get; }

    public RelayCommand SendChatCommand { get; }

    public RelayCommand StartRdpPreviewCommand { get; }

    public RelayCommand StopRdpPreviewCommand { get; }

    public void AttachRdpSurface(WindowsFormsHost hostSurface)
    {
        _rdpHost.AttachHost(hostSurface);
        StartRdpPreviewCommand.RaiseCanExecuteChanged();
        StopRdpPreviewCommand.RaiseCanExecuteChanged();
        SyncLogs();
    }

    private async Task OpenSessionAsync()
    {
        IsBusy = true;
        SessionStatus = "세션 여는 중...";

        try
        {
            await _sessionManager.OpenSessionAsync(SessionName, Port);
            _heartbeatService.Start();
            IsSessionOpen = true;
            SessionStatus = $"세션 Open · 포트 {Port}";
            StatusMessage = $"'{SessionName}' 세션이 시작되었습니다.";
            IsStatusError = false;
            RdpStatus = _rdpHost.IsAttached
                ? "RDP 미리보기 준비됨. 자격 증명을 입력하고 연결을 시작하세요."
                : "RDP 미리보기 호스트가 아직 연결되지 않았습니다.";
            ChatMessages.Insert(0, ChatLine.System("세션이 열렸습니다."));
            SyncLogs();
        }
        catch (Exception ex)
        {
            SessionStatus = "세션 Open 실패";
            StatusMessage = ex.Message;
            IsStatusError = true;
            SyncLogs();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CloseSessionAsync()
    {
        IsBusy = true;
        SessionStatus = "세션 닫는 중...";

        try
        {
            _heartbeatService.Stop();
            await _rdpHost.StopHostAsync();
            await _sessionManager.CloseSessionAsync();
            IsSessionOpen = false;
            ParticipantCount = 0;
            SessionStatus = "세션 닫힘";
            LatestScreenStatus = "화면 공유가 중지되었습니다.";
            RdpStatus = "RDP 미리보기가 중지되었습니다.";
            StatusMessage = "세션이 종료되었습니다.";
            IsStatusError = false;
            ChatMessages.Insert(0, ChatLine.System("세션이 닫혔습니다."));
            SyncLogs();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartScreenShareAsync()
    {
        var frame = _screenCapturer.CapturePreviewFrame();
        frame.DataLength = frame.Content.Length;
        await _sessionManager.BroadcastPacketAsync(frame);
        LatestScreenStatus = $"{frame.FrameDescription} 전송 준비 완료";
        SyncLogs();
    }

    private async Task SendSampleFileAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "edustream-sample-note.txt");
        await File.WriteAllTextAsync(tempFile, "EduStream sample lecture note");
        var packet = await _fileDistributor.BuildFilePacketAsync(tempFile);
        await _sessionManager.BroadcastPacketAsync(packet);

        SharedFiles.Insert(0, $"{packet.FileName} ({packet.FileSize} byte)");
        SyncLogs();
    }

    private async Task SendChatAsync()
    {
        var packet = new ChatPacket
        {
            Sender = "Professor",
            Message = ChatInput
        };

        packet.DataLength = ChatInput.Length;
        await _sessionManager.BroadcastPacketAsync(packet);

        ChatMessages.Insert(0, ChatLine.User("교수자", ChatInput, isSelf: true));
        ChatInput = string.Empty;
        SyncLogs();
    }

    private async Task StartRdpPreviewAsync()
    {
        try
        {
            await _rdpHost.StartHostAsync(RdpServerAddress, RdpUserName, RdpPassword);
            RdpStatus = $"RDP 연결 시작: {RdpServerAddress}";
        }
        catch (Exception ex)
        {
            RdpStatus = $"RDP 시작 실패: {ex.Message}";
            _logSink.Write(RdpStatus);
        }

        SyncLogs();
    }

    private async Task StopRdpPreviewAsync()
    {
        await _rdpHost.StopHostAsync();
        RdpStatus = "RDP 미리보기가 중지되었습니다.";
        SyncLogs();
    }

    private void OnParticipantsChanged()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ParticipantCount = _sessionManager.ParticipantCount;
            if (IsSessionOpen)
            {
                SessionStatus = $"세션 Open · 참가자 {ParticipantCount}명";
            }

            SyncLogs();
        });
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
