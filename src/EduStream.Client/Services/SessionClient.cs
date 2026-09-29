using EduStream.Core.Factories;
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

    public SessionJoinPacket CreateJoinRequest(string hostAddress, int port, string displayName, string? joinTicket = null)
    {
        return PacketFactory.CreateSessionJoin(
            senderId: displayName,
            displayName: displayName,
            targetAddress: hostAddress,
            targetPort: port,
            joinTicket: joinTicket);
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
        return PacketFactory.CreateError(
            senderId: "Client",
            errorCode: ErrorCodes.JoinRejected,
            message: $"{hostAddress}:{port} 연결 실패 - {message}",
            isRecoverable: true);
    }

    public SessionLeavePacket CreateLeaveRequest(string senderId, string reason)
    {
        return PacketFactory.CreateSessionLeave(
            senderId: senderId,
            displayName: senderId,
            reason: reason,
            sessionId: CurrentSession?.SessionId);
    }

    public Task DisconnectAsync(string reason = "사용자 종료")
    {
        // 사용자 종료와 연결 끊김 처리가 동시에 부를 수 있어 한 번만 읽는다.
        if (CurrentSession is { } session)
        {
            _logSink.Write($"세션 연결을 종료했습니다. 대상={session.HostAddress}:{session.Port}, 사유={reason}");
        }

        CurrentSession = null;
        return Task.CompletedTask;
    }
}
