using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당(A07): 교수자가 학생 화면(역방향 초대)을 더 이상 볼 수 없게 되면 그 학생의 원격 제어도 회수되는지 검증합니다.
/// 실제 입력 엔진 대신 호출 기록 대역으로 회수 요청 도달까지만 확인합니다.
/// </summary>
public sealed class ReverseViewingControlRevokeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StudentRestartsReverseSharing_RevokesControl_AndDoesNotRegrant()
    {
        await using var rig = await Rig.OpenAsync();
        await rig.JoinAsync("Alice");
        var alice = rig.Connection("Alice");
        await rig.DeliverInvitationAsync(alice, Guid.NewGuid());
        await rig.SessionManager.RequestControlAsync("Alice");
        Assert.Equal(ControlPhase.Active, rig.SessionManager.CurrentControlState!.Phase);

        // 학생이 역방향 공유를 다시 시작해 새 세대 초대를 보내면 이전 화면은 회수된다.
        await rig.DeliverInvitationAsync(alice, Guid.NewGuid());

        await WaitUntilAsync(() => !rig.Gate.Revoked.IsEmpty);
        var state = rig.SessionManager.CurrentControlState!;
        Assert.Equal(ControlPhase.Revoked, state.Phase);
        Assert.Equal(alice, Assert.Single(rig.Gate.Revoked).Student);
        // 새 화면이 붙어도 제어는 자동으로 다시 허용하지 않는다.
        Assert.Single(rig.Gate.Granted);
        Assert.NotNull(rig.SessionManager.ReverseCollaboration!.TryGetInvitation(alice.ConnectionId));
    }

    [Fact]
    public async Task OtherStudentsViewingWithdrawn_DoesNotTouchActiveControl()
    {
        await using var rig = await Rig.OpenAsync();
        await rig.JoinAsync("Alice");
        await rig.JoinAsync("Bob");
        var alice = rig.Connection("Alice");
        var bob = rig.Connection("Bob");
        await rig.DeliverInvitationAsync(alice, Guid.NewGuid());
        await rig.DeliverInvitationAsync(bob, Guid.NewGuid());
        await rig.SessionManager.RequestControlAsync("Alice");

        await rig.DeliverInvitationAsync(bob, Guid.NewGuid());
        await Task.Delay(200);

        var state = rig.SessionManager.CurrentControlState!;
        Assert.Equal(ControlPhase.Active, state.Phase);
        Assert.Equal(alice, state.Student);
        Assert.Empty(rig.Gate.Revoked);
    }

    [Fact]
    public async Task ViewingWithdrawnAfterControlAlreadyRevoked_RevokesOnlyOnce()
    {
        await using var rig = await Rig.OpenAsync();
        await rig.JoinAsync("Alice");
        await rig.DeliverInvitationAsync(rig.Connection("Alice"), Guid.NewGuid());
        await rig.SessionManager.RequestControlAsync("Alice");

        // 보기 허용 OFF는 레지스트리 경로와 역방향 화면 회수 경로가 동시에 회수를 시도한다.
        await rig.SessionManager.UpdateParticipantPermissionsAsync("Alice", allowViewing: false, allowControl: false);
        await Task.Delay(200);

        Assert.Equal(ControlPhase.Revoked, rig.SessionManager.CurrentControlState!.Phase);
        Assert.Single(rig.Gate.Revoked);
        Assert.Null(rig.SessionManager.ReverseCollaboration!.TryGetInvitation(rig.Connection("Alice").ConnectionId));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건이 제한 시간 안에 충족되지 않았습니다.");
            await Task.Delay(20);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private sealed class RecordingInputGate : IRemoteInputGate
    {
        public ConcurrentQueue<RemoteControlState> Granted { get; } = new();
        public ConcurrentQueue<RemoteControlState> Revoked { get; } = new();

        public Task GrantAsync(RemoteControlState requested, CancellationToken cancellationToken)
        {
            Granted.Enqueue(requested);
            return Task.CompletedTask;
        }

        public Task RevokeAsync(RemoteControlState revoked, CancellationToken cancellationToken)
        {
            Revoked.Enqueue(revoked);
            return Task.CompletedTask;
        }
    }

    private sealed class NullChannel : ICollaborationChannel
    {
        public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopRdpSharingService : IRdpSharingService
    {
        public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());
        public Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId, string participantId,
            Guid connectionId, string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<TcpClientService> _clients = new();
        private readonly PacketSerializer _serializer = new();

        private Rig(SessionManager sessionManager, int port)
        {
            SessionManager = sessionManager;
            Port = port;
        }

        public SessionManager SessionManager { get; }
        public int Port { get; }
        public RecordingInputGate Gate { get; } = new();

        public static async Task<Rig> OpenAsync()
        {
            var port = GetFreePort();
            var logSink = new InMemoryLogSink();
            var sessionManager = new SessionManager(logSink, new TcpServerService(logSink, new PacketSerializer()));
            var rig = new Rig(sessionManager, port);
            sessionManager.AttachRemoteInputGate(rig.Gate);
            await sessionManager.OpenSessionAsync("ReverseControlRevokeTest", port);
            // 제어 요청은 화면 공유가 연결된 동안에만 허용된다.
            sessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());
            return rig;
        }

        public ParticipantConnection Connection(string displayName) =>
            SessionManager.Participants.Participants.Single(participant => participant.DisplayName == displayName).Connection;

        public async Task JoinAsync(string displayName)
        {
            var client = new TcpClientService(new InMemoryLogSink(), _serializer);
            await client.ConnectAsync("127.0.0.1", Port);
            var ackReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PacketReceived += (packetType, _) =>
            {
                if (packetType is PacketType.Ack or PacketType.Error) ackReceived.TrySetResult();
                return Task.CompletedTask;
            };
            await client.SendAsync(PacketFactory.CreateSessionJoin(
                senderId: displayName, displayName: displayName, targetAddress: "127.0.0.1", targetPort: Port));
            await ackReceived.Task.WaitAsync(Wait);
            _clients.Add(client);
        }

        /// <summary>학생 연결에서 받은 것처럼 Kind 15·16을 라우터에 넣습니다. 보호 채널 전달 자체는 라우팅 테스트가 검증합니다.</summary>
        public async Task DeliverInvitationAsync(ParticipantConnection student, Guid sharingId)
        {
            var router = SessionManager.ReverseCollaboration!;
            const string connectionString = "<E><A KH=\"x\"/></E>";
            var invitation = new ReverseRdpInvitationNotice
            {
                ContractVersion = ReverseRdpInvitationNotice.CurrentVersion,
                Provider = ReverseRdpInvitationNotice.ProviderName,
                Direction = ReverseRdpInvitationNotice.StudentToProfessor,
                SessionId = student.SessionId, SharingId = sharingId, InvitationId = Guid.NewGuid(),
                ConnectionId = student.ConnectionId, ProfessorId = router.ProfessorId,
                StudentId = ReverseRdpIdentity.For(student), ParticipantId = router.ProfessorId,
                ConnectionString = connectionString, DataLength = Encoding.UTF8.GetByteCount(connectionString),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                ControlMode = ReverseRdpControlMode.HostGrantedInteractive, ViewOnly = false
            };
            var secret = new ReverseRdpInvitationSecretNotice(invitation.SessionId, invitation.SharingId,
                invitation.InvitationId, invitation.ConnectionId, invitation.StudentId, invitation.ProfessorId,
                "pw-secret", invitation.ExpiresAt);
            var channel = new NullChannel();
            await router.HandleFrameAsync(student, channel, CollaborationMessageCodec.Encode(Guid.NewGuid(), invitation));
            await router.HandleFrameAsync(student, channel, CollaborationMessageCodec.Encode(Guid.NewGuid(), secret));
            Assert.Equal(invitation.InvitationId, router.TryGetInvitation(student.ConnectionId)?.Invitation.InvitationId);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients)
            {
                try { client.Dispose(); } catch { }
            }
            await SessionManager.CloseSessionAsync();
        }
    }
}
