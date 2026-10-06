using System.Collections.Concurrent;
using System.Text;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당: 학생 앱 역방향 공유 수명. 서버 확정 보기 허용·현재 대상이 있을 때만 이 PC 호스트를 열고,
/// 허용 OFF·대상 변경·거부·시작 실패·퇴장 때 닫으며 같은 대상으로 재시도를 반복하지 않는지 확인합니다.
/// </summary>
public sealed class StudentReverseShareServiceTests
{
    [Fact]
    public async Task BeforeRoomSnapshot_DoesNotOpenHost()
    {
        var rig = new Rig();

        await rig.Share.UpdateAsync(true);

        Assert.Empty(rig.Hosts);
        Assert.Empty(rig.Channel.Frames);
        Assert.False(rig.Share.IsSharing);
    }

    [Fact]
    public async Task ViewingAllowed_OpensHostAndSendsInvitationThenSecretForCurrentTarget()
    {
        var rig = new Rig();
        rig.ApplyRoom();

        await rig.Share.UpdateAsync(true);

        var host = Assert.Single(rig.Hosts);
        Assert.Equal(rig.Target.StudentId, host.StudentId);
        Assert.True(rig.Share.IsSharing);
        var frames = rig.Channel.Frames.ToArray();
        Assert.Equal(2, frames.Length);
        var invitation = CollaborationMessageCodec.Decode<ReverseRdpInvitationNotice>(frames[0], out _);
        var secret = CollaborationMessageCodec.Decode<ReverseRdpInvitationSecretNotice>(frames[1], out _);
        Assert.Equal(host.SharingId, invitation.SharingId);
        Assert.Equal(rig.Target.ProfessorId, invitation.ProfessorId);
        Assert.Equal(rig.Target.ConnectionId, invitation.ConnectionId);
        Assert.Equal(host.Password, secret.Password);
        secret.ValidateForInvitation(invitation, DateTimeOffset.UtcNow);
        Assert.Contains(rig.Messages, message => message.Contains("공유를 시작"));
    }

    [Fact]
    public async Task RepeatedSnapshotForSameTarget_DoesNotReopenHost()
    {
        var rig = new Rig();
        rig.ApplyRoom();

        await rig.Share.UpdateAsync(true);
        await rig.Share.UpdateAsync(true);

        Assert.Single(rig.Hosts);
        Assert.Equal(2, rig.Channel.Frames.Count);
    }

    [Fact]
    public async Task ViewingRevoked_ClosesHost_AndAllowAgainOpensNewSharing()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);

        await rig.Share.UpdateAsync(false);

        Assert.True(rig.Hosts[0].Disposed);
        Assert.False(rig.Share.IsSharing);

        await rig.Share.UpdateAsync(true);

        Assert.Equal(2, rig.Hosts.Count);
        Assert.False(rig.Hosts[1].Disposed);
        Assert.NotEqual(rig.Hosts[0].SharingId, rig.Hosts[1].SharingId);
    }

    [Fact]
    public async Task ProfessorConnectionChanged_ReplacesHostForNewProfessor()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);
        var oldProfessorId = rig.Target.ProfessorId;

        rig.Professor = new ParticipantConnection(rig.SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);

        Assert.Equal(2, rig.Hosts.Count);
        Assert.True(rig.Hosts[0].Disposed);
        Assert.False(rig.Hosts[1].Disposed);
        var last = CollaborationMessageCodec.Decode<ReverseRdpInvitationNotice>(rig.Channel.Frames.ToArray()[2], out _);
        Assert.NotEqual(oldProfessorId, last.ProfessorId);
        Assert.Equal(rig.Target.ProfessorId, last.ProfessorId);
    }

    [Fact]
    public async Task AmbiguousProfessor_ClosesHost()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);

        rig.ApplyRoom(extraProfessor: true);
        await rig.Share.UpdateAsync(true);

        Assert.True(Assert.Single(rig.Hosts).Disposed);
        Assert.False(rig.Share.IsSharing);
    }

    [Fact]
    public async Task RejectedInvitation_ClosesHostAndDoesNotRetrySameTarget()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);

        Assert.True(rig.Reverse.HandleFailure(new CollaborationFailureNotice(rig.Hosts[0].InvitationId, CollaborationError.StaleConnection)));
        await WaitUntilAsync(() => rig.Hosts[0].Disposed);
        await rig.Share.UpdateAsync(true);

        Assert.Single(rig.Hosts);
        Assert.False(rig.Share.IsSharing);
        Assert.Contains(rig.Messages, message => message.Contains("초대를 받지 않아"));

        // 사용자가 허용을 다시 켜면 새 공유로 한 번 더 시도한다.
        await rig.Share.UpdateAsync(false);
        await rig.Share.UpdateAsync(true);
        Assert.Equal(2, rig.Hosts.Count);
        Assert.True(rig.Share.IsSharing);
    }

    [Fact]
    public async Task RejectionOfOtherInvitation_IsIgnored()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);
        var first = rig.Hosts[0].InvitationId;
        await rig.Share.UpdateAsync(false);
        await rig.Share.UpdateAsync(true);

        // 이전 공유 초대에 대한 늦은 거부는 현재 공유를 닫지 않는다.
        rig.Reverse.HandleFailure(new CollaborationFailureNotice(first, CollaborationError.StaleConnection));
        await rig.Share.UpdateAsync(true);

        Assert.False(rig.Hosts[1].Disposed);
        Assert.True(rig.Share.IsSharing);
    }

    [Fact]
    public async Task UnsupportedHost_ReportsAndDoesNotRetryOrSend()
    {
        var rig = new Rig { StartFailure = new NotSupportedException("WDS") };
        rig.ApplyRoom();

        await rig.Share.UpdateAsync(true);
        await rig.Share.UpdateAsync(true);

        var host = Assert.Single(rig.Hosts);
        Assert.True(host.Disposed);
        Assert.Empty(rig.Channel.Frames);
        Assert.False(rig.Share.IsSharing);
        Assert.Contains(rig.Messages, message => message.Contains("지원하지 않습니다"));
    }

    [Fact]
    public async Task SendFailure_ClosesHost()
    {
        var rig = new Rig();
        rig.Channel.Failure = new IOException("closed");
        rig.ApplyRoom();

        await rig.Share.UpdateAsync(true);

        Assert.True(Assert.Single(rig.Hosts).Disposed);
        Assert.False(rig.Share.IsSharing);
    }

    [Fact]
    public async Task Dispose_ClosesHostAndIgnoresLaterUpdates()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);

        await rig.Share.DisposeAsync();
        await rig.Share.UpdateAsync(true);

        Assert.True(Assert.Single(rig.Hosts).Disposed);
        Assert.False(rig.Share.IsSharing);
    }

    [Fact]
    public async Task StopFailure_DoesNotAnnounceSuccess_AndRetainsHostForRetry()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);
        Assert.Single(rig.Hosts).DisposeFailure = new InvalidOperationException("review: native close failed");
        await rig.Share.UpdateAsync(false);
        Assert.DoesNotContain("내 화면 공유를 종료했습니다.", rig.Messages);
        Assert.True(rig.Share.IsSharing);
        Assert.Single(rig.Hosts).DisposeFailure = null;
        await rig.Share.RefreshAsync();
        Assert.False(rig.Share.IsSharing);
        Assert.True(rig.Hosts[0].Disposed);
    }

    [Fact]
    public async Task ExpiringUnusedInvitation_RenewsWithoutRestartingHost()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);
        var first = rig.Hosts[0].InvitationId;
        rig.Now = rig.Now.AddMinutes(4);
        await rig.Share.RefreshAsync();
        Assert.Single(rig.Hosts);
        Assert.NotEqual(first, rig.Hosts[0].InvitationId);
        Assert.Equal(4, rig.Channel.Frames.Count);
    }

    [Fact]
    public async Task ConnectedViewer_IsNotDisconnectedByPeriodicRenewal()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);
        rig.Hosts[0].ActiveViewerCount = 1;
        rig.Now = rig.Now.AddMinutes(6);
        await rig.Share.RefreshAsync();
        Assert.Equal(2, rig.Channel.Frames.Count);
        Assert.False(rig.Hosts[0].Disposed);
    }

    [Fact]
    public async Task ViewerDisconnected_RenewsConsumedInvitationWithoutPermissionRetoggle()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);
        var first = rig.Hosts[0].InvitationId;
        rig.Hosts[0].ConnectionRevision++;
        await rig.Share.RefreshAsync();
        Assert.Equal(2, rig.Hosts.Count);
        Assert.True(rig.Hosts[0].Disposed);
        Assert.NotEqual(first, rig.Hosts[1].InvitationId);
        Assert.NotEqual(rig.Hosts[0].SharingId, rig.Hosts[1].SharingId);
        Assert.Equal(4, rig.Channel.Frames.Count);
    }

    [Fact]
    public async Task LocalOff_ClosesBeforeServerAck_AndIgnoresLateAllowedSnapshot()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);
        await rig.Share.SetLocalViewingAllowedAsync(false);
        Assert.False(rig.Share.IsSharing);
        await rig.Share.UpdateAsync(true);
        Assert.False(rig.Share.IsSharing);
        await rig.Share.SetLocalViewingAllowedAsync(true);
        Assert.False(rig.Share.IsSharing);
        await rig.Share.UpdateAsync(true);
        Assert.True(rig.Share.IsSharing);
    }

    [Fact]
    public async Task DisposeFailure_IsObservable_AndSecondDisposeRetries()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        await rig.Share.UpdateAsync(true);
        rig.Hosts[0].DisposeFailure = new IOException("close failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Share.DisposeAsync().AsTask());
        rig.Hosts[0].DisposeFailure = null;
        await rig.Share.DisposeAsync();
        Assert.False(rig.Share.IsSharing);
        Assert.True(rig.Hosts[0].Disposed);
    }

    [Fact]
    public async Task OldHostCleanupMustCompleteBeforeOpeningReplacement()
    {
        var rig = new Rig();
        rig.ApplyRoom();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        await using var replacement = new StudentReverseShareService(rig.Reverse, new InMemoryLogSink(),
            id => { starts++; return new FakeHost(id, null); }, enableAutoRefresh: false, beforeStart: () => cleanup.Task);
        var update = replacement.UpdateAsync(true);
        Assert.Equal(0, starts);
        cleanup.SetResult();
        await update;
        Assert.Equal(1, starts);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건이 제한 시간 안에 충족되지 않았습니다.");
            await Task.Delay(20);
        }
    }

    private sealed class Rig
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public ParticipantConnection Self { get; }
        public ParticipantConnection Professor { get; set; }
        public FakeChannel Channel { get; } = new();
        public ReverseCollaborationClient Reverse { get; }
        public StudentReverseShareService Share { get; }
        public List<FakeHost> Hosts { get; } = new();
        public ConcurrentQueue<string> Messages { get; } = new();
        public Exception? StartFailure { get; init; }
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public Rig()
        {
            Self = new ParticipantConnection(SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
            Professor = new ParticipantConnection(SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
            Reverse = new ReverseCollaborationClient(SessionId, Channel, new InMemoryLogSink(), clock: () => Now);
            Share = new StudentReverseShareService(Reverse, new InMemoryLogSink(), studentId =>
            {
                var host = new FakeHost(studentId, StartFailure);
                lock (Hosts) Hosts.Add(host);
                return host;
            }, clock: () => Now, enableAutoRefresh: false);
            Share.SharingChanged += (_, message) => Messages.Enqueue(message);
        }

        public ReverseRdpInvitationTarget Target => Reverse.Target!;

        public void ApplyRoom(bool extraProfessor = false)
        {
            var participants = new List<ParticipantSnapshot>
            {
                new(Self, "Alice", true, true, true, 0),
                new(Professor, "교수자", true, false, false, 0)
            };
            if (extraProfessor)
                participants.Add(new(new ParticipantConnection(SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor),
                    "교수자2", true, false, false, 0));
            Reverse.ApplyRoom(new RoomJoined(Self, 1, participants));
        }
    }

    private sealed class FakeChannel : ICollaborationChannel
    {
        public ConcurrentQueue<byte[]> Frames { get; } = new();
        public Exception? Failure { get; set; }

        public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            Frames.Enqueue(frame);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHost(string studentId, Exception? startFailure) : IStudentShareHost
    {
        public string StudentId { get; } = studentId;
        public Guid SharingId { get; private set; }
        public Guid InvitationId { get; private set; }
        public string? Password { get; private set; }
        public bool Disposed { get; private set; }
        public int ActiveViewerCount { get; set; }
        public long ConnectionRevision { get; set; }
        public Exception? DisposeFailure { get; set; }

        public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            if (startFailure is not null) throw startFailure;
            SharingId = Guid.NewGuid();
            return Task.FromResult(SharingId);
        }

        public Task<ReverseRdpInvitationNotice> CreateInvitationAsync(Guid sessionId, Guid sharingId, string professorId,
            Guid connectionId, string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
        {
            const string connectionString = "<E><A KH=\"x\"/></E>";
            Password = invitationPassword;
            InvitationId = Guid.NewGuid();
            return Task.FromResult(new ReverseRdpInvitationNotice
            {
                ContractVersion = ReverseRdpInvitationNotice.CurrentVersion,
                Provider = ReverseRdpInvitationNotice.ProviderName,
                Direction = ReverseRdpInvitationNotice.StudentToProfessor,
                SessionId = sessionId, SharingId = sharingId, InvitationId = InvitationId,
                ConnectionId = connectionId, ProfessorId = professorId, StudentId = StudentId,
                ParticipantId = professorId, ConnectionString = connectionString,
                DataLength = Encoding.UTF8.GetByteCount(connectionString), ExpiresAt = expiresAt,
                ControlMode = ReverseRdpControlMode.HostGrantedInteractive, ViewOnly = false
            });
        }

        public ValueTask DisposeAsync()
        {
            if (DisposeFailure is not null) throw DisposeFailure;
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
