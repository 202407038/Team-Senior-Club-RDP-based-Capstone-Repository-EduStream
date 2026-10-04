using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using EduStream.Core.Network;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Core.Common;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.Server.ViewModels;

/// <summary>
/// 교수자 대시보드의 상태와 명령을 관리합니다.
/// WDS 화면 공유와 세션 네트워크 흐름을 함께 연결합니다.
/// </summary>
public sealed class ServerViewModel : ObservableObject
{
    private readonly InMemoryLogSink _logSink = new();
    private readonly SessionManager _sessionManager;
    private readonly TcpServerService _tcpServer;
    private readonly HeartbeatService _heartbeatService;
    private readonly ScreenShareService _screenShareService;
    private readonly IRdpSharingService _rdpSharing;
    private bool _isRdpSharing;
    private bool _isRdpBusy;
    private bool _shuttingDown;
    private readonly SemaphoreSlim _rdpLifecycle = new(1, 1);
    private readonly FileDistributor _fileDistributor;
    private string _sessionName = "Capstone Live Class";
    private int _port = 5000;
    private string _chatInput = "Announcement: today's lecture note has been uploaded.";
    private string _latestScreenStatus = "Screen sharing has not started yet.";
    private string _rdpStatus = "WDS 화면 공유 대기 중";
    private string _selectedFilePath = string.Empty;
    private string _fileShareStatus = "아직 공유한 파일이 없습니다.";
    private bool _isSessionOpen;
    private bool _isBusy;
    private string _sessionStatus = "세션 대기 중";
    private string _statusMessage = "세션 이름과 포트를 설정한 뒤 세션을 열어 주세요.";
    private bool _isStatusError;
    private int _participantCount;
    private bool _isScreenSharing;
    private X509Certificate2? _secureCertificate;
    private readonly Func<X509Certificate2>? _certificateProvider;
    private string _connectionCode = "세션을 열면 표시됩니다.";

    /// <param name="certificateProvider">테스트용. 지정하지 않으면 사용자 인증서 저장소의 교수자 인증서를 씁니다.</param>
    public ServerViewModel(IRdpSharingService? rdpSharing = null, Func<X509Certificate2>? certificateProvider = null)
    {
        _certificateProvider = certificateProvider;
        var serializer = new PacketSerializer();
        _tcpServer = new TcpServerService(_logSink, serializer);
        _sessionManager = new SessionManager(_logSink, _tcpServer);
        _heartbeatService = new HeartbeatService(_sessionManager, _tcpServer, _logSink);
        _screenShareService = new ScreenShareService(_sessionManager, _logSink);
        _rdpSharing = rdpSharing ?? new RdpSharingService(_logSink);
        _sessionManager.RdpInvitationPasswordReady += OnInvitationReady;
        _sessionManager.RdpInvitationPasswordWithdrawn += OnInvitationWithdrawn;
        _fileDistributor = new FileDistributor(serializer, _logSink);

        _sessionManager.ParticipantsChanged += OnParticipantsChanged;
        _sessionManager.ChatReceived += OnChatReceived;
        _screenShareService.StatusChanged += OnScreenShareStatusChanged;

        OpenSessionCommand = new RelayCommand(() => _ = OpenSessionAsync(), () => !IsSessionOpen && !IsBusy);
        CloseSessionCommand = new RelayCommand(() => _ = CloseSessionAsync(), () => IsSessionOpen && !IsBusy && !IsRdpBusy);
        StartScreenShareCommand = new RelayCommand(() => _ = StartScreenShareAsync(), () => IsSessionOpen && !IsRdpSharing && !IsRdpBusy);
        StartAutoShareCommand = new RelayCommand(() => _ = StartAutoShareAsync(), () => IsSessionOpen && !IsScreenSharing && !IsRdpSharing && !IsRdpBusy);
        StopAutoShareCommand = new RelayCommand(() => _ = StopAutoShareAsync(), () => IsScreenSharing);
        SendSampleFileCommand = new RelayCommand(() => _ = SendSampleFileAsync(), () => IsSessionOpen);
        SelectFileCommand = new RelayCommand(SelectFile);
        SendSelectedFileCommand = new RelayCommand(() => _ = SendSelectedFileAsync(), () => IsSessionOpen && File.Exists(SelectedFilePath));
        RegisterSelectedFileCommand = new RelayCommand(() => _ = RegisterSelectedFileAsync(), () => IsSessionOpen && File.Exists(SelectedFilePath));
        SendChatCommand = new RelayCommand(() => _ = SendChatAsync(), () => IsSessionOpen && !string.IsNullOrWhiteSpace(ChatInput));
        StartRdpShareCommand = new RelayCommand(() => _ = StartRdpShareAsync(), () => IsSessionOpen && !IsBusy && !IsRdpBusy && !IsRdpSharing);
        StopRdpShareCommand = new RelayCommand(() => _ = StopRdpShareAsync(), () => IsRdpSharing && !IsBusy && !IsRdpBusy);
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

    /// <summary>
    /// 학생이 참가할 때 입력하는 접속 코드(교수자 인증서 지문)입니다. 비밀값이 아니므로 화면에 표시합니다.
    /// </summary>
    public string ConnectionCode
    {
        get => _connectionCode;
        private set => SetProperty(ref _connectionCode, value);
    }

    /// <summary>
    /// 방 비밀번호 입력칸을 읽고 비우는 함수입니다. 비밀번호를 ViewModel 속성에 보관하지 않기 위해 View가 제공합니다.
    /// </summary>
    public Func<string>? RoomPasswordProvider { get; set; }

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

    public bool IsRdpSharing
    {
        get => _isRdpSharing;
        private set { SetProperty(ref _isRdpSharing, value); UpdateRdpCommands(); }
    }

    public bool IsRdpBusy
    {
        get => _isRdpBusy;
        private set
        {
            SetProperty(ref _isRdpBusy, value);
            UpdateRdpCommands();
            CloseSessionCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<string> RdpInvitationParticipants { get; } = [];

    private void UpdateRdpCommands()
    {
        StartRdpShareCommand.RaiseCanExecuteChanged();
        StopRdpShareCommand.RaiseCanExecuteChanged();
        StartScreenShareCommand.RaiseCanExecuteChanged();
        StartAutoShareCommand.RaiseCanExecuteChanged();
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
                StartAutoShareCommand.RaiseCanExecuteChanged();
                StopAutoShareCommand.RaiseCanExecuteChanged();
                SendSampleFileCommand.RaiseCanExecuteChanged();
                SendSelectedFileCommand.RaiseCanExecuteChanged();
                RegisterSelectedFileCommand.RaiseCanExecuteChanged();
                SendChatCommand.RaiseCanExecuteChanged();
                UpdateRdpCommands();
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
                UpdateRdpCommands();
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

    /// <summary>학생이 골라 받을 수 있게 등록한 강의 파일 목록입니다(U08).</summary>
    public ObservableCollection<RegisteredFileItem> RegisteredFiles { get; } = [];

    /// <summary>선택한 파일을 강의 파일 목록에 등록합니다. 본문은 학생이 요청할 때만 보냅니다.</summary>
    public RelayCommand RegisterSelectedFileCommand { get; }

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

    public RelayCommand StartRdpShareCommand { get; }

    public RelayCommand StopRdpShareCommand { get; }

    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set
        {
            if (SetProperty(ref _selectedFilePath, value))
            {
                SendSelectedFileCommand.RaiseCanExecuteChanged();
                RegisterSelectedFileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FileShareStatus
    {
        get => _fileShareStatus;
        private set => SetProperty(ref _fileShareStatus, value);
    }

    private async Task OpenSessionAsync()
    {
        IsBusy = true;
        SessionStatus = "세션 여는 중...";

        try
        {
            var roomPassword = RoomPasswordProvider?.Invoke() ?? string.Empty;
            _secureCertificate ??= LoadSecureCertificate();
            await _sessionManager.OpenSessionAsync(SessionName, Port, roomPassword.AsMemory(), _secureCertificate);
            _heartbeatService.Start();
            IsSessionOpen = true;
            ConnectionCode = _sessionManager.ConnectionCode ?? "-";
            if (_sessionManager.FileTransfers is { } fileTransfers) fileTransfers.FileStored += OnStudentFileStored;
            SessionStatus = $"세션 Open · 포트 {Port}";
            StatusMessage = $"'{SessionName}' 세션이 시작되었습니다. 학생에게 호스트 IP, 포트, 접속 코드를 알려 주세요." +
                            (_sessionManager.IsRoomPasswordProtected ? " 방 비밀번호도 함께 알려 주세요." : string.Empty);
            IsStatusError = false;
            RdpStatus = "WDS 공유 시작 후 학생을 연결해 주세요. 이미 참여한 학생은 RDP 재접속을 눌러 주세요.";
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
            try { await StopRdpShareCoreAsync(); }
            finally
            {
                try { await _screenShareService.StopContinuousBroadcastAsync(); }
                finally { await _sessionManager.CloseSessionAsync(); }
            }
            IsSessionOpen = false;
            ConnectionCode = "세션을 열면 표시됩니다.";
            RegisteredFiles.Clear();
            IsScreenSharing = false;
            ParticipantCount = 0;
            SessionStatus = "세션 닫힘";
            LatestScreenStatus = "화면 공유가 중지되었습니다.";
            RdpStatus = "WDS 화면 공유가 중지되었습니다.";
            StatusMessage = "세션이 종료되었습니다.";
            IsStatusError = false;
            ChatMessages.Insert(0, ChatLine.System("세션이 닫혔습니다."));
            SyncLogs();
        }
        catch (Exception ex)
        {
            StatusMessage = $"세션 종료 확인 필요: {ex.GetType().Name}";
            IsStatusError = true;
            IsSessionOpen = _sessionManager.CurrentSession is not null;
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
            var sessionId = _sessionManager.CurrentSession?.SessionId;
            FilePacket? firstPacket = null;
            var sentChunks = 0;
            FileShareStatus = $"{label} 전송 준비 중: {Path.GetFileName(filePath)}";
            IsStatusError = false;
            SyncLogs();

            await foreach (var packet in _fileDistributor.StreamFilePacketsAsync(
                filePath, "Server", sessionId, FileTransferRules.MinChunkSize))
            {
                if (!IsSessionOpen || _sessionManager.CurrentSession?.SessionId != sessionId)
                    throw new InvalidOperationException("파일 전송 중 강의 세션이 변경되었습니다.");
                firstPacket ??= packet;
                await _sessionManager.BroadcastPacketAsync(packet);
                sentChunks++;
                FileShareStatus = $"{label} 전송 중: {packet.FileName} / {sentChunks}/{packet.TotalChunks} chunks";
            }

            if (firstPacket is null)
                throw new InvalidOperationException("파일 패킷이 생성되지 않았습니다.");
            FileShareStatus = $"{label} 전송 완료: {firstPacket.FileName} / {sentChunks} chunks / {firstPacket.FileSize} byte";
            StatusMessage = FileShareStatus;
            SharedFiles.Insert(0, $"{firstPacket.FileName} ({firstPacket.FileSize} byte, {sentChunks} chunks)");
            _logSink.Write($"파일 전송 완료: {firstPacket.FileName}, chunks={sentChunks}, checksum={firstPacket.Checksum[..Math.Min(12, firstPacket.Checksum.Length)]}...");
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

    public async Task StartRdpShareAsync()
    {
        if (!IsSessionOpen || IsBusy || IsRdpBusy || IsRdpSharing || _shuttingDown) return;
        IsRdpBusy = true;
        await _rdpLifecycle.WaitAsync();
        try
        {
            var sessionId = _sessionManager.CurrentSession!.SessionId;
            await StopAutoShareAsync();
            var sharingId = await _rdpSharing.StartAsync(sessionId);
            _sessionManager.AttachRdpSharing(_rdpSharing, sharingId);
            IsRdpSharing = true;
            RdpStatus = "WDS 공유 중 · 보기 전용 · 최대 학생 2명. 학생 앱에서 초대를 요청해 주세요.";
        }
        catch (Exception ex)
        {
            RdpStatus = $"WDS 시작 실패: {ex.GetType().Name}. Windows WDS 지원 환경을 확인해 주세요.";
        }
        finally { _rdpLifecycle.Release(); IsRdpBusy = false; SyncLogs(); }
    }

    public async Task StopRdpShareAsync()
    {
        if (IsRdpBusy || IsBusy) return;
        IsRdpBusy = true;
        try { await StopRdpShareCoreAsync(); }
        catch (Exception ex) { RdpStatus = $"WDS 종료 확인 필요: {ex.GetType().Name}"; }
        finally { IsRdpBusy = false; SyncLogs(); }
    }

    private async Task StopRdpShareCoreAsync()
    {
        await _rdpLifecycle.WaitAsync();
        try
        {
            // 초대 발급을 먼저 막고, 회수 알림 전송에 실패하더라도 네이티브 공유를 닫는다.
            var inputRevoke = RemoteInputRevokeStatus.Failed;
            try { inputRevoke = await _sessionManager.DetachRdpSharingAsync(); }
            finally
            {
                await _rdpSharing.StopAsync();
                IsRdpSharing = false;
                RdpInvitationParticipants.Clear();
                // 승인 회수와 실제 입력 차단 확인은 별개이므로, 확인되지 않았으면 완료로 표시하지 않는다.
                RdpStatus = inputRevoke switch
                {
                    RemoteInputRevokeStatus.Confirmed => "WDS 화면 공유 중지됨",
                    RemoteInputRevokeStatus.Pending => "WDS 화면 공유 중지됨 · 원격 입력 차단 확인 대기 중",
                    _ => "WDS 화면 공유 중지됨 · 원격 입력 차단 확인 실패(재시도 대기)"
                };
            }
        }
        finally { _rdpLifecycle.Release(); }
    }

    private void OnInvitationReady(RdpInvitationHandoff handoff) => RunOnUi(() =>
    {
        // 예약된 이벤트보다 회수/재발급이 먼저 끝났다면 오래된 항목을 표시하지 않는다.
        if (_sessionManager.TryGetPendingInvitationHandoff(handoff.ParticipantId)?.InvitationId != handoff.InvitationId) return;
        if (!RdpInvitationParticipants.Contains(handoff.ParticipantId))
            RdpInvitationParticipants.Add(handoff.ParticipantId);
    });

    private void OnInvitationWithdrawn(string participantId) => RunOnUi(() =>
    {
        if (_sessionManager.TryGetPendingInvitationHandoff(participantId) is null)
            RdpInvitationParticipants.Remove(participantId);
    });

    public string? GetInvitationPassword(string participantId)
    {
        var handoff = _sessionManager.TryGetPendingInvitationHandoff(participantId);
        return IsRdpSharing && handoff?.ExpiresAt > DateTimeOffset.UtcNow ? handoff.Password : null;
    }

    private async Task RegisterSelectedFileAsync()
    {
        var path = SelectedFilePath;
        try
        {
            var file = await _sessionManager.RegisterFileAsync(path);
            RegisteredFiles.Add(new RegisteredFileItem(file, UnregisterFile));
            FileShareStatus = $"강의 파일 목록에 등록했습니다: {file.FileName}. 학생이 목록에서 골라 받을 수 있습니다.";
        }
        catch (Exception ex)
        {
            // 로컬 경로가 담긴 예외 메시지는 화면에 그대로 보여 주지 않는다.
            FileShareStatus = "파일을 등록하지 못했습니다: " + CollaborationErrorCatalog.FromException(ex).UserMessage;
            _logSink.Write($"[FileRoute] 등록 실패: {ex.GetType().Name}");
        }
        SyncLogs();
    }

    private void UnregisterFile(RegisteredFileItem item)
    {
        try
        {
            _sessionManager.UnregisterFile(item.File.FileId);
            FileShareStatus = $"목록에서 내렸습니다: {item.File.FileName}. 이미 받은 학생의 파일은 그대로 남습니다.";
        }
        catch (InvalidOperationException)
        {
            FileShareStatus = "세션이 닫혀 있어 목록을 바꿀 수 없습니다.";
        }
        RegisteredFiles.Remove(item);
        SyncLogs();
    }

    private void OnStudentFileStored(ParticipantConnection student, FileStoredNotice notice) => RunOnUi(() =>
    {
        var name = _sessionManager.Participants.TryResolve(student.ConnectionId)?.DisplayName ?? "학생";
        var file = RegisteredFiles.FirstOrDefault(item => item.File.FileId == notice.FileId)?.File.FileName ?? "파일";
        FileShareStatus = $"{name}님이 {file} 저장을 완료했습니다.";
        SyncLogs();
    });

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public async Task ShutdownAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        _heartbeatService.Stop();
        try { await CloseSessionAsync(); }
        finally { await _rdpSharing.DisposeAsync(); }
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

    /// <summary>
    /// 저장소 인증서를 쓰면 앱을 다시 켜도 접속 코드가 같습니다. 저장소를 쓸 수 없으면 이번 실행에만 쓰는 인증서로 대체하며,
    /// 이 경우 접속 코드는 실행할 때마다 바뀝니다.
    /// </summary>
    private X509Certificate2 LoadSecureCertificate()
    {
        if (_certificateProvider is not null) return _certificateProvider();
        try
        {
            return ProfessorCertificateStore.LoadOrCreate();
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logSink.Write($"[Secure] 인증서 저장소 사용 불가, 임시 인증서 사용: {ex.GetType().Name}");
            return ProfessorCertificateStore.CreateEphemeral();
        }
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
