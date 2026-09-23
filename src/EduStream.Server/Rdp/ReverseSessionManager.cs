using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 학생→교수자 역방향 WDS 세션 관리자 프로토타입 구현
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// </summary>
public sealed class ReverseSessionManager : IReverseSessionManager
{
    private readonly ConcurrentDictionary<Guid, ReverseInvitationPacket> _invitations = new();
    private Guid _reverseSharingId = Guid.Empty;
    private string _hostStudentId = string.Empty;
    private ReverseSessionState _state = ReverseSessionState.Inactive;

    public bool IsReverseSharingActive => _state != ReverseSessionState.Inactive;
    public ReverseSessionState CurrentState => _state;
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    public Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, CancellationToken cancellationToken = default)
    {
        if (_state != ReverseSessionState.Inactive)
            throw new InvalidOperationException("역방향 공유가 이미 활성화되어 있습니다.");

        _reverseSharingId = Guid.NewGuid();
        _hostStudentId = studentId;
        _state = ReverseSessionState.Hosting;

        return Task.FromResult(_reverseSharingId);
    }

    public Task<ReverseInvitationPacket> CreateProfessorInvitationAsync(
        Guid sessionId,
        Guid sharingId,
        string professorId,
        Guid connectionId,
        string invitationPassword,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        if (_state != ReverseSessionState.Hosting)
            throw new InvalidOperationException("역방향 공유가 호스팅 상태가 아닙니다.");

        if (_reverseSharingId != sharingId)
            throw new InvalidOperationException("공유 ID가 일치하지 않습니다.");

        var invitationId = Guid.NewGuid();
        var connectionString = $"rdp://reverse/{invitationId}/{invitationPassword}";

        var invitation = new ReverseInvitationPacket
        {
            SessionId = sessionId,
            SharingId = sharingId,
            ProfessorId = professorId,
            ConnectionId = connectionId,
            ConnectionString = connectionString,
            ExpiresAt = expiresAt,
            HostStudentId = _hostStudentId
        };

        _invitations[invitationId] = invitation;
        _state = ReverseSessionState.Hosting;

        return Task.FromResult(invitation);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state != ReverseSessionState.Hosting)
            throw new InvalidOperationException("호스팅 상태에서만 연결할 수 있습니다.");

        _state = ReverseSessionState.Connecting;
        return Task.CompletedTask;
    }

    public Task OnConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (_state != ReverseSessionState.Connecting)
            throw new InvalidOperationException("연결 중 상태에서만 연결 성공 처리할 수 있습니다.");

        _state = ReverseSessionState.Connected;
        return Task.CompletedTask;
    }

    public Task OnConnectionFailedAsync(CancellationToken cancellationToken = default)
    {
        _state = ReverseSessionState.Failed;
        return Task.CompletedTask;
    }

    public Task OnDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        _state = ReverseSessionState.Disconnected;
        return Task.CompletedTask;
    }

    public void ReceiveFrame(byte[] frameData)
    {
        if (_state != ReverseSessionState.Connected && _state != ReverseSessionState.ControlGranted)
            return;

        FrameReceived?.Invoke(this, new FrameReceivedEventArgs
        {
            FrameData = frameData,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public Task StopReverseSharingAsync(CancellationToken cancellationToken = default)
    {
        _invitations.Clear();
        _reverseSharingId = Guid.Empty;
        _hostStudentId = string.Empty;
        _state = ReverseSessionState.Inactive;

        return Task.CompletedTask;
    }
}
