using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using EduStream.Core.Collaboration;
using EduStream.Core.Factories;
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

    private readonly ILogSink _logSink;
    private readonly TcpServerService _tcpServer;
    private readonly ConcurrentDictionary<string, string> _participants = new(); // displayName → clientId
    private readonly ConcurrentDictionary<string, string> _clientDisplayNames = new(); // clientId → displayName
    private readonly ConcurrentDictionary<string, DateTimeOffset> _clientLastSeen = new(); // clientId → 마지막 수신 시각
    private readonly ConcurrentDictionary<string, RdpInvitationPacket> _rdpInvitations = new(); // participantId(displayName) → 발급된 초대
    private readonly ConcurrentDictionary<Guid, IRdpSharingService> _invitationOwners = new();
    private readonly ConcurrentDictionary<string, RdpInvitationHandoff> _pendingInvitationHandoffs = new(); // participantId → 인계 대기 중인 비밀번호
    private readonly ParticipantRegistry _participantRegistry = new();
    private readonly object _sessionLock = new();
    private IRdpSharingService? _rdpSharingService;
    private Guid _rdpSharingId;
    private ParticipantConnection? _professorConnection;
    private ServerRemoteControlCoordinator? _controlCoordinator;
    private IRemoteInputGate _remoteInputGate = UnavailableRemoteInputGate.Instance;

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
        }
        _logSink.Write($"[Rdp] 공유 서비스 연결: sharingId={sharingId}");
    }

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

        await _controlCoordinator.RequestAsync(target);

        var state = _controlCoordinator.Current;
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
        var connection = _participantRegistry.TryGetConnection(clientId);
        if (connection is null || !_participantRegistry.SetPermissions(connection.ConnectionId, allowViewing, allowControl))
            return false;

        _logSink.Write($"[Control] 허용 변경: 대상={displayName}, 보기={allowViewing}, 제어={allowViewing && allowControl}");
        await ConfirmControlInputRevokedAsync();
        return true;
    }

    /// <summary>
    /// 화면 공유가 종료될 때 팀장이 호출합니다. 남아 있는 초대를 모두 정리합니다.
    /// </summary>
    public async Task DetachRdpSharingAsync()
    {
        lock (_sessionLock)
        {
            _rdpSharingService = null;
            _rdpSharingId = Guid.Empty;
        }
        await RevokeAllRdpInvitationsAsync(RdpFailureReason.SessionClosed);
        _logSink.Write("[Rdp] 공유 서비스 연결 해제");
    }

    public Task<SessionInfo> OpenSessionAsync(string sessionName, int port)
    {
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
        }

        _tcpServer.Start(port);
        _logSink.Write($"[Session] 개설: 이름={sessionName}, 포트={port}");
        return Task.FromResult(CurrentSession);
    }

    public async Task CloseSessionAsync()
    {
        // 종료 정리 전에 새 초대 발급을 막는다. 기존 초대는 발급한 서비스로 회수한다.
        lock (_sessionLock)
        {
            _rdpSharingService = null;
            _rdpSharingId = Guid.Empty;
        }
        if (_controlCoordinator is not null)
        {
            try
            {
                await _controlCoordinator.StopAsync();
            }
            catch (Exception ex)
            {
                // 입력 회수 확인 실패가 세션 종료 자체를 막지 않게 한다. 승인 상태는 이미 Revoked다.
                _logSink.Write($"[Control] 종료 중 입력 회수 확인 실패: {ex.GetType().Name}");
            }
        }
        // 연결 정리 전에 클라이언트들에게 세션 종료 알림
        if (_participants.Count > 0)
        {
            await BroadcastSystemMessageAsync("교수자가 세션을 종료했습니다. 연결이 해제됩니다.");
        }

        await RevokeAllRdpInvitationsAsync(RdpFailureReason.SessionClosed);

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
        }

        ClearParticipants();
        _participantRegistry.Clear();
        await _tcpServer.StopAsync();
    }

    /// <summary>
    /// 모든 연결된 클라이언트에게 패킷을 브로드캐스트합니다.
    /// </summary>
    public async Task BroadcastPacketAsync(BasePacket packet)
    {
        ValidateOutboundPacket(packet);
        _logSink.Write($"[Packet] 브로드캐스트: 타입={packet.MessageType}, 길이={packet.DataLength}");
        await _tcpServer.BroadcastAsync(packet);
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
                    // 화면 패킷은 모든 클라이언트에게 브로드캐스트
                    var screenPacket = JsonSerializer.Deserialize<ScreenPacket>(payload);
                    if (screenPacket is not null)
                    {
                        ScreenTransferUtility.ValidatePacketMetadata(screenPacket);
                        await _tcpServer.BroadcastAsync(screenPacket);
                        _logSink.Write($"[Screen] 브로드캐스트: 프레임#{screenPacket.FrameIndex}");
                    }
                    break;

                case PacketType.File:
                    // 파일 패킷은 모든 클라이언트에게 브로드캐스트
                    var filePacket = JsonSerializer.Deserialize<FilePacket>(payload);
                    if (filePacket is not null)
                    {
                        await _tcpServer.BroadcastAsync(filePacket);
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
        await _tcpServer.BroadcastAsync(chatPacket);

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

        if (_clientDisplayNames.TryGetValue(clientId, out var existingDisplayName))
        {
            _logSink.Write($"중복 참가 요청 차단: clientId={clientId}, 기존 이름={existingDisplayName}, 요청 이름={packet.DisplayName}");
            return CreateError(
                ErrorCodes.ClientAlreadyJoined,
                $"{existingDisplayName} 이름으로 이미 세션에 참여 중입니다.",
                true,
                packet);
        }

        // 중복 참여 체크
        if (!TryAddParticipant(clientId, packet.DisplayName))
        {
            return CreateError(ErrorCodes.AlreadyJoined, $"{packet.DisplayName}은(는) 이미 참여 중입니다.", true, packet);
        }

        _participantRegistry.Join(clientId, CurrentSession.SessionId, packet.DisplayName, ParticipantRole.Student);

        _logSink.Write($"[Session] 참여: {packet.DisplayName}, 현재 인원={CurrentSession.ParticipantCount}");

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

        var sharingService = _rdpSharingService;
        var sharingId = _rdpSharingId;
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

        await _tcpServer.BroadcastAsync(systemChat);
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
