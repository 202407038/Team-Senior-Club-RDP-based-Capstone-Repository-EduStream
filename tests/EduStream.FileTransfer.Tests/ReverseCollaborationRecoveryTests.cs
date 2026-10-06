using System.Collections.Concurrent;
using System.Text.Json;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.Server.Rdp;
using System.Windows.Threading;

namespace EduStream.FileTransfer.Tests;

public sealed partial class ReverseCollaborationRoutingTests
{
    private static void AssertEmptyLayer(AnnotationTransportNotice notice)
    {
        using var json = JsonDocument.Parse(notice.PayloadJson);
        Assert.Equal("annotation-layer", json.RootElement.GetProperty("Kind").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("Strokes").GetArrayLength());
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("disconnect")]
    [InlineData("dispose")]
    public async Task PendingReadyNotification_CannotFollowWithdrawal(string reason)
    {
        var log = new PausedReadyLog();
        using var rig = new RouterRig(log);
        var student = rig.Join("a", "Alice");
        var notifications = new ConcurrentQueue<string>();
        rig.Router.InvitationReady += _ => notifications.Enqueue("ready");
        rig.Router.InvitationWithdrawn += (_, _) => notifications.Enqueue("withdrawn");
        var invitation = rig.Invitation(student, Guid.NewGuid());
        await rig.SendAsync(student, invitation);
        var pending = Task.Run(() => rig.SendAsync(student, Secret(invitation)));
        try
        {
            await log.Reached.Task.WaitAsync(Wait);
            switch (reason)
            {
                case "permission": rig.Registry.SetPermissions(student.ConnectionId, false, false); break;
                case "disconnect": rig.Registry.Disconnect("a"); break;
                default: rig.Router.Dispose(); break;
            }
        }
        finally { log.Resume.Set(); }
        await pending.WaitAsync(Wait);
        Assert.Equal(new[] { "withdrawn" }, notifications);
        Assert.Null(rig.Router.TryGetInvitation(student.ConnectionId));
    }

    [Fact]
    public async Task ReentrantRevocation_IsDeliveredAfterEveryReadySubscriber()
    {
        using var rig = new RouterRig();
        var student = rig.Join("a", "Alice");
        var notifications = new List<string>();
        rig.Router.InvitationReady += _ =>
        {
            notifications.Add("first-ready");
            rig.Registry.SetPermissions(student.ConnectionId, false, false);
        };
        rig.Router.InvitationReady += _ => notifications.Add("second-ready");
        rig.Router.InvitationWithdrawn += (_, _) => notifications.Add("withdrawn");
        await rig.DeliverAsync(student, rig.Invitation(student, Guid.NewGuid()));
        Assert.Equal(new[] { "first-ready", "second-ready", "withdrawn" }, notifications);
    }

    [Fact]
    public async Task ThrowingInvitationConsumer_DoesNotLoseOtherConsumersOrWithdrawal()
    {
        using var rig = new RouterRig();
        var student = rig.Join("a", "Alice");
        var notifications = new List<string>();
        rig.Router.InvitationReady += _ => throw new InvalidOperationException("consumer");
        rig.Router.InvitationReady += _ => notifications.Add("ready");
        rig.Router.InvitationWithdrawn += (_, _) => notifications.Add("withdrawn");
        await rig.DeliverAsync(student, rig.Invitation(student, Guid.NewGuid()));
        rig.Registry.Disconnect("a");
        Assert.Equal(new[] { "ready", "withdrawn" }, notifications);
    }

    [Fact]
    public async Task RendererAttachedAfterReplay_AppliesEveryPendingFrameWithoutNewTraffic()
    {
        var rig = new ClientRig();
        rig.ApplyRoom();
        var sharing = Guid.NewGuid();
        await rig.ReceiveAsync(rig.Annotation(sharing, 1));
        await rig.ReceiveAsync(rig.Annotation(sharing, 2));
        Assert.True(rig.Reverse.AnnotationRecoveryRequired);
        var applied = new List<long>();
        rig.Reverse.AnnotationRenderer = n => { applied.Add(n.Sequence); return Task.CompletedTask; };
        Assert.True(await rig.Reverse.AnnotationRecovery.WaitAsync(Wait));
        Assert.Equal(new long[] { 1, 2 }, applied);
        Assert.Equal(2, rig.Reverse.LastAppliedAnnotationSequence);
        Assert.False(rig.Reverse.AnnotationRecoveryRequired);
    }

    [Fact]
    public async Task TransientRendererFailure_RetriesWithoutNewTraffic()
    {
        var rig = new ClientRig();
        rig.ApplyRoom();
        var calls = 0;
        rig.Reverse.AnnotationRenderer = _ =>
            ++calls == 1 ? Task.FromException(new InvalidOperationException("transient")) : Task.CompletedTask;
        await rig.ReceiveAsync(rig.Annotation(Guid.NewGuid(), 1));
        Assert.Equal(2, calls);
        Assert.Equal(1, rig.Reverse.LastAppliedAnnotationSequence);
        Assert.False(rig.Reverse.AnnotationRecoveryRequired);
    }

    [Fact]
    public async Task PersistentFailure_DoesNotSkipHead_AndRecoversInOrder()
    {
        var rig = new ClientRig();
        rig.ApplyRoom();
        var sharing = Guid.NewGuid();
        var attempts = new List<long>();
        rig.Reverse.AnnotationRenderer = n =>
        {
            attempts.Add(n.Sequence);
            return Task.FromException(new InvalidOperationException("renderer unavailable"));
        };
        await rig.ReceiveAsync(rig.Annotation(sharing, 1));
        await rig.ReceiveAsync(rig.Annotation(sharing, 2));
        Assert.All(attempts, n => Assert.Equal(1, n));
        Assert.Equal(0, rig.Reverse.LastAppliedAnnotationSequence);
        Assert.True(rig.Reverse.AnnotationRecoveryRequired);
        var applied = new List<long>();
        rig.Reverse.AnnotationRenderer = n => { applied.Add(n.Sequence); return Task.CompletedTask; };
        Assert.True(await rig.Reverse.AnnotationRecovery);
        Assert.Equal(new long[] { 1, 2 }, applied);
        await rig.ReceiveAsync(rig.Annotation(sharing, 1));
        Assert.Equal(new long[] { 1, 2 }, applied); // 실제 적용 뒤에만 재전달 거부
    }

    [Fact]
    public async Task FullSnapshot_ReplacesFailedOrUnrenderedHistory()
    {
        var rig = new ClientRig();
        rig.ApplyRoom();
        var sharing = Guid.NewGuid();
        await rig.ReceiveAsync(rig.Annotation(sharing, 1));
        var layer = LayerJson();
        await rig.ReceiveAsync(rig.Annotation(sharing, 2) with { PayloadJson = layer });
        var applied = new List<AnnotationTransportNotice>();
        rig.Reverse.AnnotationRenderer = n => { applied.Add(n); return Task.CompletedTask; };
        Assert.True(await rig.Reverse.AnnotationRecovery);
        Assert.Equal(2, Assert.Single(applied).Sequence);
        Assert.Equal(layer, applied[0].PayloadJson);
    }

    [Fact]
    public async Task PendingLimit_IsReported_AndFullSnapshotRestoresCompleteness()
    {
        var rig = new ClientRig();
        rig.ApplyRoom();
        var sharing = Guid.NewGuid();
        var stroke = StrokeJson();
        for (var i = 1; i <= ReverseCollaborationClient.MaxPendingAnnotationFrames + 1; i++)
            await rig.ReceiveAsync(rig.Annotation(sharing, i) with { PayloadJson = stroke });
        Assert.True(rig.Reverse.AnnotationRecoveryRequired);
        var applied = 0;
        rig.Reverse.AnnotationRenderer = _ => { applied++; return Task.CompletedTask; };
        Assert.False(await rig.Reverse.AnnotationRecovery); // 보관 상한 이후 한 건은 누락됨
        Assert.Equal(ReverseCollaborationClient.MaxPendingAnnotationFrames, applied);
        await rig.ReceiveAsync(rig.Annotation(sharing, ReverseCollaborationClient.MaxPendingAnnotationFrames + 2)
            with { PayloadJson = LayerJson() });
        Assert.False(rig.Reverse.AnnotationRecoveryRequired);
    }

    [Fact]
    public async Task StartStopRestart_SendEmptyStateWithoutAnyNewStroke()
    {
        using var rig = new RouterRig();
        var student = rig.Join("a", "Alice");
        var client = new ReverseCollaborationClient(rig.SessionId, new RecordingChannel(), new InMemoryLogSink());
        client.ApplyRoom(new RoomJoined(student, 1, new[]
        {
            new ParticipantSnapshot(student, "Alice", true, true, true, 0),
            new ParticipantSnapshot(rig.Professor, "Professor", true, false, false, 0)
        }));
        var visible = new List<long>();
        client.AnnotationRenderer = n =>
        {
            using var json = JsonDocument.Parse(n.PayloadJson);
            if (json.RootElement.TryGetProperty("Kind", out _)) visible.Clear();
            else visible.Add(n.Sequence);
            return Task.CompletedTask;
        };
        rig.Channel(student).Receiver = client.HandleFrameAsync;
        await rig.Router.AttachAnnotationPeerAsync(student, rig.Channel(student));
        rig.Router.BeginAnnotationSharing(Guid.NewGuid());
        await rig.Router.PublishAnnotationAsync(StrokeJson());
        Assert.Single(visible);
        rig.Router.EndAnnotationSharing();
        await rig.Router.WaitForAnnotationDeliveryAsync().WaitAsync(Wait);
        Assert.Empty(visible);
        var restarted = Guid.NewGuid();
        rig.Router.BeginAnnotationSharing(restarted);
        await rig.Router.WaitForAnnotationDeliveryAsync().WaitAsync(Wait);
        Assert.Empty(visible);
        Assert.Equal(restarted, client.AnnotationSharingId);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, rig.Annotations(student).Select(n => n.Sequence));
    }

    [Fact]
    public async Task InFlightStroke_CannotOvertakeStopOrRestartReset()
    {
        using var rig = new RouterRig();
        var student = rig.Join("a", "Alice");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharing = Guid.NewGuid();
        rig.Router.BeginAnnotationSharing(sharing);
        await rig.Router.AttachAnnotationPeerAsync(student, rig.Channel(student));
        rig.Channel(student).Receiver = async frame =>
        {
            var n = CollaborationMessageCodec.Decode<AnnotationTransportNotice>(frame, out _);
            if (n.Sequence != 2) return;
            entered.TrySetResult();
            await resume.Task.WaitAsync(Wait);
        };
        var pending = rig.Router.PublishAnnotationAsync(StrokeJson());
        await entered.Task.WaitAsync(Wait);
        rig.Router.EndAnnotationSharing();
        var restarted = Guid.NewGuid();
        rig.Router.BeginAnnotationSharing(restarted);
        resume.TrySetResult();
        await pending;
        await rig.Router.WaitForAnnotationDeliveryAsync().WaitAsync(Wait);
        var notices = rig.Annotations(student);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, notices.Select(n => n.Sequence));
        Assert.Equal(sharing, notices[2].SharingId);
        Assert.Equal(restarted, notices[3].SharingId);
        AssertEmptyLayer(notices[2]);
        AssertEmptyLayer(notices[3]);
    }

    private sealed class PausedReadyLog : ILogSink
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Resume { get; } = new(false);
        public void Write(string message)
        {
            if (!message.StartsWith("[Reverse] 역방향 초대 준비:")) return;
            Reached.TrySetResult();
            if (!Resume.Wait(Wait)) throw new TimeoutException("ready pause");
        }
        public IReadOnlyList<string> Snapshot() => Array.Empty<string>();
    }

    [Fact]
    public async Task FailedReplay_DetachesPeer_WithoutPoisoningOtherStudentsOrNextAttach()
    {
        using var rig = new RouterRig();
        var alice = rig.Join("a", "Alice");
        var bob = rig.Join("b", "Bob");
        rig.Router.BeginAnnotationSharing(Guid.NewGuid());
        rig.Channel(alice).Receiver = _ => Task.FromException(new IOException("replay write failed"));
        await Assert.ThrowsAsync<CollaborationException>(() =>
            rig.Router.AttachAnnotationPeerAsync(alice, rig.Channel(alice)));
        await rig.Router.AttachAnnotationPeerAsync(bob, rig.Channel(bob));
        Assert.Equal(1, await rig.Router.PublishAnnotationAsync(StrokeJson()));
        Assert.Single(rig.Annotations(alice)); // 실패한 첫 복원 시도 뒤 추가 전송 없음
        rig.Channel(alice).Receiver = null;
        await rig.Router.AttachAnnotationPeerAsync(alice, rig.Channel(alice));
        Assert.Equal(2, await rig.Router.PublishAnnotationAsync(StrokeJson()));
    }

    [Fact]
    public Task DelayedRealOverlay_RendersBufferedStroke_AndClearsOnStop() => OnAnnotationSta(async () =>
    {
        using var rig = new RouterRig();
        var student = rig.Join("a", "Alice");
        var client = new ReverseCollaborationClient(rig.SessionId, new RecordingChannel(), new InMemoryLogSink());
        client.ApplyRoom(new RoomJoined(student, 1, new[]
        {
            new ParticipantSnapshot(student, "Alice", true, true, true, 0),
            new ParticipantSnapshot(rig.Professor, "Professor", true, false, false, 0)
        }));
        rig.Channel(student).Receiver = client.HandleFrameAsync;
        rig.Router.BeginAnnotationSharing(Guid.NewGuid());
        await rig.Router.AttachAnnotationPeerAsync(student, rig.Channel(student));
        await rig.Router.PublishAnnotationAsync(StrokeJson());
        using var overlay = new AnnotationOverlayLayer(100, 100);
        client.AnnotationRenderer = n => overlay.ReceiveRemoteStrokeJsonAsync(n.PayloadJson);
        Assert.True(await client.AnnotationRecovery.WaitAsync(Wait));
        Assert.Equal(1, overlay.ShapeCount);
        rig.Router.EndAnnotationSharing();
        await rig.Router.WaitForAnnotationDeliveryAsync().WaitAsync(Wait);
        Assert.Equal(0, overlay.ShapeCount);
        Assert.False(client.AnnotationRecoveryRequired);
    });

    private static Task OnAnnotationSta(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(Wait);
    }
}
