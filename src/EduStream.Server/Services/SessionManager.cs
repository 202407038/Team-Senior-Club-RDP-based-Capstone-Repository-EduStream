using System.Collections.Concurrent;
using System.Text.Json;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
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
    private readonly object _sessionLock = new();

    /// <summary>
    /// 참여자 목록이 변경되었을 때 발생합니다.
    /// </summary>
    public event Action? ParticipantsChanged;

    /// <summary>
    /// 클라이언트로부터 채팅 메시지를 수신했을 때 발생합니다.
    /// (sender, message)
    /// </summary>
    public event Action<string, string>? ChatReceived;

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
    /// 현재 참여자 이름 목록을 반환합니다.
    /// </summary>
    public IReadOnlyCollection<string> ParticipantNames => _participants.Keys.ToList().AsReadOnly();

    public int ParticipantCount => _participants.Count;

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
        }

        _tcpServer.Start(port);
        _logSink.Write($"[Session] 개설: 이름={sessionName}, 포트={port}");
        return Task.FromResult(CurrentSession);
    }

    public async Task CloseSessionAsync()
    {
        // 연결 정리 전에 클라이언트들에게 세션 종료 알림
        if (_participants.Count > 0)
        {
            await BroadcastSystemMessageAsync("교수자가 세션을 종료했습니다. 연결이 해제됩니다.");
        }

        lock (_sessionLock)
        {
            if (CurrentSession is not null)
            {
                _logSink.Write($"[Session] 종료: 이름={CurrentSession.SessionName}");
            }
            CurrentSession = null;
        }

        ClearParticipants();
        await _tcpServer.StopAsync();
    }

    /// <summary>
    /// 모든 연결된 클라이언트에게 패킷을 브로드캐스트합니다.
    /// </summary>
    public async Task BroadcastPacketAsync(BasePacket packet)
    {
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
                            await _tcpServer.DisconnectClientAsync(clientId);
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
                            await BroadcastSystemMessageAsync($"{leaveName}님이 세션에서 나갔습니다.");
                        }
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
    private async Task OnClientDisconnectedAsync(string clientId)
    {
        var displayName = RemoveParticipant(clientId);
        if (displayName is null)
            return;

        _logSink.Write($"[Session] 연결 끊김 이탈: {displayName} (clientId={clientId})");
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

        // 중복 참여 체크
        if (!TryAddParticipant(clientId, packet.DisplayName))
        {
            return CreateError(ErrorCodes.AlreadyJoined, $"{packet.DisplayName}은(는) 이미 참여 중입니다.", true, packet);
        }

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

        var displayName = RemoveParticipant(clientId);
        _logSink.Write($"[Session] 이탈: {displayName ?? "(unknown)"}, 현재 인원={CurrentSession.ParticipantCount}");

        return PacketFactory.CreateAck(
            senderId: "Server",
            ackCode: AckCodes.SessionLeft,
            message: "세션 이탈이 처리되었습니다.",
            sessionId: CurrentSession.SessionId,
            correlationId: packet.CorrelationId);
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

    private string? RemoveParticipant(string clientId)
    {
        if (!_clientDisplayNames.TryRemove(clientId, out var displayName))
            return null;

        _participants.TryRemove(displayName, out _);
        UpdateParticipantCount();
        ParticipantsChanged?.Invoke();
        return displayName;
    }

    private void ClearParticipants()
    {
        _participants.Clear();
        _clientDisplayNames.Clear();
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
}
