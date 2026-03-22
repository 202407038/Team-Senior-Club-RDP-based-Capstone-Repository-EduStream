using EduStream.Core.Logging;
using EduStream.Core.Models;

namespace EduStream.Client.Services;

/// <summary>
/// 서버 세션 참여와 기본 상태 전이를 담당하는 클라이언트 스텁입니다.
/// </summary>
public sealed class SessionClient
{
    private readonly ILogSink _logSink;

    public SessionClient(ILogSink logSink)
    {
        _logSink = logSink;
    }

    public SessionInfo? CurrentSession { get; private set; }

    public bool IsConnected => CurrentSession is not null;

    public Task<SessionInfo> JoinSessionAsync(string hostAddress, int port)
    {
        CurrentSession = new SessionInfo
        {
            HostAddress = hostAddress,
            Port = port,
            SessionName = "EduStream 강의 세션"
        };

        _logSink.Write($"세션에 참여했습니다. 대상={hostAddress}:{port}");
        return Task.FromResult(CurrentSession);
    }

    public Task DisconnectAsync()
    {
        if (CurrentSession is not null)
        {
            _logSink.Write($"세션 연결을 종료했습니다. 대상={CurrentSession.HostAddress}:{CurrentSession.Port}");
        }

        CurrentSession = null;
        return Task.CompletedTask;
    }
}
