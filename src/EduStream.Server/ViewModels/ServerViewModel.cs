using System.Collections.ObjectModel;
using System.IO;
using EduStream.Core.Common;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.Server.ViewModels;

/// <summary>
/// 교수용 화면에서 세션 개설과 데모용 상호작용 흐름을 다룹니다.
/// </summary>
public sealed class ServerViewModel : ObservableObject
{
    private readonly InMemoryLogSink _logSink = new();
    private readonly SessionManager _sessionManager;
    private readonly ScreenCapturer _screenCapturer;
    private readonly RdpHost _rdpHost;
    private readonly FileDistributor _fileDistributor;
    private string _sessionName = "캡스톤 실시간 강의";
    private int _port = 5000;
    private string _chatInput = "공지: 오늘 강의 자료를 업로드했습니다.";
    private string _latestScreenStatus = "화면 공유를 아직 시작하지 않았습니다.";
    private bool _isSessionOpen;

    public ServerViewModel()
    {
        var serializer = new PacketSerializer();
        _sessionManager = new SessionManager(_logSink);
        _screenCapturer = new ScreenCapturer();
        _rdpHost = new RdpHost(_logSink);
        _fileDistributor = new FileDistributor(serializer, _logSink);

        OpenSessionCommand = new RelayCommand(() => _ = OpenSessionAsync(), () => !IsSessionOpen);
        CloseSessionCommand = new RelayCommand(() => _ = CloseSessionAsync(), () => IsSessionOpen);
        StartScreenShareCommand = new RelayCommand(() => _ = StartScreenShareAsync(), () => IsSessionOpen);
        SendSampleFileCommand = new RelayCommand(() => _ = SendSampleFileAsync(), () => IsSessionOpen);
        SendChatCommand = new RelayCommand(() => _ = SendChatAsync(), () => IsSessionOpen && !string.IsNullOrWhiteSpace(ChatInput));
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
            }
        }
    }

    public ObservableCollection<string> ActivityLogs { get; } = [];

    public ObservableCollection<string> SharedFiles { get; } = [];

    public ObservableCollection<string> ChatMessages { get; } = [];

    public RelayCommand OpenSessionCommand { get; }

    public RelayCommand CloseSessionCommand { get; }

    public RelayCommand StartScreenShareCommand { get; }

    public RelayCommand SendSampleFileCommand { get; }

    public RelayCommand SendChatCommand { get; }

    private async Task OpenSessionAsync()
    {
        await _sessionManager.OpenSessionAsync(SessionName, Port);
        await _rdpHost.StartHostAsync();
        IsSessionOpen = true;
        SyncLogs();
    }

    private async Task CloseSessionAsync()
    {
        await _rdpHost.StopHostAsync();
        await _sessionManager.CloseSessionAsync();
        IsSessionOpen = false;
        LatestScreenStatus = "세션 종료 상태";
        SyncLogs();
    }

    private async Task StartScreenShareAsync()
    {
        var frame = _screenCapturer.CapturePreviewFrame();
        frame.DataLength = frame.Content.Length;
        await _sessionManager.BroadcastPacketAsync(frame);
        LatestScreenStatus = $"{frame.FrameDescription} 송신 준비 완료";
        SyncLogs();
    }

    private async Task SendSampleFileAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "edustream-sample-note.txt");
        await File.WriteAllTextAsync(tempFile, "EduStream 샘플 강의 자료");
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

        ChatMessages.Insert(0, $"교수: {ChatInput}");
        ChatInput = string.Empty;
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
