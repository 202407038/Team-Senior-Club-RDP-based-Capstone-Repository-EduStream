using System.Collections.Concurrent;
using System.Drawing;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Rdp;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당: Kind 15·16(학생→교수자 역방향 초대/비밀번호)과 Kind 17(교수자→학생 판서)의 인증 라우팅을 검증합니다.
/// 정상 전달과 함께 다른 학생·옛 공유·옛 연결·만료·중복/역순·보기 허용 철회를 거부하는지 확인합니다.
/// </summary>
public sealed partial class ReverseCollaborationRoutingTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // ---------- 서버 라우터: 역방향 초대 ----------

    [Fact]
    public async Task Invitation_ThenSecret_RaisesReadyWithRegistryIdentity()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        var ready = new List<ReverseInvitationDelivery>();
        rig.Router.InvitationReady += ready.Add;
        var invitation = rig.Invitation(alice, Guid.NewGuid());

        await rig.SendAsync(alice, invitation);
        Assert.Empty(ready);
        await rig.SendAsync(alice, Secret(invitation));

        var delivery = Assert.Single(ready);
        Assert.Equal(alice, delivery.Student);
        Assert.Equal("Alice", delivery.DisplayName);
        Assert.Equal(invitation, delivery.Invitation);
        Assert.Equal(delivery, rig.Router.TryGetInvitation(alice.ConnectionId));
        Assert.DoesNotContain("pw-secret", delivery.ToString());
        Assert.Empty(rig.Failures(alice));
    }

    public static TheoryData<string> ForgedInvitations => new() { "other-student", "other-professor", "other-connection", "other-session", "expired" };

    [Theory]
    [MemberData(nameof(ForgedInvitations))]
    public async Task Invitation_NotMatchingAuthenticatedConnection_IsRejected(string forgery)
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        var bob = rig.Join("bob", "Bob");
        var valid = rig.Invitation(alice, Guid.NewGuid());
        var forged = forgery switch
        {
            // Bob 연결로 Alice 신원을 주장하거나, 다른 교수자·연결·세션·만료 초대를 보내는 경우.
            "other-student" => valid with { StudentId = ReverseRdpIdentity.For(bob) },
            "other-professor" => valid with { ProfessorId = "intruder", ParticipantId = "intruder" },
            "other-connection" => valid with { ConnectionId = bob.ConnectionId },
            "other-session" => valid with { SessionId = Guid.NewGuid() },
            _ => valid with { ExpiresAt = rig.Now.AddSeconds(-1) }
        };

        await rig.SendAsync(alice, forged);
        await rig.SendAsync(alice, Secret(forged));

        Assert.Null(rig.Router.TryGetInvitation(alice.ConnectionId));
        Assert.Contains(rig.Failures(alice), failure =>
            failure.RequestId == forged.InvitationId && failure.Error == CollaborationError.StaleConnection);
    }

    [Fact]
    public async Task InvitationSentOverAnotherStudentsConnection_IsRejected()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        var bob = rig.Join("bob", "Bob");
        var aliceInvitation = rig.Invitation(alice, Guid.NewGuid());

        // Alice 초대를 Bob 연결이 그대로 중계해도 발신 연결 기준으로 거부된다.
        await rig.SendAsync(bob, aliceInvitation);
        await rig.SendAsync(bob, Secret(aliceInvitation));

        Assert.Null(rig.Router.TryGetInvitation(bob.ConnectionId));
        Assert.Null(rig.Router.TryGetInvitation(alice.ConnectionId));
        Assert.NotEmpty(rig.Failures(bob));
    }

    [Fact]
    public async Task SecretWithoutInvitation_OrMismatched_IsRejected_AndInvitationIsNotReusable()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        var invitation = rig.Invitation(alice, Guid.NewGuid());

        await rig.SendAsync(alice, Secret(invitation));
        Assert.Contains(rig.Failures(alice), failure => failure.RequestId == invitation.InvitationId);

        await rig.SendAsync(alice, invitation);
        await rig.SendAsync(alice, Secret(invitation) with { ExpiresAt = invitation.ExpiresAt.AddMinutes(1) });
        // 비밀번호 대조에 실패한 초대는 버려지므로 올바른 비밀번호가 뒤늦게 와도 쓰지 않는다.
        await rig.SendAsync(alice, Secret(invitation));

        Assert.Null(rig.Router.TryGetInvitation(alice.ConnectionId));
        Assert.Equal(3, rig.Failures(alice).Count(failure => failure.RequestId == invitation.InvitationId));
    }

    [Fact]
    public async Task DuplicateInvitationId_IsRejected()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        var invitation = rig.Invitation(alice, Guid.NewGuid());
        await rig.DeliverAsync(alice, invitation);

        await rig.SendAsync(alice, invitation);

        Assert.Contains(rig.Failures(alice), failure =>
            failure.RequestId == invitation.InvitationId && failure.Error == CollaborationError.InvalidRequest);
        // 거부는 이미 준비된 초대를 건드리지 않는다.
        Assert.NotNull(rig.Router.TryGetInvitation(alice.ConnectionId));
    }

    [Fact]
    public async Task StudentRestartsReverseSharing_OldInvitationWithdrawn_AndOldSharingRejected()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        var withdrawn = new List<Guid>();
        rig.Router.InvitationWithdrawn += (_, invitationId) => withdrawn.Add(invitationId);
        var oldSharing = Guid.NewGuid();
        var first = rig.Invitation(alice, oldSharing);
        await rig.DeliverAsync(alice, first);

        var restarted = rig.Invitation(alice, Guid.NewGuid());
        await rig.DeliverAsync(alice, restarted);
        Assert.Equal(new[] { first.InvitationId }, withdrawn);
        Assert.Equal(restarted.InvitationId, rig.Router.TryGetInvitation(alice.ConnectionId)!.Invitation.InvitationId);

        // 늦게 도착한 이전 공유 세대의 새 초대는 거부하고 현재 초대를 유지한다.
        var late = rig.Invitation(alice, oldSharing);
        await rig.DeliverAsync(alice, late);
        Assert.Contains(rig.Failures(alice), failure =>
            failure.RequestId == late.InvitationId && failure.Error == CollaborationError.StaleConnection);
        Assert.Equal(restarted.InvitationId, rig.Router.TryGetInvitation(alice.ConnectionId)!.Invitation.InvitationId);
    }

    [Fact]
    public async Task ViewingNotAllowed_InvitationRejected_AndWithdrawalOnTurnOff()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        var withdrawn = new List<Guid>();
        rig.Router.InvitationWithdrawn += (_, invitationId) => withdrawn.Add(invitationId);
        var invitation = rig.Invitation(alice, Guid.NewGuid());
        await rig.DeliverAsync(alice, invitation);

        rig.Registry.SetPermissions(alice.ConnectionId, allowViewing: false, allowControl: false);
        Assert.Equal(new[] { invitation.InvitationId }, withdrawn);
        Assert.Null(rig.Router.TryGetInvitation(alice.ConnectionId));

        var next = rig.Invitation(alice, invitation.SharingId);
        await rig.DeliverAsync(alice, next);
        Assert.Contains(rig.Failures(alice), failure =>
            failure.RequestId == next.InvitationId && failure.Error == CollaborationError.PermissionDenied);
        Assert.Null(rig.Router.TryGetInvitation(alice.ConnectionId));
    }

    [Fact]
    public async Task StudentReconnects_OldInvitationWithdrawn_AndCannotBeReused()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        var withdrawn = new List<Guid>();
        rig.Router.InvitationWithdrawn += (_, invitationId) => withdrawn.Add(invitationId);
        var old = rig.Invitation(alice, Guid.NewGuid());
        await rig.DeliverAsync(alice, old);

        rig.Registry.Disconnect("alice");
        Assert.Equal(new[] { old.InvitationId }, withdrawn);
        var rejoined = rig.Join("alice", "Alice");

        // 재접속 전에 만든 초대(옛 연결 ID·옛 학생 ID)를 새 연결로 다시 보내도 거부된다.
        var replay = old with { InvitationId = Guid.NewGuid() };
        await rig.DeliverAsync(rejoined, replay);
        Assert.Null(rig.Router.TryGetInvitation(rejoined.ConnectionId));
        Assert.Null(rig.Router.TryGetInvitation(alice.ConnectionId));
        Assert.Contains(rig.Failures(rejoined), failure => failure.RequestId == replay.InvitationId);
    }

    [Fact]
    public async Task ReadyInvitation_ExpiresWithoutBeingReturned()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        var invitation = rig.Invitation(alice, Guid.NewGuid()) with { ExpiresAt = rig.Now.AddSeconds(30) };
        await rig.DeliverAsync(alice, invitation);
        Assert.NotNull(rig.Router.TryGetInvitation(alice.ConnectionId));

        rig.Now = rig.Now.AddSeconds(31);
        Assert.Null(rig.Router.TryGetInvitation(alice.ConnectionId));
    }

    // ---------- 서버 라우터: 판서 ----------

    [Fact]
    public async Task PublishAnnotation_WithoutSharing_FailsLoudly()
    {
        using var rig = new RouterRig();
        var ex = await Assert.ThrowsAsync<CollaborationException>(() => rig.Router.PublishAnnotationAsync(StrokeJson()));
        Assert.Equal(CollaborationError.SessionClosed, ex.Code);
    }

    [Fact]
    public async Task PublishAnnotation_ReachesEveryStudent_InOrder_FromProfessorConnection()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        var bob = rig.Join("bob", "Bob");
        var sharing = Guid.NewGuid();
        rig.Router.BeginAnnotationSharing(sharing);
        await rig.Router.AttachAnnotationPeerAsync(alice, rig.Channel(alice));
        await rig.Router.AttachAnnotationPeerAsync(bob, rig.Channel(bob));

        Assert.Equal(2, await rig.Router.PublishAnnotationAsync(StrokeJson()));
        Assert.Equal(2, await rig.Router.PublishAnnotationAsync(StrokeJson()));

        foreach (var student in new[] { alice, bob })
        {
            var notices = rig.Annotations(student);
            Assert.Equal(new long[] { 1, 2, 3 }, notices.Select(notice => notice.Sequence));
            AssertEmptyLayer(notices[0]);
            Assert.All(notices, notice =>
            {
                Assert.Equal(sharing, notice.SharingId);
                notice.ValidateForSender(rig.Professor, sharing, notice.Sequence - 1);
            });
        }
    }

    [Fact]
    public async Task InvalidAnnotation_IsRejected_WithoutConsumingSequence()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        rig.Router.BeginAnnotationSharing(Guid.NewGuid());
        await rig.Router.AttachAnnotationPeerAsync(alice, rig.Channel(alice));

        var ex = await Assert.ThrowsAsync<CollaborationException>(() => rig.Router.PublishAnnotationAsync("{\"StrokeId\":1}"));
        Assert.Equal(CollaborationError.InvalidRequest, ex.Code);
        await rig.Router.PublishAnnotationAsync(StrokeJson());

        Assert.Equal(new long[] { 1, 2 }, rig.Annotations(alice).Select(n => n.Sequence));
        AssertEmptyLayer(rig.Annotations(alice)[0]);
    }

    [Fact]
    public async Task LateJoiner_ReceivesReplay_LayerSnapshotReplacesHistory_AndRestartClears()
    {
        using var rig = new RouterRig();
        rig.Router.BeginAnnotationSharing(Guid.NewGuid());
        await rig.Router.PublishAnnotationAsync(StrokeJson());
        await rig.Router.PublishAnnotationAsync(StrokeJson());

        var alice = rig.Join("alice", "Alice");
        await rig.Router.AttachAnnotationPeerAsync(alice, rig.Channel(alice));
        Assert.Equal(new long[] { 1, 2, 3 }, rig.Annotations(alice).Select(notice => notice.Sequence));
        AssertEmptyLayer(rig.Annotations(alice)[0]);

        await rig.Router.PublishAnnotationAsync(LayerJson());
        await rig.Router.PublishAnnotationAsync(StrokeJson());
        var bob = rig.Join("bob", "Bob");
        await rig.Router.AttachAnnotationPeerAsync(bob, rig.Channel(bob));
        // 전체 상태인 레이어 스냅샷 이후만 복원한다.
        Assert.Equal(new long[] { 4, 5 }, rig.Annotations(bob).Select(notice => notice.Sequence));
        Assert.True(rig.Router.IsAnnotationReplayComplete);

        var restarted = Guid.NewGuid();
        rig.Router.EndAnnotationSharing();
        rig.Router.BeginAnnotationSharing(restarted);
        Assert.Equal(1, rig.Router.AnnotationReplayCount); // 새 공유의 빈 전체 상태
        await rig.Router.PublishAnnotationAsync(StrokeJson());
        var last = rig.Annotations(alice).Last();
        Assert.Equal(restarted, last.SharingId);
        Assert.Equal(8, last.Sequence); // 종료 초기화(6), 시작 초기화(7), 선(8)
    }

    [Fact]
    public async Task RemovedStudent_NoLongerReceivesAnnotations_AndStaleAttachIsRejected()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("alice", "Alice");
        rig.Router.BeginAnnotationSharing(Guid.NewGuid());
        await rig.Router.AttachAnnotationPeerAsync(alice, rig.Channel(alice));

        rig.Registry.Disconnect("alice");
        Assert.Equal(0, await rig.Router.PublishAnnotationAsync(StrokeJson()));
        AssertEmptyLayer(Assert.Single(rig.Annotations(alice))); // 접속 때 받은 초기화 외 추가 전달 없음
        await Assert.ThrowsAsync<CollaborationException>(() => rig.Router.AttachAnnotationPeerAsync(alice, rig.Channel(alice)));
    }

    // ---------- 학생 클라이언트 ----------

    [Fact]
    public void ClientTarget_UsesSharedIdentityRule_AfterStatusSnapshot()
    {
        var client = new ClientRig();
        Assert.Null(client.Reverse.Target);

        client.ApplyRoom();

        var target = client.Reverse.Target!;
        Assert.Equal(client.Self.ConnectionId, target.ConnectionId);
        Assert.Equal(ReverseRdpIdentity.For(client.Self), target.StudentId);
        Assert.Equal(ReverseRdpIdentity.For(client.Professor), target.ProfessorId);
    }

    [Fact]
    public async Task ClientSendInvitation_ValidatesBeforeSending_AndKeepsInvitationBeforeSecret()
    {
        var client = new ClientRig();
        client.ApplyRoom();
        var invitation = client.Invitation(Guid.NewGuid());

        await Assert.ThrowsAsync<CollaborationException>(() => client.Reverse.SendInvitationAsync(
            invitation with { StudentId = "someone-else" }, Secret(invitation)));
        Assert.Empty(client.Channel.Frames);

        await client.Reverse.SendInvitationAsync(invitation, Secret(invitation));
        Assert.Equal(new[] { CollaborationMessageKind.ReverseRdpInvitation, CollaborationMessageKind.ReverseRdpInvitationSecret },
            client.Channel.Frames.Select(frame => CollaborationFrameInspector.PeekKind(frame)));

        var rejected = new List<(Guid, CollaborationError)>();
        client.Reverse.InvitationRejected += (id, error) => rejected.Add((id, error));
        Assert.False(client.Reverse.HandleFailure(new CollaborationFailureNotice(Guid.NewGuid(), CollaborationError.FileUnavailable)));
        Assert.True(client.Reverse.HandleFailure(new CollaborationFailureNotice(invitation.InvitationId, CollaborationError.StaleConnection)));
        Assert.Equal(new[] { (invitation.InvitationId, CollaborationError.StaleConnection) }, rejected);
    }

    [Fact]
    public async Task ClientAnnotation_AppliesInOrder_AndSkipsDuplicateOutOfOrderAndForeignSender()
    {
        var client = new ClientRig();
        client.ApplyRoom();
        var applied = new List<long>();
        client.Reverse.AnnotationRenderer = notice => { applied.Add(notice.Sequence); return Task.CompletedTask; };
        var sharing = Guid.NewGuid();

        await client.ReceiveAsync(client.Annotation(sharing, 1));
        await client.ReceiveAsync(client.Annotation(sharing, 3));
        await client.ReceiveAsync(client.Annotation(sharing, 3));
        await client.ReceiveAsync(client.Annotation(sharing, 2));
        await client.ReceiveAsync(client.Annotation(sharing, 4) with { ConnectionId = client.Self.ConnectionId });
        await client.ReceiveAsync(client.Annotation(sharing, 5) with { SessionId = Guid.NewGuid() });

        Assert.Equal(new long[] { 1, 3 }, applied);
        Assert.Equal(3, client.Reverse.LastAppliedAnnotationSequence);
    }

    [Fact]
    public async Task ClientAnnotation_NewSharingResets_AndOldSharingIsRejected()
    {
        var client = new ClientRig();
        client.ApplyRoom();
        var applied = new List<(Guid, long)>();
        var changes = new List<Guid>();
        client.Reverse.AnnotationRenderer = notice => { applied.Add((notice.SharingId, notice.Sequence)); return Task.CompletedTask; };
        client.Reverse.AnnotationSharingChanged += changes.Add;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await client.ReceiveAsync(client.Annotation(first, 5));
        await client.ReceiveAsync(client.Annotation(second, 6));
        await client.ReceiveAsync(client.Annotation(first, 7));

        Assert.Equal(new[] { (first, 5L), (second, 6L) }, applied);
        Assert.Equal(new[] { first, second }, changes);
    }

    [Fact]
    public async Task ClientAnnotation_BeforeProfessorKnown_OrWithoutRenderer_OrRendererFailure_DoesNotAdvance()
    {
        var client = new ClientRig();
        var sharing = Guid.NewGuid();
        await client.ReceiveAsync(client.Annotation(sharing, 1));
        Assert.Equal(Guid.Empty, client.Reverse.AnnotationSharingId);

        client.ApplyRoom();
        await client.ReceiveAsync(client.Annotation(sharing, 1));
        Assert.Equal(0, client.Reverse.LastAppliedAnnotationSequence);

        client.Reverse.AnnotationRenderer = _ => throw new InvalidOperationException("render");
        await client.ReceiveAsync(client.Annotation(sharing, 2));
        Assert.Equal(0, client.Reverse.LastAppliedAnnotationSequence);

        var applied = new List<long>();
        client.Reverse.AnnotationRenderer = notice => { applied.Add(notice.Sequence); return Task.CompletedTask; };
        Assert.True(await client.Reverse.AnnotationRecovery);
        await client.ReceiveAsync(client.Annotation(sharing, 2));
        Assert.Equal(new long[] { 1, 2 }, applied);
    }

    // ---------- 실제 TLS 보호 채널 + SessionManager ----------

    [Fact]
    public async Task EndToEnd_StudentInvitationReachesProfessor_AndForgedInvitationIsRejected()
    {
        await using var rig = await SecureRig.OpenAsync();
        var ready = new ConcurrentQueue<ReverseInvitationDelivery>();
        rig.SessionManager.ReverseCollaboration!.InvitationReady += ready.Enqueue;
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");
        await WaitUntilAsync(() => alice.Reverse.Target is not null && bob.Reverse.Target is not null);

        var invitation = Invitation(alice.Reverse.Target!, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await alice.Reverse.SendInvitationAsync(invitation, Secret(invitation));
        await WaitUntilAsync(() => !ready.IsEmpty);
        var delivery = Assert.Single(ready);
        Assert.Equal("Alice", delivery.DisplayName);
        Assert.Equal(ReverseRdpIdentity.For(delivery.Student), invitation.StudentId);
        Assert.Equal(rig.SessionManager.ReverseCollaboration.ProfessorId, invitation.ProfessorId);

        // Bob이 클라이언트 검사를 우회해 Alice 신원을 주장하는 초대를 보호 채널로 직접 보내면 서버가 거부하고 Bob에게만 알린다.
        var forged = Invitation(bob.Reverse.Target!, Guid.NewGuid(), DateTimeOffset.UtcNow) with { StudentId = invitation.StudentId };
        await bob.Secure.Connection.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), forged));
        await bob.Secure.Connection.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), Secret(forged)));
        await WaitUntilAsync(() => bob.Failures.Count(failure => failure.RequestId == forged.InvitationId) == 2);

        Assert.Single(ready);
        Assert.Null(rig.SessionManager.ReverseCollaboration.TryGetInvitation(bob.Reverse.Target!.ConnectionId));
        Assert.Empty(alice.Failures);
    }

    [Fact]
    public async Task EndToEnd_AnnotationReachesStudents_AndReconnectReplaysCurrentSharing()
    {
        await using var rig = await SecureRig.OpenAsync();
        var sharing = Guid.NewGuid();
        rig.SessionManager.AttachRdpSharing(new NoopSharing(), sharing);
        var alice = await rig.JoinAsync("Alice");
        await WaitUntilAsync(() => alice.Reverse.Target is not null);

        await rig.SessionManager.PublishAnnotationAsync(StrokeJson());
        await rig.SessionManager.PublishAnnotationAsync(StrokeJson());
        await WaitUntilAsync(() => alice.Applied.Count == 3);
        Assert.Equal(new long[] { 1, 2, 3 }, alice.Applied.Select(notice => notice.Sequence));
        AssertEmptyLayer(alice.Applied.First());

        // 늦게 들어온 학생은 연결 직후 현재 공유의 판서를 순서대로 복원받는다.
        var bob = await rig.JoinAsync("Bob");
        await WaitUntilAsync(() => bob.Applied.Count == 3);
        Assert.Equal(new long[] { 1, 2, 3 }, bob.Applied.Select(notice => notice.Sequence));
        Assert.All(bob.Applied, notice => Assert.Equal(sharing, notice.SharingId));

        // 공유를 멈추면 판서 송신은 실패로 드러나고, 재시작 후에는 새 공유 세대만 전달된다.
        await rig.SessionManager.DetachRdpSharingAsync();
        await WaitUntilAsync(() => alice.Applied.Count == 4 && bob.Applied.Count == 4);
        AssertEmptyLayer(alice.Applied.Last());
        AssertEmptyLayer(bob.Applied.Last());
        await Assert.ThrowsAsync<CollaborationException>(() => rig.SessionManager.PublishAnnotationAsync(StrokeJson()));
        var restarted = Guid.NewGuid();
        rig.SessionManager.AttachRdpSharing(new NoopSharing(), restarted);
        await WaitUntilAsync(() => alice.Applied.Count == 5 && bob.Applied.Count == 5);
        AssertEmptyLayer(alice.Applied.Last());
        AssertEmptyLayer(bob.Applied.Last());
        Assert.Equal(restarted, alice.Reverse.AnnotationSharingId);
        Assert.Equal(restarted, bob.Reverse.AnnotationSharingId);
        await rig.SessionManager.PublishAnnotationAsync(StrokeJson());
        await WaitUntilAsync(() => alice.Applied.Count == 6 && bob.Applied.Count == 6);
        Assert.Equal(restarted, alice.Applied.Last().SharingId);
    }

    // ---------- 도우미 ----------

    private static ReverseRdpInvitationNotice Invitation(ReverseRdpInvitationTarget target, Guid sharingId, DateTimeOffset now)
    {
        const string connectionString = "<E><A KH=\"x\"/></E>";
        return new ReverseRdpInvitationNotice
        {
            ContractVersion = ReverseRdpInvitationNotice.CurrentVersion,
            Provider = ReverseRdpInvitationNotice.ProviderName,
            Direction = ReverseRdpInvitationNotice.StudentToProfessor,
            SessionId = target.SessionId, SharingId = sharingId, InvitationId = Guid.NewGuid(),
            ConnectionId = target.ConnectionId, ProfessorId = target.ProfessorId, StudentId = target.StudentId,
            ParticipantId = target.ProfessorId, ConnectionString = connectionString,
            DataLength = Encoding.UTF8.GetByteCount(connectionString), ExpiresAt = now.AddMinutes(5),
            ControlMode = ReverseRdpControlMode.HostGrantedInteractive, ViewOnly = false
        };
    }

    private static ReverseRdpInvitationSecretNotice Secret(ReverseRdpInvitationNotice invitation) =>
        new(invitation.SessionId, invitation.SharingId, invitation.InvitationId, invitation.ConnectionId,
            invitation.StudentId, invitation.ProfessorId, "pw-secret", invitation.ExpiresAt);

    private static string StrokeJson() => AnnotationStrokeWire.ToJson(new AnnotationStroke
    {
        ParticipantId = "professor", Tool = EduStream.Server.Rdp.AnnotationTool.Pen,
        Points = new[] { new Point(0, 0), new Point(10, 20) }
    });

    private static string LayerJson() => AnnotationLayerWire.ToJson(new AnnotationLayerSnapshot
    {
        Change = AnnotationLayerChange.Undone, IsVisible = true, ContentRevision = 1,
        VisibleStrokes = new[] { new AnnotationStroke
        {
            ParticipantId = "professor", Tool = EduStream.Server.Rdp.AnnotationTool.Line,
            Points = new[] { new Point(1, 1), new Point(5, 5) }
        } }
    });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건이 제한 시간 안에 충족되지 않았습니다.");
            await Task.Delay(20);
        }
    }

    private sealed class RecordingChannel : ICollaborationChannel
    {
        public ConcurrentQueue<byte[]> Frames { get; } = new();

        public Func<byte[], Task>? Receiver { get; set; }
        public async Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        {
            Frames.Enqueue(frame);
            if (Receiver is not null) await Receiver(frame);
        }
    }

    private sealed class RouterRig : IDisposable
    {
        private readonly Dictionary<Guid, RecordingChannel> _channels = new();

        public RouterRig(ILogSink? log = null)
        {
            Professor = new ParticipantConnection(SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
            Router = new ReverseCollaborationRouter(Professor, Registry, log ?? new InMemoryLogSink(), () => Now);
        }

        public Guid SessionId { get; } = Guid.NewGuid();
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public ParticipantRegistry Registry { get; } = new();
        public ParticipantConnection Professor { get; }
        public ReverseCollaborationRouter Router { get; }

        public ParticipantConnection Join(string clientId, string displayName) =>
            Registry.Join(clientId, SessionId, displayName, ParticipantRole.Student);

        public RecordingChannel Channel(ParticipantConnection student)
        {
            if (!_channels.TryGetValue(student.ConnectionId, out var channel))
                _channels[student.ConnectionId] = channel = new RecordingChannel();
            return channel;
        }

        public ReverseRdpInvitationNotice Invitation(ParticipantConnection student, Guid sharingId) =>
            ReverseCollaborationRoutingTests.Invitation(new ReverseRdpInvitationTarget(SessionId, student.ConnectionId,
                ReverseRdpIdentity.For(student), ReverseRdpIdentity.For(Professor)), sharingId, Now);

        public Task SendAsync<T>(ParticipantConnection student, T payload) where T : notnull =>
            Router.HandleFrameAsync(student, Channel(student), CollaborationMessageCodec.Encode(Guid.NewGuid(), payload));

        public async Task DeliverAsync(ParticipantConnection student, ReverseRdpInvitationNotice invitation)
        {
            await SendAsync(student, invitation);
            await SendAsync(student, Secret(invitation));
        }

        public CollaborationFailureNotice[] Failures(ParticipantConnection student) =>
            Decode<CollaborationFailureNotice>(student, CollaborationMessageKind.Failure);

        public AnnotationTransportNotice[] Annotations(ParticipantConnection student) =>
            Decode<AnnotationTransportNotice>(student, CollaborationMessageKind.Annotation);

        private T[] Decode<T>(ParticipantConnection student, CollaborationMessageKind kind) where T : notnull =>
            Channel(student).Frames.Where(frame => CollaborationFrameInspector.PeekKind(frame) == kind)
                .Select(frame => CollaborationMessageCodec.Decode<T>(frame, out _)).ToArray();

        public void Dispose() => Router.Dispose();
    }

    private sealed class ClientRig
    {
        public ClientRig()
        {
            Self = new ParticipantConnection(SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
            Professor = new ParticipantConnection(SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
            Reverse = new ReverseCollaborationClient(SessionId, Channel, new InMemoryLogSink());
        }

        public Guid SessionId { get; } = Guid.NewGuid();
        public ParticipantConnection Self { get; }
        public ParticipantConnection Professor { get; }
        public RecordingChannel Channel { get; } = new();
        public ReverseCollaborationClient Reverse { get; }

        public void ApplyRoom() => Reverse.ApplyRoom(new RoomJoined(Self, 1, new[]
        {
            new ParticipantSnapshot(Self, "Alice", true, true, true, 0),
            new ParticipantSnapshot(Professor, "교수자", true, false, false, 0)
        }));

        public ReverseRdpInvitationNotice Invitation(Guid sharingId) =>
            ReverseCollaborationRoutingTests.Invitation(Reverse.Target!, sharingId, DateTimeOffset.UtcNow);

        public AnnotationTransportNotice Annotation(Guid sharingId, long sequence) =>
            new(SessionId, sharingId, Professor.ConnectionId, sequence, StrokeJson());

        public Task ReceiveAsync(AnnotationTransportNotice notice) =>
            Reverse.HandleFrameAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), notice));
    }

    private sealed class NoopSharing : IRdpSharingService
    {
        public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());
        public Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId, string participantId,
            Guid connectionId, string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Student(TcpClientService tcp, SecureSessionChannel secure, ReverseCollaborationClient reverse)
    {
        public TcpClientService Tcp { get; } = tcp;
        public SecureSessionChannel Secure { get; } = secure;
        public ReverseCollaborationClient Reverse { get; } = reverse;
        public ConcurrentQueue<AnnotationTransportNotice> Applied { get; } = new();
        public ConcurrentQueue<CollaborationFailureNotice> Failures { get; } = new();
    }

    private sealed class SecureRig : IAsyncDisposable
    {
        private readonly List<Student> _students = new();
        private readonly PacketSerializer _serializer = new();

        public X509Certificate2 Certificate { get; } = ProfessorCertificateStore.CreateEphemeral();
        public SessionManager SessionManager { get; }
        public int Port { get; private set; }

        private SecureRig()
        {
            var log = new InMemoryLogSink();
            SessionManager = new SessionManager(log, new TcpServerService(log, new PacketSerializer()));
        }

        public static async Task<SecureRig> OpenAsync()
        {
            var rig = new SecureRig();
            for (var attempt = 0; ; attempt++)
            {
                rig.Port = TestPortAllocator.GetFreePortPair();
                try
                {
                    await rig.SessionManager.OpenSessionAsync("ReverseRoutingTest", rig.Port, default, rig.Certificate);
                    return rig;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 5) { }
            }
        }

        public async Task<Student> JoinAsync(string displayName)
        {
            var secure = await SecureRoomJoinClient.AuthenticateAsync("127.0.0.1", Port, SessionManager.ConnectionCode!,
                displayName, ReadOnlyMemory<char>.Empty, new InMemoryLogSink(), Wait);
            var status = new StudentStatusClient(secure.SessionId, secure.Connection, new InMemoryLogSink());
            var reverse = new ReverseCollaborationClient(secure.SessionId, secure.Connection, new InMemoryLogSink());
            status.RoomChanged += reverse.ApplyRoom;
            var tcp = new TcpClientService(new InMemoryLogSink(), _serializer);
            var student = new Student(tcp, secure, reverse);
            reverse.AnnotationRenderer = notice => { student.Applied.Enqueue(notice); return Task.CompletedTask; };
            // ClientViewModel.AttachStudentStatus와 같은 분기 순서.
            secure.FrameReceived += frame =>
            {
                var kind = CollaborationFrameInspector.PeekKind(frame);
                if (StudentStatusClient.Handles(kind)) status.HandleFrame(frame);
                else if (ReverseCollaborationClient.Handles(kind)) return reverse.HandleFrameAsync(frame);
                else if (kind == CollaborationMessageKind.Failure)
                {
                    var failure = CollaborationMessageCodec.Decode<CollaborationFailureNotice>(frame, out _);
                    student.Failures.Enqueue(failure);
                    reverse.HandleFailure(failure);
                }
                return Task.CompletedTask;
            };

            var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcp.PacketReceived += (type, payload) =>
            {
                if (type == PacketType.Ack && _serializer.Deserialize<AckPacket>(payload)?.AckCode == AckCodes.SessionJoined)
                    joined.TrySetResult();
                return Task.CompletedTask;
            };
            await tcp.ConnectAsync("127.0.0.1", Port);
            await tcp.SendAsync(PacketFactory.CreateSessionJoin(displayName, displayName, "127.0.0.1", Port, secure.JoinTicket));
            await joined.Task.WaitAsync(Wait);
            _students.Add(student);
            return student;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var student in _students)
            {
                try { student.Tcp.Dispose(); } catch { }
                await student.Secure.DisposeAsync();
            }
            await SessionManager.CloseSessionAsync();
            Certificate.Dispose();
        }
    }
}
