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
/// Presents the student dashboard state and wires it to the demo services.
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
    private string _connectionState = "Waiting for connection";
    private string _renderStatus = "No screen frame has been received yet.";
    private string _downloadStatus = "Waiting for download";
    private string _chatInput = "Student question: when is the assignment due?";
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
            Message = $"{DisplayName} connection acknowledged"
        };

        await _sessionClient.ApplyJoinAckAsync(ack, HostAddress, Port);
        IsConnected = true;
        ConnectionState = "Connected";
        _logSink.Write($"Session join request created: {joinRequest.DisplayName} -> {joinRequest.TargetAddress}:{joinRequest.TargetPort}");
        SyncLogs();
    }

    private async Task DisconnectAsync()
    {
        var leavePacket = _sessionClient.CreateLeaveRequest(DisplayName, "User requested disconnect");
        _logSink.Write($"Session leave request created: {leavePacket.SenderId}, reason={leavePacket.Reason}");
        await _sessionClient.DisconnectAsync(leavePacket.Reason);
        IsConnected = false;
        ConnectionState = "Disconnected";
        RenderStatus = "Screen rendering stopped";
        SyncLogs();
    }

    private void SendChat()
    {
        ChatMessages.Insert(0, $"Student: {ChatInput}");
        _logSink.Write($"Chat message sent: {ChatInput}");
        ChatInput = string.Empty;
        SyncLogs();
    }

    private void SimulateScreenRender()
    {
        var packet = new ScreenPacket
        {
            FrameIndex = 1,
            FrameDescription = "Professor sample preview frame"
        };

        RenderStatus = _screenRenderer.Render(packet);
        _logSink.Write("Rendered a sample screen frame.");
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

        var path = await _fileReceiver.SaveAsync(packet, Path.Combine(Path.GetTempPath(), "EduStreamClient"));
        DownloadedFiles.Insert(0, Path.GetFileName(path));
        DownloadStatus = $"{Path.GetFileName(path)} saved successfully";
        _logSink.Write($"Saved sample file to {path}");
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
