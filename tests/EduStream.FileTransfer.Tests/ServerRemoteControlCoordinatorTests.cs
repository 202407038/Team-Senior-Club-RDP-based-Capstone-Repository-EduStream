using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class ServerRemoteControlCoordinatorTests
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task RequestAsync_DefaultPermission_BecomesActiveOnlyAfterInputGranted()
    {
        var rig = new Rig();
        var student = rig.Join("client-1");
        var grant = rig.Gate.BlockNextGrant();

        // U07: 제어 허용 기본 ON이므로 학생이 따로 켜지 않아도 요청할 수 있다.
        var request = rig.Coordinator.RequestAsync(student);
        await grant.Entered.WaitAsync(DefaultWait);
        Assert.Equal(ControlPhase.Requested, rig.Coordinator.Current!.Phase);

        grant.Release();
        await request.WaitAsync(DefaultWait);

        Assert.Equal(ControlPhase.Active, rig.Coordinator.Current!.Phase);
        Assert.Equal(student, rig.Coordinator.Current.Student);
    }

    [Fact]
    public async Task RequestAsync_StudentTurnedControlOff_IsDenied()
    {
        var rig = new Rig();
        var student = rig.Join("client-1");
        rig.Registry.SetPermissions(student.ConnectionId, allowViewing: true, allowControl: false);

        var error = await Assert.ThrowsAsync<CollaborationException>(() => rig.Coordinator.RequestAsync(student));

        Assert.Equal(CollaborationError.PermissionDenied, error.Code);
        Assert.Null(rig.Coordinator.Current);
        Assert.Empty(rig.Gate.Grants);
    }

    [Fact]
    public async Task RequestAsync_WithoutInputEngine_FailsInsteadOfActive()
    {
        var rig = new Rig(UnavailableRemoteInputGate.Instance);
        var student = rig.Join("client-1");

        var error = await Assert.ThrowsAsync<CollaborationException>(() => rig.Coordinator.RequestAsync(student));

        Assert.Equal(CollaborationError.UnsupportedCapability, error.Code);
        Assert.Equal(ControlPhase.Failed, rig.Coordinator.Current!.Phase);
        Assert.DoesNotContain(rig.Events, e => e.Phase == ControlPhase.Active);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task PermissionTurnedOff_WhileActive_RevokesApprovalAndInput(bool allowViewing, bool allowControl)
    {
        var rig = new Rig();
        var student = rig.Join("client-1");
        await rig.Coordinator.RequestAsync(student);
        Assert.Equal(ControlPhase.Active, rig.Coordinator.Current!.Phase);

        rig.Registry.SetPermissions(student.ConnectionId, allowViewing, allowControl);

        // 승인 회수는 권한 변경 즉시 반영된다.
        Assert.Equal(ControlPhase.Revoked, rig.Coordinator.Current!.Phase);
        await rig.Coordinator.ConfirmInputRevokedAsync().WaitAsync(DefaultWait);
        Assert.Contains(rig.Gate.Revokes, r => r.Student == student);
        Assert.False(rig.Coordinator.IsInputRevokePending);
    }

    [Fact]
    public async Task PermissionTurnedOff_WhileGrantPending_NeverBecomesActive()
    {
        var rig = new Rig();
        var student = rig.Join("client-1");
        var grant = rig.Gate.BlockNextGrant(honorCancellation: true);

        var request = rig.Coordinator.RequestAsync(student);
        await grant.Entered.WaitAsync(DefaultWait);
        rig.Registry.SetPermissions(student.ConnectionId, allowViewing: true, allowControl: false);
        await request.WaitAsync(DefaultWait);

        Assert.True(grant.WasCancelled);
        Assert.Equal(ControlPhase.Revoked, rig.Coordinator.Current!.Phase);
        Assert.DoesNotContain(rig.Events, e => e.Phase == ControlPhase.Active);
    }

    [Fact]
    public async Task PermissionChangeOfOtherStudent_KeepsCurrentControl()
    {
        var rig = new Rig();
        var a = rig.Join("client-a");
        var b = rig.Join("client-b");
        await rig.Coordinator.RequestAsync(a);

        rig.Registry.SetPermissions(b.ConnectionId, allowViewing: false, allowControl: false);

        Assert.Equal(ControlPhase.Active, rig.Coordinator.Current!.Phase);
        Assert.Equal(a, rig.Coordinator.Current.Student);
    }

    [Fact]
    public async Task Disconnect_WhileActive_RevokesApprovalAndInput()
    {
        var rig = new Rig();
        var student = rig.Join("client-1");
        await rig.Coordinator.RequestAsync(student);

        rig.Registry.Disconnect("client-1");

        Assert.Equal(ControlPhase.Revoked, rig.Coordinator.Current!.Phase);
        await rig.Coordinator.ConfirmInputRevokedAsync().WaitAsync(DefaultWait);
        Assert.Contains(rig.Gate.Revokes, r => r.Student == student);
    }

    [Fact]
    public async Task Rejoin_ReplacingActiveConnection_RevokesOldConnection()
    {
        var rig = new Rig();
        var first = rig.Join("client-1");
        await rig.Coordinator.RequestAsync(first);

        rig.Join("client-1");

        Assert.Equal(ControlPhase.Revoked, rig.Coordinator.Current!.Phase);
        Assert.Equal(first, rig.Coordinator.Current.Student);
    }

    [Fact]
    public async Task SwitchingTarget_WaitsForPreviousInputRevokeBeforeRequestingNext()
    {
        var rig = new Rig();
        var a = rig.Join("client-a");
        var b = rig.Join("client-b");
        await rig.Coordinator.RequestAsync(a);
        var revoke = rig.Gate.BlockNextRevoke();

        var switchTask = rig.Coordinator.RequestAsync(b);
        await revoke.Entered.WaitAsync(DefaultWait);

        // A 입력 회수 확인 전에는 B를 요청하지도, 입력 허용을 시작하지도 않는다.
        Assert.Equal(ControlPhase.Revoked, rig.Coordinator.Current!.Phase);
        Assert.Equal(a, rig.Coordinator.Current.Student);
        Assert.DoesNotContain(rig.Gate.Grants, g => g.Student == b);

        revoke.Release();
        await switchTask.WaitAsync(DefaultWait);

        Assert.Equal(ControlPhase.Active, rig.Coordinator.Current!.Phase);
        Assert.Equal(b, rig.Coordinator.Current.Student);
        Assert.Equal(new[] { "grant:a", "revoke:a", "grant:b" }, rig.Gate.CallOrder(a, b));
    }

    [Fact]
    public async Task StopDuringTargetSwitch_DoesNotLeaveNewRequestBehind()
    {
        var rig = new Rig();
        var a = rig.Join("client-a");
        var b = rig.Join("client-b");
        await rig.Coordinator.RequestAsync(a);
        var revoke = rig.Gate.BlockNextRevoke();

        var switchTask = rig.Coordinator.RequestAsync(b);
        await revoke.Entered.WaitAsync(DefaultWait);
        var stopTask = rig.Coordinator.StopAsync();
        revoke.Release();
        await Task.WhenAll(switchTask, stopTask).WaitAsync(DefaultWait);

        Assert.Equal(ControlPhase.Revoked, rig.Coordinator.Current!.Phase);
        Assert.Equal(a, rig.Coordinator.Current.Student);
        Assert.DoesNotContain(rig.Events, e => e.Student == b);
        Assert.DoesNotContain(rig.Gate.Grants, g => g.Student == b);
    }

    [Fact]
    public async Task StopWhileGrantPending_RevokesAgainIfGrantCompletesLate()
    {
        var rig = new Rig();
        var student = rig.Join("client-1");
        // 취소를 무시하고 늦게 끝나는 native 허용을 흉내 낸다.
        var grant = rig.Gate.BlockNextGrant(honorCancellation: false);

        var request = rig.Coordinator.RequestAsync(student);
        await grant.Entered.WaitAsync(DefaultWait);
        await rig.Coordinator.StopAsync().WaitAsync(DefaultWait);
        var revokesAfterStop = rig.Gate.Revokes.Count;

        grant.Release();
        await request.WaitAsync(DefaultWait);

        Assert.Equal(ControlPhase.Revoked, rig.Coordinator.Current!.Phase);
        Assert.True(rig.Gate.Revokes.Count > revokesAfterStop);
        Assert.DoesNotContain(rig.Events, e => e.Phase == ControlPhase.Active);
    }

    [Fact]
    public async Task InputRevokeFailure_BlocksSwitchUntilRetrySucceeds()
    {
        var rig = new Rig();
        var a = rig.Join("client-a");
        var b = rig.Join("client-b");
        await rig.Coordinator.RequestAsync(a);
        rig.Gate.FailNextRevoke();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Coordinator.RequestAsync(b));

        Assert.Equal(ControlPhase.Revoked, rig.Coordinator.Current!.Phase);
        Assert.Equal(a, rig.Coordinator.Current.Student);
        Assert.True(rig.Coordinator.IsInputRevokePending);
        Assert.DoesNotContain(rig.Gate.Grants, g => g.Student == b);

        await rig.Coordinator.RequestAsync(b);

        Assert.Equal(ControlPhase.Active, rig.Coordinator.Current!.Phase);
        Assert.Equal(b, rig.Coordinator.Current.Student);
        Assert.False(rig.Coordinator.IsInputRevokePending);
    }

    [Fact]
    public async Task StopAsync_Idempotent_DoesNotRaiseDuplicateEvents()
    {
        var rig = new Rig();
        var student = rig.Join("client-1");

        await rig.Coordinator.RequestAsync(student);
        await rig.Coordinator.StopAsync();
        await rig.Coordinator.StopAsync();

        Assert.Single(rig.Events, e => e.Phase == ControlPhase.Revoked);
    }

    [Fact]
    public async Task Dispose_StopsReactingToRegistryAndRejectsRequests()
    {
        var rig = new Rig();
        var student = rig.Join("client-1");
        await rig.Coordinator.RequestAsync(student);

        rig.Coordinator.Dispose();
        rig.Registry.Disconnect("client-1");

        Assert.Equal(ControlPhase.Active, rig.Coordinator.Current!.Phase);
        var other = rig.Join("client-2");
        var error = await Assert.ThrowsAsync<CollaborationException>(() => rig.Coordinator.RequestAsync(other));
        Assert.Equal(CollaborationError.SessionClosed, error.Code);
    }

    [Fact]
    public void Constructor_RejectsNonProfessorConnection()
    {
        var registry = new ParticipantRegistry();
        var notProfessor = new ParticipantConnection(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);

        Assert.Throws<CollaborationException>(() =>
            new ServerRemoteControlCoordinator(notProfessor, registry, new RecordingInputGate(), new InMemoryLogSink()));
    }

    private sealed class Rig
    {
        private readonly Guid _sessionId = Guid.NewGuid();

        public Rig(IRemoteInputGate? gate = null)
        {
            var professor = new ParticipantConnection(_sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
            Coordinator = new ServerRemoteControlCoordinator(professor, Registry, gate ?? Gate, new InMemoryLogSink());
            Coordinator.StateChanged += state => { lock (Events) Events.Add(state); };
        }

        public ParticipantRegistry Registry { get; } = new();
        public RecordingInputGate Gate { get; } = new();
        public ServerRemoteControlCoordinator Coordinator { get; }
        public List<RemoteControlState> Events { get; } = new();

        public ParticipantConnection Join(string clientId) =>
            Registry.Join(clientId, _sessionId, clientId, ParticipantRole.Student);
    }

    /// <summary>3번 실제 입력 엔진 대신 호출 순서와 완료 시점을 제어하는 대역입니다.</summary>
    private sealed class RecordingInputGate : IRemoteInputGate
    {
        private readonly object _lock = new();
        private readonly List<(string Kind, RemoteControlState State)> _calls = new();
        private Blocker? _nextGrant;
        private Blocker? _nextRevoke;
        private bool _failNextRevoke;

        public IReadOnlyList<RemoteControlState> Grants => Select("grant");
        public IReadOnlyList<RemoteControlState> Revokes => Select("revoke");

        public Blocker BlockNextGrant(bool honorCancellation = true) => _nextGrant = new Blocker(honorCancellation);
        public Blocker BlockNextRevoke() => _nextRevoke = new Blocker(honorCancellation: false);
        public void FailNextRevoke() => _failNextRevoke = true;

        public string[] CallOrder(ParticipantConnection a, ParticipantConnection b)
        {
            lock (_lock)
                return _calls.Select(c => $"{c.Kind}:{(c.State.Student == a ? "a" : c.State.Student == b ? "b" : "?")}").ToArray();
        }

        public Task GrantAsync(RemoteControlState requested, CancellationToken cancellationToken)
        {
            lock (_lock) _calls.Add(("grant", requested));
            var blocker = Interlocked.Exchange(ref _nextGrant, null);
            return blocker?.WaitAsync(cancellationToken) ?? Task.CompletedTask;
        }

        public Task RevokeAsync(RemoteControlState revoked, CancellationToken cancellationToken)
        {
            lock (_lock) _calls.Add(("revoke", revoked));
            if (_failNextRevoke)
            {
                _failNextRevoke = false;
                return Task.FromException(new InvalidOperationException("native 입력 차단 확인 실패"));
            }
            var blocker = Interlocked.Exchange(ref _nextRevoke, null);
            return blocker?.WaitAsync(cancellationToken) ?? Task.CompletedTask;
        }

        private IReadOnlyList<RemoteControlState> Select(string kind)
        {
            lock (_lock) return _calls.Where(c => c.Kind == kind).Select(c => c.State).ToArray();
        }
    }

    private sealed class Blocker(bool honorCancellation)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public bool WasCancelled { get; private set; }

        public void Release() => _release.TrySetResult();

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            if (!honorCancellation)
            {
                await _release.Task;
                return;
            }
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }
    }
}
