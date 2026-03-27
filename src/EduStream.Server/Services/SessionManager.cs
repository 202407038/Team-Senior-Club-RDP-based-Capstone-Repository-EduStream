using System.Collections.Concurrent;
using System.Text.Json;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.Server.Services;

/// <summary>
/// 세션 개설/종료와 참가자 관리, 그리고 서버 측 패킷 라우팅을 담당합니다.
/// 실제 네트워크 송수신은 TcpServerService를 통해 수행합니다.
/// </summary>
public sealed class SessionManager
{
    private readonly ILogSink _logSink;
    private readonly TcpServerService _tcpServer;
    private readonly ConcurrentDictionary<string, string> _participants = new();

    // clientId -> DisplayName 매핑. 연결이 끊겼을 때 자동 이탈 처리에 사용합니다.
    private readonly ConcurrentDictionary<string, string> _clientDisplayNames = new();
    private readonly object _sessionLock = new();

    public SessionManager(TcpServerService tcpServer, ILogSink logSink)
    {
        _tcpServer = tcpServer;
        _logSink = logSink;

        _tcpServer.PacketReceived += OnPacketReceivedAsync;
        _tcpServer.ClientDisconnected += OnClientDisconnectedAsync;
    }

    /// <summary>
    /// 참여자 목록이 변경되었을 때 UI가 즉시 반영할 수 있도록 알립니다.
    /// </summary>
    public event Action? ParticipantsChanged;

    public SessionInfo? CurrentSession { get; private set; }

    public bool IsSessionOpen => CurrentSession is not null;

    public int ParticipantCount => _participants.Count;

    public IReadOnlyCollection<string> ParticipantNames => _participants.Keys.ToList().AsReadOnly();

    public async Task<SessionInfo> OpenSessionAsync(string sessionName, int port)
    {
        SessionInfo openedSession;

        lock (_sessionLock)
        {
            CurrentSession = new SessionInfo
            {
                SessionName = sessionName,
                HostName = Environment.MachineName,
                Port = port,
                HostAddress = "0.0.0.0"
            };

            openedSession = CurrentSession;
        }

        await _tcpServer.StartAsync(port);
        _logSink.Write($"세션을 개설했습니다. 이름={sessionName}, 포트={port}");
        return openedSession;
    }

    public async Task CloseSessionAsync()
    {
        string? sessionName = null;

        lock (_sessionLock)
        {
            if (CurrentSession is not null)
            {
                sessionName = CurrentSession.SessionName;
            }

            _participants.Clear();
            _clientDisplayNames.Clear();
            CurrentSession = null;
        }

        await _tcpServer.StopAsync();

        if (!string.IsNullOrWhiteSpace(sessionName))
        {
            _logSink.Write($"세션을 종료했습니다. 이름={sessionName}");
        }

        ParticipantsChanged?.Invoke();
    }

    public async Task BroadcastPacketAsync(BasePacket packet)
    {
        SessionInfo? currentSession;

        lock (_sessionLock)
        {
            currentSession = CurrentSession;
        }

        if (currentSession is null)
        {
            _logSink.Write("브로드캐스트 실패: 세션이 열려 있지 않습니다.");
            return;
        }

        packet.SessionId = currentSession.SessionId;
        packet.SenderId = "Server";

        _logSink.Write($"패킷 브로드캐스트: {packet.MessageType}, 참여자={ParticipantCount}명");
        await _tcpServer.BroadcastAsync(packet);
    }

    public Task<BasePacket> HandleJoinAsync(SessionJoinPacket packet)
    {
        BasePacket response;
        string? logMessage = null;
        var notifyParticipantsChanged = false;

        lock (_sessionLock)
        {
            var currentSession = CurrentSession;
            if (currentSession is null)
            {
                response = CreateError(
                    ErrorCodes.SessionNotOpen,
                    "현재 열려 있는 세션이 없습니다.",
                    false,
                    packet);
            }
            else if (string.IsNullOrWhiteSpace(packet.DisplayName))
            {
                response = CreateError(
                    ErrorCodes.DisplayNameRequired,
                    "참여자 이름은 비워둘 수 없습니다.",
                    true,
                    packet);
            }
            else if (!_participants.TryAdd(packet.DisplayName, packet.SenderId))
            {
                response = CreateError(
                    ErrorCodes.AlreadyJoined,
                    $"{packet.DisplayName}은(는) 이미 참여 중입니다.",
                    true,
                    packet);
            }
            else
            {
                currentSession.ParticipantCount = _participants.Count;
                logMessage = $"세션 참여 처리: {packet.DisplayName}, 현재 인원={currentSession.ParticipantCount}";
                notifyParticipantsChanged = true;
                response = new AckPacket
                {
                    SessionId = currentSession.SessionId,
                    SenderId = "Server",
                    AckCode = AckCodes.SessionJoined,
                    Message = $"{packet.DisplayName}님이 세션에 참여했습니다."
                };
            }
        }

        if (logMessage is not null)
        {
            _logSink.Write(logMessage);
        }

        if (notifyParticipantsChanged)
        {
            ParticipantsChanged?.Invoke();
        }

        return Task.FromResult(response);
    }

    public Task<BasePacket> HandleLeaveAsync(SessionLeavePacket packet)
    {
        BasePacket response;
        string? logMessage = null;
        var notifyParticipantsChanged = false;

        lock (_sessionLock)
        {
            var currentSession = CurrentSession;
            if (currentSession is null)
            {
                response = CreateError(
                    ErrorCodes.SessionNotOpen,
                    "현재 열려 있는 세션이 없습니다.",
                    false,
                    packet);
            }
            else
            {
                // 새 호출부는 DisplayName을 채우고, 이전 호출부는 SenderId만 채울 수 있습니다.
                var participantKey = string.IsNullOrWhiteSpace(packet.DisplayName)
                    ? packet.SenderId
                    : packet.DisplayName;

                if (!string.IsNullOrWhiteSpace(participantKey))
                {
                    _participants.TryRemove(participantKey, out _);
                }

                currentSession.ParticipantCount = _participants.Count;
                logMessage = $"세션 이탈 처리: {participantKey}, 현재 인원={currentSession.ParticipantCount}";
                notifyParticipantsChanged = true;
                response = new AckPacket
                {
                    SessionId = currentSession.SessionId,
                    SenderId = "Server",
                    AckCode = AckCodes.SessionLeft,
                    Message = "세션 이탈을 처리했습니다."
                };
            }
        }

        if (logMessage is not null)
        {
            _logSink.Write(logMessage);
        }

        if (notifyParticipantsChanged)
        {
            ParticipantsChanged?.Invoke();
        }

        return Task.FromResult(response);
    }

    public HeartbeatPacket CreateHeartbeat()
    {
        lock (_sessionLock)
        {
            return new HeartbeatPacket
            {
                SessionId = CurrentSession?.SessionId,
                SenderId = "Server"
            };
        }
    }

    /// <summary>
    /// 클라이언트로부터 수신된 패킷을 타입별로 라우팅합니다.
    /// </summary>
    private async Task OnPacketReceivedAsync(string clientId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("MessageType", out var typeElement))
            {
                _logSink.Write($"패킷에 MessageType이 없습니다: clientId={clientId}");
                return;
            }

            var packetType = (PacketType)typeElement.GetInt32();
            BasePacket? response = null;

            switch (packetType)
            {
                case PacketType.SessionJoin:
                    var joinPacket = JsonSerializer.Deserialize<SessionJoinPacket>(json);
                    if (joinPacket is not null)
                    {
                        response = await HandleJoinAsync(joinPacket);
                        if (response is AckPacket)
                        {
                            _clientDisplayNames[clientId] = joinPacket.DisplayName;
                        }
                    }
                    break;

                case PacketType.SessionLeave:
                    var leavePacket = JsonSerializer.Deserialize<SessionLeavePacket>(json);
                    if (leavePacket is not null)
                    {
                        response = await HandleLeaveAsync(leavePacket);
                        _clientDisplayNames.TryRemove(clientId, out _);
                    }
                    break;

                case PacketType.Chat:
                    var chatPacket = JsonSerializer.Deserialize<ChatPacket>(json);
                    if (chatPacket is not null)
                    {
                        _logSink.Write($"채팅 수신: {chatPacket.Sender}: {chatPacket.Message}");
                        await _tcpServer.BroadcastAsync(chatPacket);
                    }
                    break;

                case PacketType.Heartbeat:
                    // 현재는 heartbeat 수신 여부만 확인하고 별도 응답은 하지 않습니다.
                    break;

                default:
                    _logSink.Write($"알 수 없는 패킷 타입: {packetType}, clientId={clientId}");
                    break;
            }

            if (response is not null)
            {
                await _tcpServer.SendToClientAsync(clientId, response);
            }
        }
        catch (Exception ex)
        {
            _logSink.Write($"패킷 처리 오류: clientId={clientId}, {ex.Message}");
        }
    }

    /// <summary>
    /// 연결이 끊긴 클라이언트를 자동으로 세션에서 제거합니다.
    /// </summary>
    private Task OnClientDisconnectedAsync(string clientId)
    {
        string? logMessage = null;
        var notifyParticipantsChanged = false;

        lock (_sessionLock)
        {
            if (_clientDisplayNames.TryRemove(clientId, out var displayName))
            {
                _participants.TryRemove(displayName, out _);

                if (CurrentSession is not null)
                {
                    CurrentSession.ParticipantCount = _participants.Count;
                    logMessage = $"연결 끊김으로 자동 이탈: {displayName}, 현재 인원={CurrentSession.ParticipantCount}";
                    notifyParticipantsChanged = true;
                }
            }
        }

        if (logMessage is not null)
        {
            _logSink.Write(logMessage);
        }

        if (notifyParticipantsChanged)
        {
            ParticipantsChanged?.Invoke();
        }

        return Task.CompletedTask;
    }

    private static ErrorPacket CreateError(string errorCode, string message, bool isRecoverable, BasePacket requestPacket)
    {
        return new ErrorPacket
        {
            SessionId = requestPacket.SessionId,
            SenderId = "Server",
            CorrelationId = requestPacket.CorrelationId,
            ErrorCode = errorCode,
            Message = message,
            IsRecoverable = isRecoverable
        };
    }
}
