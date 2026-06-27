using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Forms.Integration;
using EduStream.Core.Common;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
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
    private readonly ScreenShareService _screenShareService;
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
    private string _selectedFilePath = string.Empty;
    private string _fileShareStatus = "아직 공유한 파일이 없습니다.";
    private bool _isSessionOpen;
    private bool _isBusy;
    private string _sessionStatus = "세션 대기 중";
    private string _statusMessage = "세션 이름과 포트를 설정한 뒤 세션을 열어 주세요.";
    private bool _isStatusError;
    private int _participantCount;
    private bool _isScreenSharing;

    public ServerViewModel(RdpHost? rdpHost = null)
    {
        var serializer = new PacketSerializer();
        _tcpServer = new TcpServerService(_logSink, serializer);
        _sessionManager = new SessionManager(_logSink, _tcpServer);
        _heartbeatService = new HeartbeatService(_sessionManager, _tcpServer, _logSink);
        _screenShareService = new ScreenShareService(_sessionManager, _logSink);
        _rdpHost = rdpHost ?? new RdpHost(_logSink);
        _fileDistributor = new FileDistributor(serializer, _logSink);

        _sessionManager.ParticipantsChanged += OnParticipantsChanged;
        _sessionManager.ChatReceived += OnChatReceived;
        _screenShareService.StatusChanged += OnScreenShareStatusChanged;

        OpenSessionCommand = new RelayCommand(() => _ = OpenSessionAsync(), () => !IsSessionOpen && !IsBusy);
        CloseSessionCommand = new RelayCommand(() => _ = CloseSessionAsync(), () => IsSessionOpen && !IsBusy);
        StartScreenShareCommand = new RelayCommand(() => _ = StartScreenShareAsync(), () => IsSessionOpen);
        StartAutoShareCommand = new RelayCommand(() => _ = StartAutoShareAsync(), () => IsSessionOpen && !IsScreenSharing);
        StopAutoShareCommand = new RelayCommand(() => _ = StopAutoShareAsync(), () => IsScreenSharing);
        SendSampleFileCommand = new RelayCommand(() => _ = SendSampleFileAsync(), () => IsSessionOpen);
        SelectFileCommand = new RelayCommand(SelectFile);
        SendSelectedFileCommand = new RelayCommand(() => _ = SendSelectedFileAsync(), () => IsSessionOpen && File.Exists(SelectedFilePath));
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
                SendSelectedFileCommand.RaiseCanExecuteChanged();
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

    public bool IsScreenSharing
    {
        get => _isScreenSharing;
        private set
        {
            if (SetProperty(ref _isScreenSharing, value))
            {
                StartAutoShareCommand.RaiseCanExecuteChanged();
                StopAutoShareCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<string> ActivityLogs { get; } = [];

    public ObservableCollection<string> SharedFiles { get; } = [];

    public ObservableCollection<ChatLine> ChatMessages { get; } = [];

    public RelayCommand OpenSessionCommand { get; }

    public RelayCommand CloseSessionCommand { get; }

    public RelayCommand StartScreenShareCommand { get; }

    public RelayCommand StartAutoShareCommand { get; }

    public RelayCommand StopAutoShareCommand { get; }

    public RelayCommand SendSampleFileCommand { get; }

    public RelayCommand SelectFileCommand { get; }

    public RelayCommand SendSelectedFileCommand { get; }

    public RelayCommand SendChatCommand { get; }

    public RelayCommand StartRdpPreviewCommand { get; }

    public RelayCommand StopRdpPreviewCommand { get; }

    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set
        {
            if (SetProperty(ref _selectedFilePath, value))
            {
                SendSelectedFileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FileShareStatus
    {
        get => _fileShareStatus;
        private set => SetProperty(ref _fileShareStatus, value);
    }

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
            await _screenShareService.StopContinuousBroadcastAsync();
            await _sessionManager.CloseSessionAsync();
            IsSessionOpen = false;
            IsScreenSharing = false;
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
        var frame = await _screenShareService.CaptureAndBroadcastPreviewAsync();
        LatestScreenStatus = $"{frame.FrameDescription} 전송 준비 완료";
        SyncLogs();
    }

    private async Task StartAutoShareAsync()
    {
        await _screenShareService.StartContinuousBroadcastAsync();
        IsScreenSharing = _screenShareService.IsStreaming;
        LatestScreenStatus = _screenShareService.LatestStatus;
        SyncLogs();
    }

    private async Task StopAutoShareAsync()
    {
        await _screenShareService.StopContinuousBroadcastAsync();
        IsScreenSharing = _screenShareService.IsStreaming;
        LatestScreenStatus = _screenShareService.LatestStatus;
        SyncLogs();
    }

    private async Task SendSampleFileAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "edustream-sample-note.txt");
        var sampleContent = string.Join(Environment.NewLine, Enumerable.Range(1, 260)
            .Select(index => $"EduStream sample lecture note line {index:D3}"));
        await File.WriteAllTextAsync(tempFile, sampleContent);

        await SendFileAsync(tempFile, "샘플 파일");
    }

    private void SelectFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "공유할 파일 선택",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedFilePath = dialog.FileName;
            FileShareStatus = $"선택됨: {Path.GetFileName(dialog.FileName)}";
            IsStatusError = false;
            SyncLogs();
        }
    }

    private async Task SendSelectedFileAsync()
    {
        if (!File.Exists(SelectedFilePath))
        {
            FileShareStatus = "전송할 파일을 먼저 선택해 주세요.";
            StatusMessage = FileShareStatus;
            IsStatusError = true;
            return;
        }

        await SendFileAsync(SelectedFilePath, "선택 파일");
    }

    private async Task SendFileAsync(string filePath, string label)
    {
        if (!IsSessionOpen)
        {
            FileShareStatus = "세션을 먼저 열어 주세요.";
            StatusMessage = FileShareStatus;
            IsStatusError = true;
            return;
        }

        try
        {
            var packets = await _fileDistributor.BuildFilePacketsAsync(
                filePath,
                senderId: "Server",
                sessionId: _sessionManager.CurrentSession?.SessionId,
                chunkSize: FileTransferRules.MinChunkSize);

            FileShareStatus = $"{label} 전송 중: {Path.GetFileName(filePath)} / {packets.Count} chunks";
            IsStatusError = false;
            SyncLogs();

            foreach (var packet in packets)
            {
                await _sessionManager.BroadcastPacketAsync(packet);
            }

            var firstPacket = packets[0];
            FileShareStatus = $"{label} 전송 완료: {firstPacket.FileName} / {packets.Count} chunks / {firstPacket.FileSize} byte";
            StatusMessage = FileShareStatus;
            SharedFiles.Insert(0, $"{firstPacket.FileName} ({firstPacket.FileSize} byte, {packets.Count} chunks)");
            _logSink.Write($"파일 전송 완료: {firstPacket.FileName}, chunks={packets.Count}, checksum={firstPacket.Checksum[..Math.Min(12, firstPacket.Checksum.Length)]}...");
            SyncLogs();
        }
        catch (Exception ex)
        {
            FileShareStatus = $"{label} 전송 실패: {ex.Message}";
            StatusMessage = FileShareStatus;
            IsStatusError = true;
            _logSink.Write(FileShareStatus);
            SyncLogs();
        }
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

    private void OnChatReceived(string sender, string message)
    {
        // ChatReceived는 TCP 수신 스레드에서 발생하므로 UI 스레드로 마샬링해야
        // ObservableCollection 바인딩이 깨지지 않습니다.
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var line = string.Equals(sender, "System", StringComparison.Ordinal)
                ? ChatLine.System(message)
                : ChatLine.User(sender, message, isSelf: false);
            ChatMessages.Insert(0, line);
            SyncLogs();
        });
    }

    private void OnScreenShareStatusChanged()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            IsScreenSharing = _screenShareService.IsStreaming;
            LatestScreenStatus = _screenShareService.LatestStatus;
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
