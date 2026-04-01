using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;

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

    public SessionJoinPacket CreateJoinRequest(string hostAddress, int port, string displayName)
    {
        return new SessionJoinPacket
        {
            SenderId = displayName,
            DisplayName = displayName,
            TargetAddress = hostAddress,
            TargetPort = port
        };
    }

    public Task<SessionInfo> ApplyJoinAckAsync(AckPacket packet, string hostAddress, int port)
    {
        CurrentSession = new SessionInfo
        {
            SessionId = packet.SessionId ?? Guid.NewGuid(),
            HostAddress = hostAddress,
            Port = port,
            SessionName = "EduStream 강의 세션",
            ParticipantCount = 1
        };

        _logSink.Write($"세션 참여 응답 수신: {packet.AckCode}, 대상={hostAddress}:{port}");
        return Task.FromResult(CurrentSession);
    }

    public ErrorPacket CreateJoinError(string hostAddress, int port, string message)
    {
        return new ErrorPacket
        {
            SenderId = "Client",
            ErrorCode = ErrorCodes.JoinRejected,
            Message = $"{hostAddress}:{port} 연결 실패 - {message}",
            IsRecoverable = true
        };
    }

    public SessionLeavePacket CreateLeaveRequest(string senderId, string reason)
    {
        return new SessionLeavePacket
        {
            SessionId = CurrentSession?.SessionId,
            SenderId = senderId,
            DisplayName = senderId,
            Reason = reason
        };
    }

    public Task DisconnectAsync(string reason = "사용자 종료")
    {
        if (CurrentSession is not null)
        {
            _logSink.Write($"세션 연결을 종료했습니다. 대상={CurrentSession.HostAddress}:{CurrentSession.Port}, 사유={reason}");
        }

        CurrentSession = null;
        return Task.CompletedTask;
    }
}
