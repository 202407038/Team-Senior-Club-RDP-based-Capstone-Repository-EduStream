using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EduStream.Core.Collaboration;
using EduStream.Core.Factories;
using EduStream.Core.FileSharing;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Protocols;
using EduStream.Core.Utils;

namespace EduStream.Server.Services;

/// <summary>
/// 세션 개설/종료, 참여자 관리, 패킷 라우팅을 담당합니다.
/// TcpServerService를 통해 실제 네트워크 전송을 수행합니다.
/// </summary>
public sealed class SessionManager
{
    private const int MaxChatMessageLength = 500;
    private const string ProfessorDisplayName = "교수자";

    private readonly ILogSink _logSink;
    private readonly TcpServerService _tcpServer;
    private readonly ConcurrentDictionary<string, string> _participants = new(); // displayName → clientId
    private readonly ConcurrentDictionary<string, string> _clientDisplayNames = new(); // clientId → displayName
    private readonly ConcurrentDictionary<string, DateTimeOffset> _clientLastSeen = new(); // clientId → 마지막 수신 시각
    private readonly ConcurrentDictionary<string, RdpInvitationPacket> _rdpInvitations = new(); // participantId(displayName) → 발급된 초대
    private readonly ConcurrentDictionary<Guid, IRdpSharingService> _invitationOwners = new();
    private readonly ConcurrentDictionary<string, RdpInvitationHandoff> _pendingInvitationHandoffs = new(); // participantId → 인계 대기 중인 비밀번호
    // U03: 공유 시작 전 요청했거나 공유 멈춤으로 초대가 회수된 학생. 공유가 다시 붙으면 재요청 알림을 받는다.
    private readonly ConcurrentDictionary<string, ParticipantConnection> _screenWaiters = new(); // clientId → 대기 시점 연결
    private readonly ParticipantRegistry _participantRegistry = new();
    private readonly object _sessionLock = new();
    private IRdpSharingService? _rdpSharingService;
    private Guid _rdpSharingId;
    // 공유 수명 동안만 유효한 토큰. 공유 중지/세션 종료 시 취소해 그 공유에서 시작된 제어 요청을 끝낸다.
    private CancellationTokenSource? _sharingLifetime;
    private ParticipantConnection? _professorConnection;
    private ServerRemoteControlCoordinator? _controlCoordinator;
    private IRemoteInputGate _remoteInputGate = UnavailableRemoteInputGate.Instance;
    private StudentRemoteInputGate? _studentInputGate;
    private RoomPasswordVerifier? _roomPassword;
    private ISessionFileCatalog? _fileCatalog;
    private SecureCollaborationListener? _secureListener;
    private SecureRoomGate? _secureGate;
    private readonly ConcurrentDictionary<string, SecureCollaborationConnection> _secureConnections = new(); // clientId → 묶인 보호 채널
    private long _controlNoticeSequence;
    // 재연결 토큰 SHA256 → 부여 정보. _sessionLock으로 보호한다.
    private readonly Dictionary<string, ReconnectGrant> _reconnectGrants = new(StringComparer.Ordinal);

    /// <summary>ExpiresAt이 null이면 아직 연결 중인 참가자의 토큰입니다. 비정상으로 끊기면 만료 시각이 정해집니다.</summary>
    private sealed record ReconnectGrant(string ClientId, string DisplayName, DateTimeOffset? ExpiresAt,
        bool AllowViewing, bool AllowControl);

    /// <summary>재연결 티켓으로 참가할 때 복원할 허용 상태입니다.</summary>
    private sealed record ReconnectRestore(bool AllowViewing, bool AllowControl);
    private SessionFileTransferRouter? _fileTransfers;
    private ReverseCollaborationRouter? _reverseRouter;

    /// <summary>
    /// 참여자 목록이 변경되었을 때 발생합니다.
    /// </summary>
    public event Action? ParticipantsChanged;

    /// <summary>
    /// 클라이언트로부터 채팅 메시지를 수신했을 때 발생합니다.
    /// (sender, message)
    /// </summary>
    public event Action<string, string>? ChatReceived;

    /// <summary>
    /// RDP 초대 비밀번호가 발급되어 교수자 앱이 별도 채널(화면 표시, 구두 전달 등)로
    /// 학생에게 인계할 수 있게 됐을 때 발생합니다. 비밀번호는 이 이벤트로만 전달되며
    /// TCP 패킷에는 실리지 않습니다.
    /// </summary>
    public event Action<RdpInvitationHandoff>? RdpInvitationPasswordReady;

    /// <summary>
    /// 인계 대기 중이던 비밀번호가 재발급/이탈/세션 종료 등으로 더 이상 유효하지 않게 됐을 때
    /// 발생합니다. 교수자 앱은 표시 중인 비밀번호를 이 알림을 받으면 즉시 화면에서 지워야 합니다.
    /// </summary>
    public event Action<string>? RdpInvitationPasswordWithdrawn;

    public SessionManager(ILogSink logSink, TcpServerService tcpServer)
    {
        _logSink = logSink;
        _tcpServer = tcpServer;

        _tcpServer.PacketReceived += OnPacketReceivedAsync;
        _tcpServer.ClientDisconnected += OnClientDisconnectedAsync;
        _participantRegistry.PermissionsChanged += OnRegistryPermissionsChanged;
    }

    public SessionInfo? CurrentSession { get; private set; }

    public bool IsSessionOpen => CurrentSession is not null;

    /// <summary>
    /// 실제 승인된 연결 기준 참가자 목록/권한 레지스트리입니다.
    /// 파일 요청 인가(IFileRequestAuthorizer)는 이 레지스트리를 대조해서 판단해야 합니다.
    /// </summary>
    public ParticipantRegistry Participants => _participantRegistry;

    /// <summary>
    /// 현재 진행 중인 원격 제어 승인 상태입니다. 세션이 열려 있지 않으면 null입니다.
    /// </summary>
    public RemoteControlState? CurrentControlState => _controlCoordinator?.Current;

    /// <summary>
    /// 현재 참여자 이름 목록을 반환합니다.
    /// </summary>
    public IReadOnlyCollection<string> ParticipantNames => _participants.Keys.ToList().AsReadOnly();

    public int ParticipantCount => _participants.Count;

    /// <summary>
    /// 현재 활성 상태인 RDP 초대 수입니다. 세션 종료/이탈 정리가 실제로
    /// 잔류 초대 없이 끝났는지 테스트/모니터링에서 확인할 때 사용합니다.
    /// </summary>
    public int RdpInvitationCount => _rdpInvitations.Count;

    /// <summary>
    /// 지정한 참가자에게 인계할 RDP 초대 비밀번호가 남아 있으면 반환합니다.
    /// 교수자 앱이 <see cref="RdpInvitationPasswordReady"/>를 놓쳤을 때 다시 조회하는 용도입니다.
    /// </summary>
    public RdpInvitationHandoff? TryGetPendingInvitationHandoff(string participantId) =>
        _pendingInvitationHandoffs.TryGetValue(participantId, out var handoff) ? handoff : null;

    /// <summary>
    /// 마지막 수신 시각이 지정한 임계를 넘어선 클라이언트 ID 목록을 반환합니다.
    /// HeartbeatService가 비활성 클라이언트를 끊을 때 사용합니다.
    /// </summary>
    public IReadOnlyCollection<string> GetStaleClientIds(TimeSpan timeout)
    {
        var threshold = DateTimeOffset.UtcNow - timeout;
        var stale = new List<string>();
        foreach (var (clientId, lastSeen) in _clientLastSeen)
        {
            if (lastSeen < threshold)
            {
                stale.Add(clientId);
            }
        }
        return stale;
    }

    /// <summary>
    /// 3번이 구현한 RDP 공유 서비스를 연결합니다. 화면 공유가 실제로 시작된 뒤 팀장이 호출합니다.
    /// 연결 전에는 RdpInvitationRequest를 RdpSharingNotStarted로 거부합니다.
    /// </summary>
    public void AttachRdpSharing(IRdpSharingService sharingService, Guid sharingId)
    {
        ArgumentNullException.ThrowIfNull(sharingService);
        if (sharingId == Guid.Empty)
            throw new ArgumentException("공유 ID가 필요합니다.", nameof(sharingId));

        lock (_sessionLock)
        {
            _rdpSharingService = sharingService;
            _rdpSharingId = sharingId;
            _reverseRouter?.BeginAnnotationSharing(sharingId);
            if (_sharingLifetime is null || _sharingLifetime.IsCancellationRequested)
                _sharingLifetime = new CancellationTokenSource();
        }
        _logSink.Write($"[Rdp] 공유 서비스 연결: sharingId={sharingId}");

        // 호출자(교수자 UI)는 학생 알림 전송을 기다리지 않는다. 전송 실패는 연결별로 TcpServerService가 정리한다.
        _ = ResumeScreenWaitersAsync(sharingService).ContinueWith(
            task => _logSink.Write($"[Rdp] 화면 복귀 알림 오류: {task.Exception?.GetBaseException().GetType().Name}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// 공유 재시작 자동 복귀 대기 중인 학생 수입니다. 테스트/모니터링용입니다.
    /// </summary>
    public int ScreenWaiterCount => _screenWaiters.Count;

    /// <summary>
    /// 3번이 구현한 실제 원격 입력 엔진을 연결합니다. 연결 전에는 제어 요청이 Active가 되지 않고 Failed로 끝납니다.
    /// 진행 중인 제어가 있으면 교체할 수 없습니다.
    /// </summary>
    public void AttachRemoteInputGate(IRemoteInputGate inputGate)
    {
        ArgumentNullException.ThrowIfNull(inputGate);
        lock (_sessionLock)
        {
            _controlCoordinator?.SetInputGate(inputGate);
            _remoteInputGate = inputGate;
        }
        _logSink.Write("[Control] 입력 엔진 연결");
    }

    /// <summary>
    /// 교수자가 선택한 학생 한 명에게 원격 제어를 요청합니다. 기존 대상은 먼저 회수하고
    /// 3번의 실제 입력 회수 확인 후에 새 대상으로 전환합니다. Active는 입력 허용 완료 후에만 표시됩니다.
    /// 화면 공유가 연결되어 있을 때만 요청할 수 있고, 요청 중 공유가 중지되면 요청은 회수되어 Active가 되지 않습니다.
    /// </summary>
    public async Task RequestControlAsync(string targetDisplayName)
    {
        if (CurrentSession is null)
            throw new InvalidOperationException("현재 열려 있는 세션이 없습니다.");
        if (_controlCoordinator is null)
            throw new InvalidOperationException("제어 조정자가 초기화되지 않았습니다.");
        if (!_participants.TryGetValue(targetDisplayName, out var clientId))
            throw new InvalidOperationException($"{targetDisplayName}은(는) 현재 참가자가 아닙니다.");

        var target = _participantRegistry.TryGetConnection(clientId)
            ?? throw new InvalidOperationException($"{targetDisplayName}의 연결 정보를 찾을 수 없습니다.");

        CancellationToken sharingToken;
        lock (_sessionLock)
        {
            // 공유 중지가 시작되면 서비스 참조가 먼저 비워지므로, 중지 중·중지 후 요청은 여기서 막힌다.
            if (_rdpSharingService is null || _sharingLifetime is null || _sharingLifetime.IsCancellationRequested)
                throw new InvalidOperationException("현재 화면 공유가 시작되지 않았습니다.");
            sharingToken = _sharingLifetime.Token;
        }

        var coordinator = _controlCoordinator;
        try
        {
            await coordinator.RequestAsync(target, sharingToken);
        }
        catch (OperationCanceledException) when (sharingToken.IsCancellationRequested)
        {
            // 진입 확인 뒤 공유 중지가 끼어든 경우. 조정자가 요청을 이미 회수했다.
            _logSink.Write($"[Control] 공유 중지로 요청 취소: 대상={targetDisplayName}");
            return;
        }

        var state = coordinator.Current;
        if (state is not null && state.Student == target && state.Phase == ControlPhase.Active)
            _logSink.Write($"[Control] 활성: 대상={targetDisplayName}, requestId={state.RequestId}");
        else
            _logSink.Write($"[Control] 요청 대체됨: 대상={targetDisplayName}");
    }

    /// <summary>
    /// 현재 원격 제어 대상을 회수하고 3번의 실제 입력 차단 확인까지 기다립니다.
    /// </summary>
    public async Task StopControlAsync()
    {
        if (_controlCoordinator is null) return;
        await _controlCoordinator.StopAsync();
        _logSink.Write("[Control] 회수");
    }

    /// <summary>
    /// 학생의 보기/제어 허용 변경을 서버 기준 레지스트리에 반영합니다. 해당 학생이 제어 대상이면
    /// 승인을 즉시 회수하고 실제 입력 차단 확인까지 기다립니다.
    /// </summary>
    /// <remarks>
    /// 현재 단계에서는 학생 앱→서버 권한 변경 메시지 계약이 확정되지 않아 서버 내부 진입점만 제공합니다.
    /// </remarks>
    public async Task<bool> UpdateParticipantPermissionsAsync(string displayName, bool allowViewing, bool allowControl)
    {
        if (!_participants.TryGetValue(displayName, out var clientId))
            return false;
        return await UpdatePermissionsForClientAsync(clientId, displayName, allowViewing, allowControl);
    }

    private async Task<bool> UpdatePermissionsForClientAsync(string clientId, string displayName, bool allowViewing, bool allowControl)
    {
        var connection = _participantRegistry.TryGetConnection(clientId);
        if (connection is null || !_participantRegistry.SetPermissions(connection.ConnectionId, allowViewing, allowControl))
            return false;

        // 보기 허용은 교수자가 학생 화면을 보는 권한(U06/U07)이다. 학생이 교수자 공유 화면을 받는 자동 복귀(U03)와는 무관하다.
        _logSink.Write($"[Control] 허용 변경: 대상={displayName}, 보기={allowViewing}, 제어={allowViewing && allowControl}");
        await ConfirmControlInputRevokedAsync();
        return true;
    }

    /// <summary>
    /// 교수자 로컬 파일을 강의 카탈로그에 등록합니다. 본문은 아직 전송하지 않으며
    /// 이름·길이·SHA256·청크 크기만 목록에 올라갑니다.
    /// </summary>
    public async Task<SessionFileDescriptor> RegisterFileAsync(string localPath, CancellationToken cancellationToken = default)
    {
        var catalog = _fileCatalog ?? throw new InvalidOperationException("현재 열려 있는 세션이 없습니다.");
        var descriptor = await catalog.RegisterAsync(localPath, cancellationToken);
        PublishFileCatalog();
        return descriptor;
    }

    /// <summary>
    /// 등록된 파일을 목록에서 내립니다. 이후 요청/미완료 청크는 차단되지만
    /// 이미 저장 완료된 파일은 학생 쪽에 그대로 남습니다.
    /// </summary>
    public bool UnregisterFile(Guid fileId)
    {
        if (_fileCatalog is null)
            throw new InvalidOperationException("현재 열려 있는 세션이 없습니다.");
        if (!_fileCatalog.Unregister(fileId)) return false;
        // 진행 중인 해당 파일 전송은 카탈로그가 다음 청크 전에 FileUnavailable로 끊는다.
        PublishFileCatalog();
        return true;
    }

    /// <summary>
    /// 학생 연결별 파일 목록 전달·다운로드 요청 라우팅입니다. 세션이 열려 있지 않으면 null입니다.
    /// 보호 채널이 확정되면 인증된 연결마다 AttachAsync로 붙입니다.
    /// </summary>
    public SessionFileTransferRouter? FileTransfers => _fileTransfers;

    /// <summary>
    /// 학생→교수자 역방향 초대(Kind 15·16) 대조와 교수자→학생 판서(Kind 17) 전달입니다. 세션이 열려 있지 않으면 null입니다.
    /// 교수자 viewer는 InvitationReady/InvitationWithdrawn을, 판서 엔진은 PublishAnnotationAsync를 연결합니다.
    /// </summary>
    public ReverseCollaborationRouter? ReverseCollaboration => _reverseRouter;

    /// <summary>
    /// 교수자 판서 엔진이 낸 JSON을 현재 화면 공유의 판서로 학생 전원에게 보냅니다. 공유가 없으면 SessionClosed로 실패합니다.
    /// </summary>
    public Task<int> PublishAnnotationAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var router = _reverseRouter ?? throw new CollaborationException(CollaborationError.SessionClosed);
        return router.PublishAnnotationAsync(payloadJson, cancellationToken);
    }

    /// <summary>
    /// 현재 강의의 파일 목록 스냅샷입니다. 세션이 열려 있지 않으면 null입니다.
    /// revision은 등록/해제마다 증가하므로 학생 쪽 동기화 여부 판단에 사용할 수 있습니다.
    /// </summary>
    public SessionFileCatalogSnapshot? GetFileCatalogSnapshot() => _fileCatalog?.GetSnapshot();

    private void PublishFileCatalog()
    {
        var router = _fileTransfers;
        if (router is null) return;
        // 등록/해제 호출자는 학생 전달 완료를 기다리지 않는다. 송신 실패는 라우터가 연결별로 로그만 남긴다.
        _ = router.PublishCatalogAsync().ContinueWith(
            task => _logSink.Write($"[FileRoute] 목록 전달 오류: {task.Exception?.GetBaseException().GetType().Name}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// 승인은 회수했지만 실제 입력 차단 확인이 아직 끝나지 않은 원격 제어가 있으면 true입니다.
    /// <see cref="DetachRdpSharingAsync"/>가 Pending/Failed를 반환한 뒤 차단 완료 여부를 다시 확인할 때 사용합니다.
    /// </summary>
    public bool IsControlInputRevokePending => _controlCoordinator?.IsInputRevokePending ?? false;

    /// <summary>
    /// 화면 공유가 종료될 때 팀장이 호출합니다. 남아 있는 초대와 진행 중인 원격 제어를 모두 정리합니다.
    /// 공유가 없는 상태에서 제어만 남아 있는 것은 의미가 없으므로, 세션 종료가 아니라
    /// "화면 공유만 중지"하는 경우에도 항상 함께 회수합니다.
    /// </summary>
    /// <returns>
    /// 실제 입력 차단 확인 결과. 승인 상태는 항상 즉시 회수되지만, Confirmed가 아니면
    /// 학생 PC 입력이 막혔다고 표시하면 안 됩니다.
    /// </returns>
    public async Task<RemoteInputRevokeStatus> DetachRdpSharingAsync()
    {
        EndSharingLifetime();
        // 공유 멈춤은 강의 종료가 아니므로 지금 화면을 받던 학생은 재시작 때 자동 복귀 대상으로 남긴다.
        foreach (var participantId in _rdpInvitations.Keys.ToList())
        {
            if (_participants.TryGetValue(participantId, out var clientId))
                TryAddScreenWaiter(clientId);
        }
        // 공유 재시작 뒤 제어를 자동 재승인하지 않는다(화면 수신 자동 복귀와 별개, 협의 필요 항목).
        var inputRevoke = await StopControlForTeardownAsync("공유 중지");
        await RevokeAllRdpInvitationsAsync(RdpFailureReason.SessionClosed);
        _logSink.Write($"[Rdp] 공유 서비스 연결 해제: 입력 차단={inputRevoke}");
        return inputRevoke;
    }

    /// <summary>
    /// 방 비밀번호가 설정된 세션인지 여부입니다. 비밀번호 값 자체는 어디에도 노출하지 않습니다.
    /// </summary>
    public bool IsRoomPasswordProtected => _roomPassword is not null;

    /// <summary>
    /// 구버전 진단용 인증서 지문입니다. 새 LAN API에서는 요구하지 않습니다. 기존 UI의 표시/입력 제거는 5번 후속 작업입니다.
    /// </summary>
    public string? ConnectionCode => _secureListener?.ConnectionCode;

    /// <summary>
    /// 보호 채널 참가 인증(티켓)을 거쳐야만 참가할 수 있는 세션이면 true입니다.
    /// </summary>
    public bool IsSecureJoinRequired => _secureGate is not null;

    /// <summary>현재 참가자와 묶인 보호 채널 수입니다. 테스트/모니터링용입니다.</summary>
    public int SecureConnectionCount => _secureConnections.Count;

    /// <summary>비정상 끊김 뒤 재연결 토큰을 쓸 수 있는 시간입니다(U03 자동 재연결).</summary>
    public TimeSpan ReconnectWindow { get; set; } = ReconnectRules.DefaultWindow;

    /// <summary>발급되어 아직 쓰이지 않은 재연결 토큰 수입니다. 테스트/모니터링용입니다.</summary>
    public int ReconnectGrantCount
    {
        get { lock (_sessionLock) return _reconnectGrants.Count; }
    }

    /// <summary>
    /// roomPassword가 비어 있으면 비밀번호 없는 방입니다. 비밀번호는 해시로만 보관하며
    /// SessionInfo에 넣지 않습니다(브로드캐스트/직렬화 노출 방지).
    /// </summary>
    /// <param name="secureChannelCertificate">
    /// 지정하면 세션 포트 + 1에서 보호 채널을 열고, 비밀번호 유무와 관계없이 모든 참가에 보호 채널 인증 티켓을 요구합니다.
    /// 교수자 앱은 항상 지정합니다. 지정하지 않으면 비밀번호 방은 참가를 모두 거부하고(fail-closed),
    /// 비밀번호 없는 방은 기존 v1 참가 경로만 씁니다(기존 테스트·개발 경로 호환).
    /// </param>
    public async Task<SessionInfo> OpenSessionAsync(string sessionName, int port, ReadOnlyMemory<char> roomPassword = default,
        X509Certificate2? secureChannelCertificate = null)
    {
        var securePort = secureChannelCertificate is null ? 0 : CollaborationPorts.ForSession(port);
        // 해시 계산은 잠금 밖에서 끝내고, 입력 오류면 세션을 열지 않는다.
        var passwordVerifier = RoomPasswordVerifier.Create(roomPassword.Span);

        lock (_sessionLock)
        {
            if (CurrentSession is not null)
            {
                throw new InvalidOperationException(
                    $"세션이 이미 열려 있습니다. 이름={CurrentSession.SessionName}, 포트={CurrentSession.Port}");
            }

            CurrentSession = new SessionInfo
            {
                SessionName = sessionName,
                HostName = Environment.MachineName,
                Port = port,
                HostAddress = "127.0.0.1"
            };

            _professorConnection = new ParticipantConnection(
                CurrentSession.SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
            _controlCoordinator = new ServerRemoteControlCoordinator(
                _professorConnection, _participantRegistry, _remoteInputGate, _logSink);
            _controlCoordinator.StateChanged += OnControlStateChanged;
            _roomPassword = passwordVerifier;
            _fileCatalog = new SessionFileCatalog(
                CurrentSession.SessionId, new SessionFileRequestAuthorizer(_participantRegistry));
            _fileTransfers = new SessionFileTransferRouter(_fileCatalog, _participantRegistry, _logSink);
            _reverseRouter = new ReverseCollaborationRouter(_professorConnection, _participantRegistry, _logSink);
            // 교수자가 학생 화면을 더 이상 볼 수 없으면(학생 공유 재시작·초대 교체 등) 그 학생 제어도 회수한다.
            // 보이지 않는 화면을 계속 조작하게 두지 않는다. 새 화면이 붙어도 제어는 자동 재승인하지 않는다.
            var coordinator = _controlCoordinator;
            _reverseRouter.InvitationWithdrawn += (student, _) => coordinator.WithdrawTarget(student, "역방향 화면 회수");
            if (secureChannelCertificate is not null)
            {
                // 보호 채널이 있으면 학생 PC로 실제 입력 허용/회수를 보낸다. 별도 입력 엔진을 붙였으면 그것을 유지한다.
                var reverseRouter = _reverseRouter;
                _studentInputGate = new StudentRemoteInputGate(reverseRouter.ProfessorId, _participantRegistry,
                    FindSecureChannel,
                    student => reverseRouter.TryGetInvitation(student.ConnectionId)?.Invitation.SharingId,
                    _logSink);
                if (_remoteInputGate is UnavailableRemoteInputGate) coordinator.SetInputGate(_studentInputGate);
                _secureListener = new SecureCollaborationListener(secureChannelCertificate, _logSink);
                _secureGate = new SecureRoomGate(_secureListener, CurrentSession.SessionId, passwordVerifier, _logSink);
                _secureGate.ConnectionClosed += OnSecureConnectionClosed;
                _secureGate.BoundFrameReceived += OnSecureFrameAsync;
                _secureGate.ReconnectValidator = ValidateReconnectAsync;
            }
        }

        var session = CurrentSession;
        try
        {
            _tcpServer.Start(port);
            _secureListener?.Start(securePort);
        }
        catch
        {
            await CloseSessionAsync();
            throw;
        }
        _logSink.Write($"[Session] 개설: 이름={sessionName}, 포트={port}, 방 비밀번호={(passwordVerifier is null ? "없음" : "설정")}, " +
                       $"보호 채널={(_secureListener is null ? "없음" : $"포트 {securePort}")}");
        return session;
    }

    public async Task CloseSessionAsync()
    {
        // 종료 정리 전에 새 초대 발급과 제어 요청을 막는다. 기존 초대는 발급한 서비스로 회수한다.
        EndSharingLifetime();
        // 입력 회수 확인 실패가 세션 종료 자체를 막지 않게 한다. 결과는 로그로만 남긴다.
        await StopControlForTeardownAsync("세션 종료");
        // 세션 종료는 비정상 끊김이 아니므로 학생 앱이 자동 재연결하지 않게 먼저 알리고 토큰을 모두 폐기한다.
        lock (_sessionLock) _reconnectGrants.Clear();
        if (CurrentSession is { } closing)
        {
            var ended = CollaborationMessageCodec.Encode(Guid.NewGuid(), new SessionEndedNotice(closing.SessionId));
            var targets = _secureConnections.ToArray();
            foreach (var (clientId, secure) in targets)
                await SendSecureAsync(secure, ended, clientId);
            _logSink.Write($"[Reconnect] 세션 종료 알림: {targets.Length}명");
        }
        // 연결 정리 전에 클라이언트들에게 세션 종료 알림
        if (_participants.Count > 0)
        {
            await BroadcastSystemMessageAsync("교수자가 세션을 종료했습니다. 연결이 해제됩니다.");
        }

        await RevokeAllRdpInvitationsAsync(RdpFailureReason.SessionClosed);

        SecureRoomGate? secureGate;
        SecureCollaborationListener? secureListener;
        lock (_sessionLock)
        {
            if (CurrentSession is not null)
            {
                _logSink.Write($"[Session] 종료: 이름={CurrentSession.SessionName}");
            }
            CurrentSession = null;
            _controlCoordinator?.Dispose();
            _controlCoordinator = null;
            _professorConnection = null;
            _roomPassword = null;
            _fileTransfers?.Dispose();
            _fileTransfers = null;
            _reverseRouter?.Dispose();
            _reverseRouter = null;
            _studentInputGate?.Dispose();
            _studentInputGate = null;
            _fileCatalog?.Dispose();
            _fileCatalog = null;
            secureGate = _secureGate;
            secureListener = _secureListener;
            _secureGate = null;
            _secureListener = null;
        }

        if (secureGate is not null)
        {
            secureGate.ConnectionClosed -= OnSecureConnectionClosed;
            secureGate.BoundFrameReceived -= OnSecureFrameAsync;
            await secureGate.DisposeAsync();
        }
        if (secureListener is not null) await secureListener.DisposeAsync();
        _secureConnections.Clear();

        ClearParticipants();
        _participantRegistry.Clear();
        await _tcpServer.StopAsync();
    }

    /// <summary>
    /// 참가를 마친 학생의 보호 채널 메시지를 처리합니다. 대상 학생은 메시지 내용이 아니라 묶인 연결로만 정합니다.
    /// </summary>
    private async Task OnSecureFrameAsync(SecureCollaborationConnection secure, byte[] frame)
    {
        var clientId = FindClientId(secure);
        if (clientId is null) return;

        try
        {
            var kind = CollaborationFrameInspector.PeekKind(frame);
            switch (kind)
            {
                case CollaborationMessageKind.PermissionChange:
                    var request = CollaborationMessageCodec.Decode<PermissionChangeRequest>(frame, out _);
                    if (_clientDisplayNames.TryGetValue(clientId, out var displayName))
                        await UpdatePermissionsForClientAsync(clientId, displayName, request.AllowViewing, request.AllowControl);
                    break;
                case CollaborationMessageKind.FileRequest:
                case CollaborationMessageKind.FileCancel:
                case CollaborationMessageKind.FileStored:
                    var participant = _participantRegistry.TryGetConnection(clientId);
                    var router = _fileTransfers;
                    if (participant is not null && router is not null)
                        await router.HandleFrameAsync(participant, frame);
                    break;
                case CollaborationMessageKind.ReverseRdpInvitation:
                case CollaborationMessageKind.ReverseRdpInvitationSecret:
                    var student = _participantRegistry.TryGetConnection(clientId);
                    var reverse = _reverseRouter;
                    if (student is not null && reverse is not null)
                        await reverse.HandleFrameAsync(student, secure, frame);
                    break;
                case CollaborationMessageKind.RemoteInputResult:
                    var inputResult = CollaborationMessageCodec.Decode<RemoteInputResultNotice>(frame, out _);
                    var sender = _participantRegistry.TryGetConnection(clientId);
                    var inputGate = _studentInputGate;
                    if (sender is not null && inputGate is not null)
                        inputGate.HandleResult(sender, inputResult);
                    break;
                default:
                    _logSink.Write($"[Secure] 처리하지 않는 메시지 무시: kind={kind}, clientId={clientId}");
                    break;
            }
        }
        catch (CollaborationException ex)
        {
            _logSink.Write($"[Secure] 잘못된 메시지 무시: clientId={clientId}, 사유={ex.Code}");
        }
    }

    /// <summary>
    /// 학생 본인의 허용 상태만 보냅니다. 다른 학생의 이름·연결 정보는 학생에게 보내지 않습니다.
    /// 레지스트리 스냅샷 한 번으로 revision과 상태를 함께 읽어, 늦게 도착한 옛 상태를 학생이 revision으로 걸러낼 수 있게 합니다.
    /// </summary>
    private async Task PushStudentStatusAsync(string clientId)
    {
        if (!_secureConnections.TryGetValue(clientId, out var secure)) return;
        var connection = _participantRegistry.TryGetConnection(clientId);
        if (connection is null) return;
        var room = _participantRegistry.Snapshot(connection);
        if (room is null) return;
        var professor = _professorConnection;
        var visible = room.Participants.Where(participant => participant.Connection == connection).ToList();
        // 교수자 연결은 학생이 역방향 초대의 ProfessorId를 만들고 판서 발신자를 대조하는 데 필요하다. 다른 학생 정보는 보내지 않는다.
        if (professor is not null && professor.SessionId == connection.SessionId)
            visible.Add(new ParticipantSnapshot(professor, ProfessorDisplayName, Connected: true,
                AllowViewing: false, AllowControl: false, PermissionRevision: 0));
        var self = new RoomJoined(connection, room.Revision, visible);
        await SendSecureAsync(secure, CollaborationMessageCodec.Encode(Guid.NewGuid(), self), clientId);
    }

    private void OnRegistryPermissionsChanged(ParticipantSnapshot snapshot)
    {
        var clientId = FindClientId(snapshot.Connection);
        if (clientId is not null) _ = PushStudentStatusAsync(clientId);
    }

    /// <summary>
    /// 제어 대상 학생에게 현재 단계를 알립니다. 대상이 바뀌면 이전 학생은 Revoked, 새 학생은 Requested/Active를 받습니다.
    /// 번호는 상태 변경 순서대로 매겨 학생이 늦게 도착한 알림을 버릴 수 있게 합니다.
    /// </summary>
    private void OnControlStateChanged(RemoteControlState state)
    {
        var sequence = Interlocked.Increment(ref _controlNoticeSequence);
        var clientId = FindClientId(state.Student);
        if (clientId is null || !_secureConnections.TryGetValue(clientId, out var secure)) return;
        var notice = new ControlStatusNotice(state.Student.SessionId, sequence, state.Phase);
        _ = SendSecureAsync(secure, CollaborationMessageCodec.Encode(Guid.NewGuid(), notice), clientId);
    }

    /// <summary>참가 연결마다 새 재연결 토큰을 발급합니다. 이전 토큰은 폐기되고 연결 중에는 쓸 수 없습니다.</summary>
    private async Task IssueReconnectGrantAsync(string clientId, string displayName, SecureCollaborationConnection secure)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_sessionLock)
        {
            RemoveReconnectGrantsLocked(clientId);
            _reconnectGrants[HashToken(token)] = new ReconnectGrant(clientId, displayName, null, true, true);
        }
        var notice = new ReconnectGrantNotice(token, (int)Math.Ceiling(ReconnectWindow.TotalSeconds));
        await SendSecureAsync(secure, CollaborationMessageCodec.Encode(Guid.NewGuid(), notice), clientId);
    }

    private void ArmReconnectGrant(string clientId)
    {
        if (!_clientDisplayNames.ContainsKey(clientId)) return;
        var connection = _participantRegistry.TryGetConnection(clientId);
        var snapshot = connection is null ? null : _participantRegistry.TryResolve(connection.ConnectionId);
        var expiresAt = DateTimeOffset.UtcNow + ReconnectWindow;
        lock (_sessionLock)
        {
            foreach (var (key, grant) in _reconnectGrants.ToArray())
            {
                if (grant.ClientId != clientId || grant.ExpiresAt is not null) continue;
                _reconnectGrants[key] = grant with
                {
                    ExpiresAt = expiresAt,
                    AllowViewing = snapshot?.AllowViewing ?? true,
                    AllowControl = snapshot?.AllowControl ?? true
                };
            }
        }
    }

    private void RevokeReconnectGrant(string clientId)
    {
        lock (_sessionLock) RemoveReconnectGrantsLocked(clientId);
    }

    private void RemoveReconnectGrantsLocked(string clientId)
    {
        foreach (var key in _reconnectGrants.Where(pair => pair.Value.ClientId == clientId).Select(pair => pair.Key).ToArray())
            _reconnectGrants.Remove(key);
    }

    /// <summary>
    /// 재연결 토큰을 확인하고 소비합니다. 서버가 아직 끊김을 모르는 연결(학생 쪽이 먼저 알아챈 경우)이면
    /// 토큰 소유를 본인 증명으로 보고 옛 연결을 정리한 뒤 교체합니다.
    /// </summary>
    private async Task<object?> ValidateReconnectAsync(string displayName, string token)
    {
        if (token.Length > ReconnectRules.MaxTokenLength) return null;
        var key = HashToken(token);
        var now = DateTimeOffset.UtcNow;
        ReconnectGrant? grant;
        lock (_sessionLock)
        {
            foreach (var expired in _reconnectGrants.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                _reconnectGrants.Remove(expired);
            if (!_reconnectGrants.TryGetValue(key, out grant) || !string.Equals(grant.DisplayName, displayName, StringComparison.Ordinal))
                return null;
            _reconnectGrants.Remove(key);
        }

        if (grant.ExpiresAt is null)
        {
            var connection = _participantRegistry.TryGetConnection(grant.ClientId);
            var snapshot = connection is null ? null : _participantRegistry.TryResolve(connection.ConnectionId);
            grant = grant with { AllowViewing = snapshot?.AllowViewing ?? true, AllowControl = snapshot?.AllowControl ?? true };
            _logSink.Write($"[Reconnect] 끊김 전 연결 교체: clientId={grant.ClientId}");
            await _tcpServer.DisconnectClientAsync(grant.ClientId, "재연결로 교체");
        }

        _logSink.Write($"[Reconnect] 토큰 확인: 이름={displayName}");
        return new ReconnectRestore(grant.AllowViewing, grant.AllowControl);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// 참가 학생의 보호 채널을 파일 라우터에 붙여 현재 파일 목록을 보내고, 이후 등록/해제 목록과 다운로드 응답을 받게 합니다.
    /// 이탈·끊김 시 라우터는 레지스트리의 ConnectionRemoved로 스스로 떼어 냅니다.
    /// </summary>
    private async Task AttachFileRoutingAsync(string clientId, ParticipantConnection participant, SecureCollaborationConnection secure)
    {
        var router = _fileTransfers;
        if (router is null) return;
        try
        {
            await router.AttachAsync(participant, secure);
        }
        catch (Exception ex)
        {
            // 연결 직후 이탈 등. 파일 목록만 못 받을 뿐 참가 자체는 유지한다.
            _logSink.Write($"[FileRoute] 채널 연결 실패: clientId={clientId}, {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 학생이 교수자 신원을 먼저 알아야 판서를 대조할 수 있으므로, 상태(교수자 연결 포함)를 보낸 뒤 판서 복원을 붙인다.
    /// </summary>
    private async Task PushStatusThenAttachAnnotationsAsync(string clientId, ParticipantConnection participant,
        SecureCollaborationConnection secure)
    {
        await PushStudentStatusAsync(clientId);
        var router = _reverseRouter;
        if (router is null) return;
        try
        {
            await router.AttachAnnotationPeerAsync(participant, secure);
        }
        catch (Exception ex)
        {
            // 연결 직후 이탈 등. 판서만 못 받을 뿐 참가 자체는 유지한다.
            _logSink.Write($"[Annotation] 채널 연결 실패: clientId={clientId}, {ex.GetType().Name}");
        }
    }

    private async Task SendSecureAsync(SecureCollaborationConnection secure, byte[] frame, string clientId)
    {
        try
        {
            await secure.SendAsync(frame);
        }
        catch (Exception ex)
        {
            // 보호 채널이 끊기면 ConnectionClosed가 참가 연결을 정리한다. 여기서는 기록만 한다.
            _logSink.Write($"[Secure] 상태 전달 실패: clientId={clientId}, {ex.GetType().Name}");
        }
    }

    private string? FindClientId(SecureCollaborationConnection secure)
    {
        foreach (var (clientId, bound) in _secureConnections)
        {
            if (ReferenceEquals(bound, secure)) return clientId;
        }
        return null;
    }

    private SecureCollaborationConnection? FindSecureChannel(ParticipantConnection connection)
    {
        var clientId = FindClientId(connection);
        return clientId is not null && _secureConnections.TryGetValue(clientId, out var secure) ? secure : null;
    }

    private string? FindClientId(ParticipantConnection connection)
    {
        foreach (var clientId in _secureConnections.Keys)
        {
            if (_participantRegistry.TryGetConnection(clientId) == connection) return clientId;
        }
        return null;
    }

    /// <summary>
    /// 보호 채널이 닫히면 그 참가자의 기존 TCP 연결도 끊습니다. 두 연결은 한 참가자의 수명을 공유합니다.
    /// </summary>
    private void OnSecureConnectionClosed(SecureCollaborationConnection connection)
    {
        foreach (var (clientId, bound) in _secureConnections)
        {
            if (!ReferenceEquals(bound, connection)) continue;
            _logSink.Write($"[SecureJoin] 보호 채널 종료로 참가 연결 정리: clientId={clientId}");
            _ = _tcpServer.DisconnectClientAsync(clientId, "보호 채널 종료");
        }
    }

    /// <summary>
    /// 참가 승인된 연결에게만 패킷을 브로드캐스트합니다. TCP 연결만 하고 참가하지 않은 연결은 받지 않습니다.
    /// </summary>
    public async Task BroadcastPacketAsync(BasePacket packet)
    {
        ValidateOutboundPacket(packet);
        _logSink.Write($"[Packet] 브로드캐스트: 타입={packet.MessageType}, 길이={packet.DataLength}");
        await BroadcastToParticipantsAsync(packet);
    }

    /// <summary>
    /// 강의 데이터(채팅·화면·파일·시스템 메시지)는 참가 승인된 연결에만 보낸다.
    /// 연결 유지용 heartbeat만 TcpServerService.BroadcastAsync로 전체 연결에 나간다.
    /// </summary>
    private Task BroadcastToParticipantsAsync(BasePacket packet) =>
        _tcpServer.SendToClientsAsync(_clientDisplayNames.Keys.ToArray(), packet);

    /// <summary>
    /// 참가하지 않은 연결의 기능 요청이면 NotParticipant 오류를 보내고 true를 반환합니다.
    /// </summary>
    private async Task<bool> RejectIfNotParticipantAsync(string clientId, BasePacket packet, string feature)
    {
        if (_clientDisplayNames.ContainsKey(clientId))
            return false;

        _logSink.Write($"[{feature}] 비참가자 차단: clientId={clientId}");
        await _tcpServer.SendToClientAsync(clientId, CreateError(ErrorCodes.NotParticipant,
            "세션에 참여하지 않은 상태에서는 요청할 수 없습니다.", true, packet));
        return true;
    }

    public HeartbeatPacket CreateHeartbeat()
    {
        return PacketFactory.CreateHeartbeat(
            senderId: "Server",
            sessionId: CurrentSession?.SessionId);
    }

    /// <summary>
    /// 수신된 패킷을 MessageType별로 라우팅합니다.
    /// </summary>
    private async Task OnPacketReceivedAsync(string clientId, byte[] payload)
    {
        // 어떤 종류의 패킷이든 수신한 시점을 기록해 두면 HeartbeatService가
        // 응답 없는 클라이언트만 골라서 끊을 수 있습니다.
        _clientLastSeen[clientId] = DateTimeOffset.UtcNow;

        try
        {
            // BasePacket으로 먼저 역직렬화하여 MessageType 확인
            var basePacket = JsonSerializer.Deserialize<JsonElement>(payload);
            if (!basePacket.TryGetProperty("MessageType", out var messageTypeElement))
            {
                _logSink.Write($"[Packet] MessageType 누락: clientId={clientId}");
                return;
            }

            var messageType = (PacketType)messageTypeElement.GetInt32();
            PacketContractUtility.ValidatePacketType(messageType);

            switch (messageType)
            {
                case PacketType.SessionJoin:
                    var joinPacket = JsonSerializer.Deserialize<SessionJoinPacket>(payload);
                    if (joinPacket is not null)
                    {
                        var response = HandleJoin(clientId, joinPacket);
                        await _tcpServer.SendToClientAsync(clientId, response);

                        if (response is ErrorPacket)
                        {
                            await _tcpServer.DisconnectClientAsync(clientId, "참가 거부");
                        }
                        else
                        {
                            await BroadcastSystemMessageAsync($"{joinPacket.DisplayName}님이 세션에 참여했습니다.");
                        }
                    }
                    break;

                case PacketType.SessionLeave:
                    var leavePacket = JsonSerializer.Deserialize<SessionLeavePacket>(payload);
                    if (leavePacket is not null)
                    {
                        var leaveName = GetDisplayName(clientId, leavePacket.DisplayName);
                        var response = HandleLeave(clientId, leavePacket);
                        await _tcpServer.SendToClientAsync(clientId, response);

                        if (response is AckPacket && !string.IsNullOrWhiteSpace(leaveName))
                        {
                            // 정상 퇴장한 학생은 자동 재연결하지 않는다(U03).
                            RevokeReconnectGrant(clientId);
                            await RevokeControlIfDisconnectedAsync(clientId);
                            await RevokeRdpInvitationAsync(leaveName, RdpFailureReason.SessionClosed, notifyClientId: null);
                            await BroadcastSystemMessageAsync($"{leaveName}님이 세션에서 나갔습니다.");
                        }
                    }
                    break;

                case PacketType.RdpInvitationRequest:
                    var rdpRequest = JsonSerializer.Deserialize<RdpInvitationRequestPacket>(payload);
                    if (rdpRequest is not null)
                    {
                        await HandleRdpInvitationRequestAsync(clientId, rdpRequest);
                    }
                    break;

                case PacketType.Chat:
                    var chatPacket = JsonSerializer.Deserialize<ChatPacket>(payload);
                    if (chatPacket is not null)
                    {
                        await HandleChatAsync(clientId, chatPacket);
                    }
                    break;

                case PacketType.Screen:
                    // 화면 패킷은 참가자에게만 브로드캐스트
                    var screenPacket = JsonSerializer.Deserialize<ScreenPacket>(payload);
                    if (screenPacket is not null)
                    {
                        if (await RejectIfNotParticipantAsync(clientId, screenPacket, "Screen"))
                            break;
                        ScreenTransferUtility.ValidatePacketMetadata(screenPacket);
                        await BroadcastToParticipantsAsync(screenPacket);
                        _logSink.Write($"[Screen] 브로드캐스트: 프레임#{screenPacket.FrameIndex}");
                    }
                    break;

                case PacketType.File:
                    // 파일 패킷은 참가자에게만 브로드캐스트
                    var filePacket = JsonSerializer.Deserialize<FilePacket>(payload);
                    if (filePacket is not null)
                    {
                        if (await RejectIfNotParticipantAsync(clientId, filePacket, "File"))
                            break;
                        await BroadcastToParticipantsAsync(filePacket);
                        _logSink.Write($"[File] 브로드캐스트: {filePacket.FileName}");
                    }
                    break;

                case PacketType.Heartbeat:
                    // 클라이언트 하트비트 수신 — 연결 유지 확인용
                    break;

                default:
                    _logSink.Write($"[Packet] 미처리 타입: {messageType}, clientId={clientId}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logSink.Write($"[Packet] 처리 오류: clientId={clientId}, {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 클라이언트 연결 끊김 시 자동으로 세션 이탈을 처리합니다.
    /// </summary>
    private async Task OnClientDisconnectedAsync(string clientId, string reason)
    {
        _clientLastSeen.TryRemove(clientId, out _);

        // 참가 중이던 연결이 비정상으로 끊긴 경우에만 재연결 토큰을 쓸 수 있게 연다. 허용 상태는 끊기기 직전 값을 보관한다.
        ArmReconnectGrant(clientId);
        var displayName = RemoveParticipant(clientId);
        if (displayName is null)
            return;

        await RevokeControlIfDisconnectedAsync(clientId);
        await RevokeRdpInvitationAsync(displayName, RdpFailureReason.NetworkInterrupted, notifyClientId: null);

        // 세션 계층 로그에는 TCP 계층이 모르는 sessionId까지 남겨 추적성을 확보한다.
        _logSink.Write(
            $"[Session] 연결 끊김 이탈: {displayName} " +
            $"(clientId={clientId}, sessionId={CurrentSession?.SessionId}, 사유={reason})");
        await BroadcastSystemMessageAsync($"{displayName}님의 연결이 끊어졌습니다.");
    }

    private async Task HandleChatAsync(string clientId, ChatPacket chatPacket)
    {
        // 1) 참가자 검증
        if (!_clientDisplayNames.TryGetValue(clientId, out var verifiedName))
        {
            var errorPacket = CreateError(ErrorCodes.NotParticipant,
                "세션에 참여하지 않은 상태에서는 채팅을 보낼 수 없습니다.",
                true, chatPacket);
            await _tcpServer.SendToClientAsync(clientId, errorPacket);
            _logSink.Write($"[Chat] 비참가자 차단: clientId={clientId}");
            return;
        }

        // 2) 빈 메시지 검증
        if (string.IsNullOrWhiteSpace(chatPacket.Message))
        {
            _logSink.Write($"[Chat] 빈 메시지 무시: {verifiedName} (clientId={clientId})");
            return;
        }

        // 3) 메시지 길이 제한
        if (chatPacket.Message.Length > MaxChatMessageLength)
        {
            var errorPacket = CreateError(ErrorCodes.MessageTooLong,
                $"메시지는 {MaxChatMessageLength}자 이하여야 합니다. (현재 {chatPacket.Message.Length}자)",
                true, chatPacket);
            await _tcpServer.SendToClientAsync(clientId, errorPacket);
            _logSink.Write($"[Chat] 길이 초과: {verifiedName}, {chatPacket.Message.Length}자");
            return;
        }

        // 4) 송신자 이름을 서버 매핑 기준으로 강제 보정 (변조 방지)
        chatPacket.Sender = verifiedName;

        // 5) 브로드캐스트
        var targetCount = _participants.Count;
        await BroadcastToParticipantsAsync(chatPacket);

        ChatReceived?.Invoke(verifiedName, chatPacket.Message);
        _logSink.Write($"[Chat] 브로드캐스트: {verifiedName} → {targetCount}명");
    }

    private BasePacket HandleJoin(string clientId, SessionJoinPacket packet)
    {
        if (CurrentSession is null)
        {
            return CreateError(ErrorCodes.SessionNotOpen, "현재 열려 있는 세션이 없습니다.", false, packet);
        }

        if (string.IsNullOrWhiteSpace(packet.DisplayName))
        {
            return CreateError(ErrorCodes.DisplayNameRequired, "참여자 이름은 비워둘 수 없습니다.", true, packet);
        }

        var secureGate = _secureGate;
        if (secureGate is null && _roomPassword is not null)
        {
            // 보호 채널 없이 연 세션에는 비밀번호를 받을 경로가 없다.
            // 평문 v1 참가 요청으로 우회되지 않도록 비밀번호 방은 참가를 거부한다(fail-closed).
            _logSink.Write($"[Session] 비밀번호 방 참가 거부(보호 채널 미지원): clientId={clientId}");
            return CreateError(ErrorCodes.JoinRejected, "비밀번호가 설정된 방은 보호된 인증 연결이 준비된 뒤 참가할 수 있습니다.", false, packet);
        }

        if (_clientDisplayNames.TryGetValue(clientId, out var existingDisplayName))
        {
            _logSink.Write($"중복 참가 요청 차단: clientId={clientId}, 기존 이름={existingDisplayName}, 요청 이름={packet.DisplayName}");
            return CreateError(
                ErrorCodes.ClientAlreadyJoined,
                $"{existingDisplayName} 이름으로 이미 세션에 참여 중입니다.",
                true,
                packet);
        }

        SecureCollaborationConnection? secure = null;
        ReconnectRestore? restore = null;
        if (secureGate is not null)
        {
            // 티켓은 이름에 묶인 일회용이다. 실패해도 다시 쓸 수 없으므로 학생은 인증부터 다시 한다.
            var redemption = secureGate.Redeem(packet.JoinTicket, packet.DisplayName);
            secure = redemption?.Connection;
            restore = redemption?.ReconnectContext as ReconnectRestore;
            if (secure is null)
            {
                _logSink.Write($"[SecureJoin] 티켓 없음/만료/불일치로 참가 거부: clientId={clientId}");
                return CreateError(ErrorCodes.JoinRejected,
                    "보호 연결의 참가 승인이 확인되지 않았습니다. 같은 버전의 앱으로 세션에 다시 참가해 주세요.", true, packet);
            }
        }

        // 중복 참여 체크
        if (!TryAddParticipant(clientId, packet.DisplayName))
        {
            if (secure is not null) _ = secure.DisposeAsync();
            return CreateError(ErrorCodes.AlreadyJoined, $"{packet.DisplayName}은(는) 이미 참여 중입니다.", true, packet);
        }

        var participant = _participantRegistry.Join(clientId, CurrentSession.SessionId, packet.DisplayName, ParticipantRole.Student);
        if (restore is not null && (!restore.AllowViewing || !restore.AllowControl))
        {
            // 학생이 끊기기 전에 꺼 둔 허용은 재연결로 다시 켜지지 않게 복원한다(U07).
            _participantRegistry.SetPermissions(participant.ConnectionId, restore.AllowViewing, restore.AllowControl);
        }
        if (secure is not null)
        {
            _secureConnections[clientId] = secure;
            _ = IssueReconnectGrantAsync(clientId, packet.DisplayName, secure);
            // 참가 직후 학생이 기본 허용 상태(보기·제어 ON)를 바로 표시할 수 있게 한다(U07).
            _ = PushStatusThenAttachAnnotationsAsync(clientId, participant, secure);
            _ = AttachFileRoutingAsync(clientId, participant, secure);
            // 등록 직전에 닫혔다면 닫힘 알림이 이 참가자를 찾지 못했으므로 여기서 정리한다.
            if (secure.IsClosed) _ = _tcpServer.DisconnectClientAsync(clientId, "보호 채널 종료");
        }

        _logSink.Write($"[Session] {(restore is null ? "참여" : "재연결")}: {packet.DisplayName}, 현재 인원={CurrentSession.ParticipantCount}");

        return PacketFactory.CreateAck(
            senderId: "Server",
            ackCode: AckCodes.SessionJoined,
            message: $"{packet.DisplayName}님이 세션에 참여했습니다.",
            sessionId: CurrentSession.SessionId,
            correlationId: packet.CorrelationId);
    }

    private BasePacket HandleLeave(string clientId, SessionLeavePacket packet)
    {
        if (CurrentSession is null)
        {
            return CreateError(ErrorCodes.SessionNotOpen, "현재 열려 있는 세션이 없습니다.", false, packet);
        }

        if (!_clientDisplayNames.TryGetValue(clientId, out var displayName))
        {
            _logSink.Write($"비참가자 이탈 요청 차단: clientId={clientId}, 요청 이름={packet.DisplayName}");
            return CreateError(
                ErrorCodes.NotParticipant,
                "세션에 참여하지 않은 상태에서는 이탈 요청을 보낼 수 없습니다.",
                true,
                packet);
        }

        if (!string.IsNullOrWhiteSpace(packet.DisplayName) &&
            !string.Equals(packet.DisplayName, displayName, StringComparison.Ordinal))
        {
            _logSink.Write($"이탈 요청 이름 불일치: clientId={clientId}, 매핑 이름={displayName}, 요청 이름={packet.DisplayName}");
            return CreateError(
                ErrorCodes.NotParticipant,
                "현재 연결의 참여자 이름과 이탈 요청 정보가 일치하지 않습니다.",
                true,
                packet);
        }

        displayName = RemoveParticipant(clientId);
        _logSink.Write($"[Session] 이탈: {displayName ?? "(unknown)"}, 현재 인원={CurrentSession.ParticipantCount}");

        return PacketFactory.CreateAck(
            senderId: "Server",
            ackCode: AckCodes.SessionLeft,
            message: "세션 이탈이 처리되었습니다.",
            sessionId: CurrentSession.SessionId,
            correlationId: packet.CorrelationId);
    }

    /// <summary>
    /// 강의 세션 참가 자격을 검증한 뒤 요청한 학생 한 명에게만 RDP 초대를 발급합니다.
    /// 신원은 패킷 주장값이 아니라 참가 승인된 연결(_clientDisplayNames)에서 얻습니다.
    /// </summary>
    private async Task HandleRdpInvitationRequestAsync(string clientId, RdpInvitationRequestPacket request)
    {
        if (CurrentSession is null)
        {
            await _tcpServer.SendToClientAsync(clientId,
                CreateError(ErrorCodes.SessionNotOpen, "현재 열려 있는 세션이 없습니다.", false, request));
            return;
        }

        // 초대 생성은 await로 시간이 걸릴 수 있어(3번 구현/COM 호출), 그 사이 세션 종료나
        // 본인 이탈이 끼어들 수 있다. 생성 완료 후 이 스냅샷과 비교해 경합을 감지한다.
        var sessionAtRequest = CurrentSession;
        if (sessionAtRequest is null) return;

        if (!_clientDisplayNames.TryGetValue(clientId, out var participantId))
        {
            await _tcpServer.SendToClientAsync(clientId,
                CreateError(ErrorCodes.NotParticipant, "세션에 참여하지 않은 상태에서는 RDP 초대를 요청할 수 없습니다.", true, request));
            _logSink.Write($"[Rdp] 비참가자 초대 요청 차단: clientId={clientId}");
            return;
        }

        try
        {
            RdpInvitationContract.ValidateRequest(request, sessionAtRequest.SessionId, participantId);
        }
        catch (ArgumentException ex)
        {
            await _tcpServer.SendToClientAsync(clientId,
                CreateError(ErrorCodes.NotParticipant, ex.Message, true, request));
            _logSink.Write($"[Rdp] 초대 요청 검증 실패: participant={participantId}, {ex.Message}");
            return;
        }

        IRdpSharingService? sharingService;
        Guid sharingId;
        lock (_sessionLock)
        {
            sharingService = _rdpSharingService;
            sharingId = _rdpSharingId;
            // 발급 중인 요청도 공유 중지·재시작 때 복귀 대상으로 남긴다.
            // 성공 시 아래에서 제거하며 퇴장/철회/종료는 기존 정리 경로가 제거한다.
            // 공유 연결과 같은 잠금으로 등록해 재시작 알림과의 경합을 막는다.
            TryAddScreenWaiter(clientId);
        }
        if (sharingService is null)
        {
            await _tcpServer.SendToClientAsync(clientId,
                CreateError(ErrorCodes.RdpSharingNotStarted, "현재 화면 공유가 시작되지 않았습니다.", true, request));
            _logSink.Write($"[Rdp] 공유 미시작 상태에서 초대 요청 거부: participant={participantId}");
            return;
        }

        // 이미 발급된 초대/연결이 있으면 정리한 뒤 재발급한다 (재접속 등).
        await RevokeRdpInvitationAsync(participantId, RdpFailureReason.SessionClosed, notifyClientId: null);

        var invitationPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var expiresAt = DateTimeOffset.UtcNow + RdpInvitationContract.InvitationAcceptanceLifetime;

        RdpInvitationPacket invitation;
        try
        {
            invitation = await sharingService.CreateInvitationAsync(
                sessionAtRequest.SessionId, sharingId, participantId, request.ConnectionId,
                invitationPassword, expiresAt);
        }
        catch (Exception ex)
        {
            await _tcpServer.SendToClientAsync(clientId,
                CreateError(ErrorCodes.RdpInvitationFailed, "RDP 초대를 생성하지 못했습니다.", true, request));
            _logSink.Write($"[Rdp] 초대 생성 실패: participant={participantId}, {ex.GetType().Name}");
            return;
        }

        // 초대 발급 중(위 await) 세션 종료·공유 해제·본인 이탈이 끼어들었는지 재확인한다.
        // 그렇지 않으면 종료 이후에 뒤늦게 도착한 초대가 아무도 정리하지 않는 채로 남는다.
        var handoff = new RdpInvitationHandoff(
            participantId, request.ConnectionId, invitation.InvitationId, invitationPassword, expiresAt);
        bool isStaleAfterCreation;
        lock (_sessionLock)
        {
            isStaleAfterCreation = CurrentSession?.SessionId != sessionAtRequest.SessionId ||
                !ReferenceEquals(_rdpSharingService, sharingService) || _rdpSharingId != sharingId ||
                !_clientDisplayNames.TryGetValue(clientId, out var name) || name != participantId;
            if (!isStaleAfterCreation)
            {
                _screenWaiters.TryRemove(clientId, out _);
                _invitationOwners[invitation.InvitationId] = sharingService;
                _pendingInvitationHandoffs[participantId] = handoff;
                _rdpInvitations[participantId] = invitation;
            }
        }

        if (isStaleAfterCreation)
        {
            // 교체된 서비스나 같은 이름의 새 참가자를 건드리지 않는다.
            await sharingService.RevokeInvitationAsync(invitation.InvitationId);
            _logSink.Write(
                $"[Rdp] 초대 발급-이탈/종료 경합 감지, 즉시 폐기: participant={participantId}, invitation={invitation.InvitationId}");
            return;
        }

        RdpInvitationPasswordReady?.Invoke(handoff);

        // 비밀번호는 평문 TCP가 아니라 요청한 학생의 보호 채널로만 보낸다(U03 수동 입력 제거).
        // 보호 채널 없이 연 세션은 기존처럼 교수자 화면의 별도 전달 경로만 남는다.
        if (_secureConnections.TryGetValue(clientId, out var secure))
        {
            var secret = new RdpInvitationSecretNotice(sessionAtRequest.SessionId, invitation.InvitationId,
                request.ConnectionId, invitationPassword, expiresAt);
            await SendSecureAsync(secure, CollaborationMessageCodec.Encode(Guid.NewGuid(), secret), clientId);
        }

        // 요청한 학생에게만 개별 전송한다 — 브로드캐스트 금지.
        await _tcpServer.SendToClientAsync(clientId, invitation);
        _logSink.Write($"[Rdp] 초대 발급: participant={participantId}, connectionId={request.ConnectionId}");
    }

    /// <summary>
    /// 참가자 한 명의 활성 RDP 초대를 폐기하고 연결 정보를 정리합니다.
    /// notifyClientId가 있으면 해당 연결에 폐기 패킷을 보냅니다(이탈/연결 종료 당사자에게는 보낼 필요가 없습니다).
    /// </summary>
    private async Task RevokeRdpInvitationAsync(string participantId, RdpFailureReason reason, string? notifyClientId)
    {
        RdpInvitationPacket invitation;
        bool withdrawn;
        lock (_sessionLock)
        {
            if (!_rdpInvitations.TryRemove(participantId, out invitation!)) return;
            withdrawn = _pendingInvitationHandoffs.TryRemove(participantId, out _);
        }
        if (withdrawn)
            RdpInvitationPasswordWithdrawn?.Invoke(participantId);

        if (_invitationOwners.TryRemove(invitation.InvitationId, out var owner))
        {
            try
            {
                await owner.RevokeInvitationAsync(invitation.InvitationId);
            }
            catch (Exception ex)
            {
                _logSink.Write($"[Rdp] 초대 폐기 실패: participant={participantId}, {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (notifyClientId is not null)
        {
            var revoked = PacketFactory.CreateRdpInvitationRevoked(
                senderId: "Server",
                sessionId: CurrentSession?.SessionId ?? invitation.SessionId ?? Guid.Empty,
                participantId: participantId,
                invitationId: invitation.InvitationId,
                connectionId: invitation.ConnectionId,
                reason: reason);
            await _tcpServer.SendToClientAsync(notifyClientId, revoked);
        }

        _logSink.Write($"[Rdp] 초대 정리: participant={participantId}, 사유={reason}");
    }

    private async Task RevokeAllRdpInvitationsAsync(RdpFailureReason reason)
    {
        foreach (var participantId in _rdpInvitations.Keys.ToList())
        {
            _participants.TryGetValue(participantId, out var clientId);
            await RevokeRdpInvitationAsync(participantId, reason, clientId);
        }
    }

    /// <summary>
    /// 새 초대 발급과 새 제어 요청을 막고, 이 공유에서 진행 중인 제어 요청을 취소합니다.
    /// </summary>
    private void EndSharingLifetime()
    {
        CancellationTokenSource? lifetime;
        lock (_sessionLock)
        {
            _rdpSharingService = null;
            _rdpSharingId = Guid.Empty;
            _reverseRouter?.EndAnnotationSharing();
            lifetime = _sharingLifetime;
            _sharingLifetime = null;
        }
        // 진행 중인 요청이 토큰을 계속 참조할 수 있어 Dispose하지 않는다(타이머 없는 CTS라 누수 없음).
        lifetime?.Cancel();
    }

    /// <summary>
    /// 현재 참가 중인 학생만 자동 복귀 대기에 넣습니다. 연결은 대기 시점 것을 기록해
    /// 재접속으로 교체된 연결에는 알림을 보내지 않게 합니다.
    /// </summary>
    private void TryAddScreenWaiter(string clientId)
    {
        var connection = _participantRegistry.TryGetConnection(clientId);
        if (connection is null) return;
        if (_participantRegistry.TryResolve(connection.ConnectionId) is not { Connected: true }) return;
        _screenWaiters[clientId] = connection;
    }

    /// <summary>
    /// 공유가 붙은 뒤 대기 중인 학생에게 초대 재요청 알림을 보냅니다. 초대는 학생이 새 연결 ID로 요청할 때 발급하며,
    /// 이 알림 자체는 초대·비밀번호를 담지 않습니다. 보내는 도중 공유가 다시 멈추면 남은 학생은 대기로 둡니다.
    /// </summary>
    private async Task ResumeScreenWaitersAsync(IRdpSharingService sharingService)
    {
        foreach (var (clientId, waitedConnection) in _screenWaiters.ToArray())
        {
            Guid sessionId;
            lock (_sessionLock)
            {
                if (!ReferenceEquals(_rdpSharingService, sharingService) || CurrentSession is null) return;
                sessionId = CurrentSession.SessionId;
            }
            if (!_screenWaiters.TryRemove(new KeyValuePair<string, ParticipantConnection>(clientId, waitedConnection)))
                continue;

            var current = _participantRegistry.TryResolve(waitedConnection.ConnectionId);
            if (current is not { Connected: true } ||
                _participantRegistry.TryGetConnection(clientId) != waitedConnection)
            {
                _logSink.Write($"[Rdp] 화면 복귀 대상 제외(퇴장/교체): clientId={clientId}");
                continue;
            }

            await _tcpServer.SendToClientAsync(clientId, PacketFactory.CreateAck(
                senderId: "Server",
                ackCode: AckCodes.RdpSharingStarted,
                message: "화면 공유가 시작되었습니다.",
                sessionId: sessionId));
            _logSink.Write($"[Rdp] 화면 복귀 알림: clientId={clientId}");
        }
    }

    /// <summary>
    /// 공유 중지/세션 종료 정리 중 원격 제어를 회수합니다. 회수 실패가 뒤따르는 초대·세션 정리를
    /// 막지 않도록 예외 대신 결과로 돌려줍니다. 승인 상태는 어느 경우든 이미 Revoked입니다.
    /// </summary>
    private async Task<RemoteInputRevokeStatus> StopControlForTeardownAsync(string reason)
    {
        var coordinator = _controlCoordinator;
        if (coordinator is null) return RemoteInputRevokeStatus.Confirmed;

        try
        {
            await coordinator.StopAsync();
        }
        catch (Exception ex)
        {
            _logSink.Write($"[Control] {reason} 중 입력 회수 확인 실패: {ex.GetType().Name}");
            return RemoteInputRevokeStatus.Failed;
        }

        // StopAsync는 취소를 무시한 허용 작업이 남아도 최초 회수 후 반환하므로, 반환만으로 차단 완료로 보지 않는다.
        if (coordinator.IsInputRevokePending)
        {
            _logSink.Write($"[Control] {reason}: 허용 작업 종료 대기, 입력 차단 확인 미완료");
            return RemoteInputRevokeStatus.Pending;
        }
        return RemoteInputRevokeStatus.Confirmed;
    }

    private string? GetDisplayName(string clientId, string? packetDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(packetDisplayName))
            return packetDisplayName;

        _clientDisplayNames.TryGetValue(clientId, out var name);
        return name;
    }

    private bool TryAddParticipant(string clientId, string displayName)
    {
        if (!_participants.TryAdd(displayName, clientId))
            return false;

        _clientDisplayNames.TryAdd(clientId, displayName);
        UpdateParticipantCount();
        ParticipantsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 레지스트리에서 연결을 지웁니다. 그 연결이 제어 대상이었다면 조정자가 ConnectionRemoved로
    /// 승인을 회수하고, 여기서는 실제 입력 차단 확인까지 기다립니다.
    /// </summary>
    private async Task RevokeControlIfDisconnectedAsync(string clientId)
    {
        if (_participantRegistry.Disconnect(clientId) is null) return;
        await ConfirmControlInputRevokedAsync();
    }

    private async Task ConfirmControlInputRevokedAsync()
    {
        var coordinator = _controlCoordinator;
        if (coordinator is null) return;
        try
        {
            await coordinator.ConfirmInputRevokedAsync();
        }
        catch (Exception ex)
        {
            // 확인 실패는 대기열에 남아 다음 요청/중지 때 재시도된다. 수신 루프는 계속 돈다.
            _logSink.Write($"[Control] 입력 회수 확인 실패: {ex.GetType().Name}");
        }
    }

    private string? RemoveParticipant(string clientId)
    {
        string? displayName;
        lock (_sessionLock)
        {
            if (!_clientDisplayNames.TryRemove(clientId, out displayName)) return null;
            _participants.TryRemove(displayName, out _);
            // 실제 퇴장·연결 끊김 후에는 자동 복귀하지 않는다. 다시 오면 새 참가로 처리한다(U03).
            _screenWaiters.TryRemove(clientId, out _);
            if (_secureConnections.TryRemove(clientId, out var secure)) _ = secure.DisposeAsync();
        }
        UpdateParticipantCount();
        ParticipantsChanged?.Invoke();
        return displayName;
    }

    private void ClearParticipants()
    {
        _participants.Clear();
        _clientDisplayNames.Clear();
        _clientLastSeen.Clear();
        _screenWaiters.Clear();
        UpdateParticipantCount();
        ParticipantsChanged?.Invoke();
    }

    private void UpdateParticipantCount()
    {
        if (CurrentSession is not null)
            CurrentSession.ParticipantCount = _participants.Count;
    }

    private async Task BroadcastSystemMessageAsync(string message)
    {
        var systemChat = PacketFactory.CreateSystemChat(
            message: message,
            sessionId: CurrentSession?.SessionId);

        systemChat.SenderId = "Server";

        await BroadcastToParticipantsAsync(systemChat);
        ChatReceived?.Invoke("System", message);
        _logSink.Write($"[Chat] 시스템 브로드캐스트: {message}");
    }

    private static ErrorPacket CreateError(string errorCode, string message, bool isRecoverable, BasePacket requestPacket)
    {
        return PacketFactory.CreateError(
            senderId: "Server",
            errorCode: errorCode,
            message: message,
            isRecoverable: isRecoverable,
            sessionId: requestPacket.SessionId,
            correlationId: requestPacket.CorrelationId);
    }

    private static void ValidateOutboundPacket(BasePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet is FilePacket filePacket)
        {
            FileTransferUtility.ValidatePacketMetadata(filePacket);
        }
    }
}
