using System.Collections.Concurrent;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.Server.Services;

/// <summary>
/// 세션 개설/종료와 참가자 브로드캐스트 진입점을 담당합니다.
/// 실제 소켓 구현은 이후 단계에서 이 클래스에 연결됩니다.
/// </summary>
public sealed class SessionManager
{
    private readonly ILogSink _logSink;
    private readonly ConcurrentDictionary<string, string> _participants = new();
    private readonly object _sessionLock = new();

    public SessionManager(ILogSink logSink)
    {
        _logSink = logSink;
    }

    public SessionInfo? CurrentSession { get; private set; }

    public bool IsSessionOpen => CurrentSession is not null;

    public int ParticipantCount => _participants.Count;

    public Task<SessionInfo> OpenSessionAsync(string sessionName, int port)
    {
        SessionInfo openedSession;

        lock (_sessionLock)
        {
            CurrentSession = new SessionInfo
            {
                SessionName = sessionName,
                HostName = Environment.MachineName,
                Port = port,
                HostAddress = "127.0.0.1"
            };

            openedSession = CurrentSession;
        }

        _logSink.Write($"세션을 개설했습니다. 이름={sessionName}, 포트={port}");
        return Task.FromResult(openedSession);
    }

    public Task CloseSessionAsync()
    {
        string? sessionName = null;

        lock (_sessionLock)
        {
            if (CurrentSession is not null)
            {
                sessionName = CurrentSession.SessionName;
            }

            _participants.Clear();
            CurrentSession = null;
        }

        if (!string.IsNullOrWhiteSpace(sessionName))
        {
            _logSink.Write($"세션을 종료했습니다. 이름={sessionName}");
        }

        return Task.CompletedTask;
    }

    public Task BroadcastPacketAsync(BasePacket packet)
    {
        _logSink.Write($"패킷 브로드캐스트 요청: {packet.MessageType}, 길이={packet.DataLength}");
        return Task.CompletedTask;
    }

    public Task<BasePacket> HandleJoinAsync(SessionJoinPacket packet)
    {
        BasePacket response;
        string? logMessage = null;

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

        return Task.FromResult(response);
    }

    public Task<BasePacket> HandleLeaveAsync(SessionLeavePacket packet)
    {
        BasePacket response;
        string? logMessage = null;

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
                // 새 호출부는 DisplayName을 채우고, 기존 호출부는 SenderId로 fallback 합니다.
                var participantKey = string.IsNullOrWhiteSpace(packet.DisplayName)
                    ? packet.SenderId
                    : packet.DisplayName;

                if (!string.IsNullOrWhiteSpace(participantKey))
                {
                    _participants.TryRemove(participantKey, out _);
                }

                currentSession.ParticipantCount = _participants.Count;
                logMessage = $"세션 이탈 처리: {participantKey}, 현재 인원={currentSession.ParticipantCount}";
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
