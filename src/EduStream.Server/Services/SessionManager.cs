using System.Collections.Concurrent;
using System.Text.Json;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.Server.Services;

/// <summary>
/// 세션 개설/종료, 참여자 관리, 패킷 라우팅을 담당합니다.
/// TcpServerService를 통해 실제 네트워크 전송을 수행합니다.
/// </summary>
public sealed class SessionManager
{
    private readonly ILogSink _logSink;
    private readonly TcpServerService _tcpServer;
    private readonly ConcurrentDictionary<string, string> _participants = new(); // displayName → clientId
    private readonly ConcurrentDictionary<string, string> _clientDisplayNames = new(); // clientId → displayName
    private readonly object _sessionLock = new();

    /// <summary>
    /// 참여자 목록이 변경되었을 때 발생합니다.
    /// </summary>
    public event Action? ParticipantsChanged;

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

    public Task<SessionInfo> OpenSessionAsync(string sessionName, int port)
    {
        lock (_sessionLock)
        {
            CurrentSession = new SessionInfo
            {
                SessionName = sessionName,
                HostName = Environment.MachineName,
                Port = port,
                HostAddress = "127.0.0.1"
            };
        }

        _tcpServer.Start(port);
        _logSink.Write($"세션을 개설했습니다. 이름={sessionName}, 포트={port}");
        return Task.FromResult(CurrentSession);
    }

    public async Task CloseSessionAsync()
    {
        lock (_sessionLock)
        {
            if (CurrentSession is not null)
            {
                _logSink.Write($"세션을 종료했습니다. 이름={CurrentSession.SessionName}");
            }
            CurrentSession = null;
        }

        _participants.Clear();
        _clientDisplayNames.Clear();
        await _tcpServer.StopAsync();
        ParticipantsChanged?.Invoke();
    }

    /// <summary>
    /// 모든 연결된 클라이언트에게 패킷을 브로드캐스트합니다.
    /// </summary>
    public async Task BroadcastPacketAsync(BasePacket packet)
    {
        _logSink.Write($"패킷 브로드캐스트: {packet.MessageType}, 길이={packet.DataLength}");
        await _tcpServer.BroadcastAsync(packet);
    }

    public HeartbeatPacket CreateHeartbeat()
    {
        return new HeartbeatPacket
        {
            SessionId = CurrentSession?.SessionId,
            SenderId = "Server"
        };
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
                _logSink.Write($"MessageType 없는 패킷 수신: clientId={clientId}");
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
                    }
                    break;

                case PacketType.SessionLeave:
                    var leavePacket = JsonSerializer.Deserialize<SessionLeavePacket>(payload);
                    if (leavePacket is not null)
                    {
                        var response = HandleLeave(clientId, leavePacket);
                        await _tcpServer.SendToClientAsync(clientId, response);
                    }
                    break;

                case PacketType.Chat:
                    // 채팅은 모든 클라이언트에게 브로드캐스트
                    var chatPacket = JsonSerializer.Deserialize<ChatPacket>(payload);
                    if (chatPacket is not null)
                    {
                        await _tcpServer.BroadcastAsync(chatPacket);
                        _logSink.Write($"채팅 브로드캐스트: {chatPacket.SenderId}");
                    }
                    break;

                case PacketType.Heartbeat:
                    // 클라이언트 하트비트 수신 — 연결 유지 확인용
                    break;

                default:
                    _logSink.Write($"처리되지 않은 패킷 타입: {messageType}, clientId={clientId}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logSink.Write($"패킷 처리 오류: clientId={clientId}, {ex.Message}");
        }
    }

    /// <summary>
    /// 클라이언트 연결 끊김 시 자동으로 세션 이탈을 처리합니다.
    /// </summary>
    private async Task OnClientDisconnectedAsync(string clientId)
    {
        if (_clientDisplayNames.TryRemove(clientId, out var displayName))
        {
            _participants.TryRemove(displayName, out _);

            if (CurrentSession is not null)
            {
                CurrentSession.ParticipantCount = _participants.Count;
            }

            _logSink.Write($"연결 끊김으로 세션 이탈: {displayName} (clientId={clientId})");
            ParticipantsChanged?.Invoke();
        }

        await Task.CompletedTask;
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
        if (!_participants.TryAdd(packet.DisplayName, clientId))
        {
            return CreateError(ErrorCodes.AlreadyJoined, $"{packet.DisplayName}은(는) 이미 참여 중입니다.", true, packet);
        }

        _clientDisplayNames.TryAdd(clientId, packet.DisplayName);
        CurrentSession.ParticipantCount = _participants.Count;

        _logSink.Write($"세션 참여 처리: {packet.DisplayName}, 현재 인원={CurrentSession.ParticipantCount}");
        ParticipantsChanged?.Invoke();

        return new AckPacket
        {
            SessionId = CurrentSession.SessionId,
            SenderId = "Server",
            AckCode = AckCodes.SessionJoined,
            Message = $"{packet.DisplayName}님이 세션에 참여했습니다."
        };
    }

    private BasePacket HandleLeave(string clientId, SessionLeavePacket packet)
    {
        if (CurrentSession is null)
        {
            return CreateError(ErrorCodes.SessionNotOpen, "현재 열려 있는 세션이 없습니다.", false, packet);
        }

        var displayName = packet.DisplayName;

        // DisplayName이 비어있으면 clientId로 조회
        if (string.IsNullOrWhiteSpace(displayName))
        {
            _clientDisplayNames.TryGetValue(clientId, out displayName);
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            _participants.TryRemove(displayName, out _);
            _clientDisplayNames.TryRemove(clientId, out _);
        }

        CurrentSession.ParticipantCount = _participants.Count;
        _logSink.Write($"세션 이탈 처리: {displayName}, 현재 인원={CurrentSession.ParticipantCount}");
        ParticipantsChanged?.Invoke();

        return new AckPacket
        {
            SessionId = CurrentSession.SessionId,
            SenderId = "Server",
            AckCode = AckCodes.SessionLeft,
            Message = "세션 이탈이 처리되었습니다."
        };
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
