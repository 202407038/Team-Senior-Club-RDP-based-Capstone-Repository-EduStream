using EduStream.Core.Logging;
using EduStream.Core.Models;

namespace EduStream.Server.Services;

/// <summary>
/// 세션 개설/종료와 패킷 브로드캐스트 진입점을 담당합니다.
/// 실제 소켓 구현은 이후 단계에서 이 클래스에 들어갑니다.
/// </summary>
public sealed class SessionManager
{
    private readonly ILogSink _logSink;

    public SessionManager(ILogSink logSink)
    {
        _logSink = logSink;
    }

    public SessionInfo? CurrentSession { get; private set; }

    public bool IsSessionOpen => CurrentSession is not null;

    public Task<SessionInfo> OpenSessionAsync(string sessionName, int port)
    {
        CurrentSession = new SessionInfo
        {
            SessionName = sessionName,
            Port = port,
            HostAddress = "127.0.0.1"
        };

        _logSink.Write($"세션을 개설했습니다. 이름={sessionName}, 포트={port}");
        return Task.FromResult(CurrentSession);
    }

    public Task CloseSessionAsync()
    {
        if (CurrentSession is not null)
        {
            _logSink.Write($"세션을 종료했습니다. 이름={CurrentSession.SessionName}");
        }

        CurrentSession = null;
        return Task.CompletedTask;
    }

    public Task BroadcastPacketAsync(BasePacket packet)
    {
        _logSink.Write($"패킷 브로드캐스트 요청: {packet.MessageType}, 길이={packet.DataLength}");
        return Task.CompletedTask;
    }
}
