using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Forms.Integration;
using EduStream.Core.Common;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.Server.ViewModels;

/// <summary>
/// Coordinates the professor-facing dashboard and the demo services behind it.
/// </summary>
public sealed class ServerViewModel : ObservableObject
{
    private readonly InMemoryLogSink _logSink = new();
    private readonly TcpServerService _tcpServer;
    private readonly SessionManager _sessionManager;
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
    private bool _isSessionOpen;
    private int _participantCount;
    private bool _isScreenShareActive;
    private int _screenFrameCount;
    private string _screenLoopStatus = "자동 송신 중지";

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

        OpenSessionCommand = new RelayCommand(() => _ = OpenSessionAsync(), () => !IsSessionOpen);
        CloseSessionCommand = new RelayCommand(() => _ = CloseSessionAsync(), () => IsSessionOpen);
        StartScreenShareCommand = new RelayCommand(() => _ = StartScreenShareAsync(), () => IsSessionOpen);
        StartAutoScreenShareCommand = new RelayCommand(() => _ = StartAutoScreenShareAsync(), () => IsSessionOpen && !IsScreenShareActive);
        StopAutoScreenShareCommand = new RelayCommand(() => _ = StopAutoScreenShareAsync(), () => IsScreenShareActive);
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

    public int ParticipantCount
    {
        get => _participantCount;
        private set => SetProperty(ref _participantCount, value);
    }

    public bool IsScreenShareActive
    {
        get => _isScreenShareActive;
        private set
        {
            if (SetProperty(ref _isScreenShareActive, value))
            {
                StartAutoScreenShareCommand.RaiseCanExecuteChanged();
                StopAutoScreenShareCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int ScreenFrameCount
    {
        get => _screenFrameCount;
        private set => SetProperty(ref _screenFrameCount, value);
    }

    public string ScreenLoopStatus
    {
        get => _screenLoopStatus;
        private set => SetProperty(ref _screenLoopStatus, value);
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
                StartAutoScreenShareCommand.RaiseCanExecuteChanged();
                StopAutoScreenShareCommand.RaiseCanExecuteChanged();
                SendSampleFileCommand.RaiseCanExecuteChanged();
                SendChatCommand.RaiseCanExecuteChanged();
                StartRdpPreviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<string> ActivityLogs { get; } = [];

    public ObservableCollection<string> SharedFiles { get; } = [];

    public ObservableCollection<string> ChatMessages { get; } = [];

    public RelayCommand OpenSessionCommand { get; }

    public RelayCommand CloseSessionCommand { get; }

    public RelayCommand StartScreenShareCommand { get; }

    public RelayCommand StartAutoScreenShareCommand { get; }

    public RelayCommand StopAutoScreenShareCommand { get; }

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

    private void OnChatReceived(string sender, string message)
    {
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            ChatMessages.Insert(0, $"{sender}: {message}");
            SyncLogs();
        });
    }

    private void OnParticipantsChanged()
    {
        // UI 스레드에서 실행되도록 Dispatcher를 통해 갱신
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            ParticipantCount = _sessionManager.ParticipantNames.Count;
            SyncLogs();
        });
    }

    private async Task OpenSessionAsync()
    {
        await _sessionManager.OpenSessionAsync(SessionName, Port);
        _heartbeatService.Start();
        IsSessionOpen = true;
        ScreenLoopStatus = "자동 송신 중지";
        RdpStatus = _rdpHost.IsAttached
            ? "RDP preview is ready. Enter credentials and start the connection."
            : "RDP preview host is not attached yet.";
        SyncLogs();
    }

    private async Task CloseSessionAsync()
    {
        await _screenShareService.StopContinuousBroadcastAsync();
        _heartbeatService.Stop();
        await _rdpHost.StopHostAsync();
        await _sessionManager.CloseSessionAsync();
        IsSessionOpen = false;
        ParticipantCount = 0;
        IsScreenShareActive = false;
        ScreenFrameCount = 0;
        LatestScreenStatus = "Session closed.";
        ScreenLoopStatus = "자동 송신 중지";
        RdpStatus = "RDP preview stopped.";
        SyncLogs();
    }

    private async Task StartScreenShareAsync()
    {
        var frame = await _screenShareService.CaptureAndBroadcastPreviewAsync();
        LatestScreenStatus = _screenShareService.BuildLatestStatus(frame);
        SyncLogs();
    }

    private async Task StartAutoScreenShareAsync()
    {
        await _screenShareService.StartContinuousBroadcastAsync();
        IsScreenShareActive = _screenShareService.IsStreaming;
        ScreenLoopStatus = _screenShareService.LatestStatus;
        SyncLogs();
    }

    private async Task StopAutoScreenShareAsync()
    {
        await _screenShareService.StopContinuousBroadcastAsync();
        IsScreenShareActive = _screenShareService.IsStreaming;
        ScreenLoopStatus = _screenShareService.LatestStatus;
        SyncLogs();
    }

    private async Task SendSampleFileAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "edustream-sample-note.txt");
        var sampleContent = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 2400).Select(index => $"Lecture sample line {index:D4}: chunked transfer verification payload."));
        await File.WriteAllTextAsync(tempFile, sampleContent);

        var packets = await _fileDistributor.BuildFilePacketsAsync(
            tempFile,
            senderId: "Server",
            sessionId: _sessionManager.CurrentSession?.SessionId,
            chunkSize: FileTransferRules.MinChunkSize);

        foreach (var packet in packets)
        {
            await _sessionManager.BroadcastPacketAsync(packet);
        }

        var firstPacket = packets[0];
        SharedFiles.Insert(0, $"{firstPacket.FileName} ({firstPacket.FileSize} byte, {packets.Count} chunks)");
        _logSink.Write($"샘플 파일 청크 전송 완료: {firstPacket.FileName}, 청크 수={packets.Count}");
        SyncLogs();
    }

    private async Task SendChatAsync()
    {
        var packet = PacketFactory.CreateChat(
            senderId: "Server",
            sender: "Professor",
            message: ChatInput,
            sessionId: _sessionManager.CurrentSession?.SessionId);
        await _sessionManager.BroadcastPacketAsync(packet);

        ChatMessages.Insert(0, $"Professor: {ChatInput}");
        ChatInput = string.Empty;
        SyncLogs();
    }

    private async Task StartRdpPreviewAsync()
    {
        try
        {
            await _rdpHost.StartHostAsync(RdpServerAddress, RdpUserName, RdpPassword);
            RdpStatus = $"RDP connection started for {RdpServerAddress}.";
        }
        catch (Exception ex)
        {
            RdpStatus = $"RDP start failed: {ex.Message}";
            _logSink.Write(RdpStatus);
        }

        SyncLogs();
    }

    private async Task StopRdpPreviewAsync()
    {
        await _rdpHost.StopHostAsync();
        RdpStatus = "RDP preview stopped.";
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

    private void OnScreenShareStatusChanged()
    {
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            IsScreenShareActive = _screenShareService.IsStreaming;
            ScreenFrameCount = _screenShareService.BroadcastFrameCount;
            ScreenLoopStatus = _screenShareService.LatestStatus;
            LatestScreenStatus = _screenShareService.LatestStatus;
            SyncLogs();
        });
    }
}
