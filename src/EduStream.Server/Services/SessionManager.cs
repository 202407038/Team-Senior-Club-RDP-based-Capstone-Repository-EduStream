using System.Collections.Concurrent;
using System.Text.Json;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.Server.Services;

/// <summary>
/// 세션 개설/종료와 패킷 브로드캐스트 진입점을 담당합니다.
/// TcpServerService를 통해 실제 네트워크 통신을 수행합니다.
/// </summary>
public sealed class SessionManager
{
    private readonly ILogSink _logSink;
    private readonly TcpServerService _tcpServer;
    private readonly ConcurrentDictionary<string, string> _participants = new();

    /// <summary>
    /// clientId → DisplayName 매핑 (네트워크 연결 해제 시 DisplayName 조회용)
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _clientDisplayNames = new();

    private readonly object _sessionLock = new();

    /// <summary>
    /// 참여자 목록이 변경되었을 때 발생합니다.
    /// </summary>
    public event Action? ParticipantsChanged;

    public SessionManager(TcpServerService tcpServer, ILogSink logSink)
    {
        _tcpServer = tcpServer;
        _logSink = logSink;

        _tcpServer.PacketReceived += OnPacketReceivedAsync;
        _tcpServer.ClientDisconnected += OnClientDisconnectedAsync;
    }

    public SessionInfo? CurrentSession { get; private set; }

    public bool IsSessionOpen => CurrentSession is not null;

    public int ParticipantCount => _participants.Count;

    public IReadOnlyCollection<string> ParticipantNames => _participants.Keys.ToList().AsReadOnly();

    public async Task<SessionInfo> OpenSessionAsync(string sessionName, int port)
    {
        lock (_sessionLock)
        {
            CurrentSession = new SessionInfo
            {
                SessionName = sessionName,
                HostName = Environment.MachineName,
                Port = port,
                HostAddress = "0.0.0.0"
            };
        }

        await _tcpServer.StartAsync(port);
        _logSink.Write($"세션을 개설했습니다. 이름={sessionName}, 포트={port}");
        return CurrentSession;
    }

    public async Task CloseSessionAsync()
    {
        lock (_sessionLock)
        {
            if (CurrentSession is not null)
            {
                _logSink.Write($"세션을 종료했습니다. 이름={CurrentSession.SessionName}");
            }

            _participants.Clear();
            _clientDisplayNames.Clear();
            CurrentSession = null;
        }

        await _tcpServer.StopAsync();
        ParticipantsChanged?.Invoke();
    }

    public async Task BroadcastPacketAsync(BasePacket packet)
    {
        if (!IsSessionOpen)
        {
            _logSink.Write("브로드캐스트 실패: 세션이 열려있지 않습니다.");
            return;
        }

        packet.SessionId = CurrentSession!.SessionId;
        packet.SenderId = "Server";

        _logSink.Write($"패킷 브로드캐스트: {packet.MessageType}, 참여자={ParticipantCount}명");
        await _tcpServer.BroadcastAsync(packet);
    }

    public Task<BasePacket> HandleJoinAsync(SessionJoinPacket packet)
    {
        if (CurrentSession is null)
        {
            return Task.FromResult<BasePacket>(CreateError(ErrorCodes.SessionNotOpen, "현재 열려 있는 세션이 없습니다.", false, packet));
        }

        if (string.IsNullOrWhiteSpace(packet.DisplayName))
        {
            return Task.FromResult<BasePacket>(CreateError(ErrorCodes.DisplayNameRequired, "참여자 이름은 비워둘 수 없습니다.", true, packet));
        }

        if (!_participants.TryAdd(packet.DisplayName, packet.SenderId))
        {
            return Task.FromResult<BasePacket>(CreateError(ErrorCodes.AlreadyJoined, $"{packet.DisplayName}은(는) 이미 참여 중입니다.", true, packet));
        }

        CurrentSession.ParticipantCount = _participants.Count;

        _logSink.Write($"세션 참여 처리: {packet.DisplayName}, 현재 인원={CurrentSession.ParticipantCount}");
        ParticipantsChanged?.Invoke();

        return Task.FromResult<BasePacket>(new AckPacket
        {
            SessionId = CurrentSession.SessionId,
            SenderId = "Server",
            AckCode = AckCodes.SessionJoined,
            Message = $"{packet.DisplayName}님이 세션에 참여했습니다."
        });
    }

    public Task<BasePacket> HandleLeaveAsync(SessionLeavePacket packet)
    {
        if (CurrentSession is null)
        {
            return Task.FromResult<BasePacket>(CreateError(ErrorCodes.SessionNotOpen, "현재 열려 있는 세션이 없습니다.", false, packet));
        }

        if (!string.IsNullOrWhiteSpace(packet.DisplayName))
        {
            _participants.TryRemove(packet.DisplayName, out _);
        }

        CurrentSession.ParticipantCount = _participants.Count;
        _logSink.Write($"세션 이탈 처리: {packet.DisplayName}, 현재 인원={CurrentSession.ParticipantCount}");
        ParticipantsChanged?.Invoke();

        return Task.FromResult<BasePacket>(new AckPacket
        {
            SessionId = CurrentSession.SessionId,
            SenderId = "Server",
            AckCode = AckCodes.SessionLeft,
            Message = "세션 이탈이 처리되었습니다."
        });
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
    /// 클라이언트로부터 수신된 패킷을 타입별로 라우팅합니다.
    /// </summary>
    private async Task OnPacketReceivedAsync(string clientId, string json)
    {
        try
        {
            // MessageType을 먼저 확인하여 적절한 타입으로 역직렬화
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
                        _clientDisplayNames[clientId] = joinPacket.DisplayName;
                        response = await HandleJoinAsync(joinPacket);
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
                    // Heartbeat 응답은 별도 처리 없이 수신 확인만
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
    /// 클라이언트 연결이 끊어졌을 때 자동으로 세션에서 이탈 처리합니다.
    /// </summary>
    private async Task OnClientDisconnectedAsync(string clientId)
    {
        if (_clientDisplayNames.TryRemove(clientId, out var displayName))
        {
            _participants.TryRemove(displayName, out _);

            if (CurrentSession is not null)
            {
                CurrentSession.ParticipantCount = _participants.Count;
                _logSink.Write($"연결 끊김으로 자동 이탈: {displayName}, 현재 인원={CurrentSession.ParticipantCount}");
                ParticipantsChanged?.Invoke();
            }
        }

        await Task.CompletedTask;
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
