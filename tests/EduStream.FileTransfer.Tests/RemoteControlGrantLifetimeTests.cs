using System.Collections.Concurrent;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>9월 5주차 보완: 늦은 입력 허용/부분 실패·호출 취소·엔진 교체의 회수 수명을 검증합니다.</summary>
public sealed class RemoteControlGrantLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task LatePartialFailureAfterStop_RevokesOriginalInputAgain()
    {
        using var rig = new Rig();
        var student = rig.Join("a");
        rig.Gate.FailAfterGrant = true;
        var request = rig.Coordinator.RequestAsync(student);
        await rig.Gate.Entered.Task.WaitAsync(Timeout);
        await rig.Coordinator.StopAsync().WaitAsync(Timeout);
        Assert.True(rig.Coordinator.IsInputRevokePending);
        rig.Gate.Release.TrySetResult();
        await request.WaitAsync(Timeout);

        Assert.Equal(ControlPhase.Revoked, rig.Coordinator.Current!.Phase);
        Assert.Empty(rig.Gate.Enabled);
        Assert.True(rig.Gate.Revokes.Count >= 2);
        Assert.False(rig.Coordinator.IsInputRevokePending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineReplacement_WaitsForLateGrantAndCleanup(bool fail)
    {
        using var rig = new Rig();
        rig.Gate.FailAfterGrant = fail;
        var request = rig.Coordinator.RequestAsync(rig.Join("a"));
        await rig.Gate.Entered.Task.WaitAsync(Timeout);
        await rig.Coordinator.StopAsync().WaitAsync(Timeout);
        Assert.Throws<InvalidOperationException>(() => rig.Coordinator.SetInputGate(UnavailableRemoteInputGate.Instance));

        rig.Gate.Release.TrySetResult();
        await request.WaitAsync(Timeout);
        Assert.Empty(rig.Gate.Enabled);
        rig.Coordinator.SetInputGate(UnavailableRemoteInputGate.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_RevokesEvenWhenNativeIgnoresCancellation(bool honorCancellation)
    {
        using var rig = new Rig();
        using var cancellation = new CancellationTokenSource();
        rig.Gate.HonorCancellation = honorCancellation;
        var request = rig.Coordinator.RequestAsync(rig.Join("a"), cancellation.Token);
        await rig.Gate.Entered.Task.WaitAsync(Timeout);
        cancellation.Cancel();
        Assert.Equal(ControlPhase.Revoked, rig.Coordinator.Current!.Phase);
        rig.Gate.Release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Timeout));
        Assert.Empty(rig.Gate.Enabled);
        Assert.DoesNotContain(rig.Events, state => state.Phase == ControlPhase.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingTarget_WaitsForLateGrantAndCleanup(bool fail)
    {
        using var rig = new Rig();
        rig.Gate.FailAfterGrant = fail;
        var a = rig.Join("a");
        var b = rig.Join("b");
        var first = rig.Coordinator.RequestAsync(a);
        await rig.Gate.Entered.Task.WaitAsync(Timeout);
        var switching = rig.Coordinator.RequestAsync(b);
        Assert.False(switching.IsCompleted);
        Assert.DoesNotContain(rig.Gate.Grants, state => state.Student == b);

        rig.Gate.Release.TrySetResult();
        await Task.WhenAll(first, switching).WaitAsync(Timeout);
        Assert.Equal(b, rig.Coordinator.Current!.Student);
        Assert.Equal(ControlPhase.Active, rig.Coordinator.Current.Phase);
        Assert.Equal(new[] { b.ConnectionId }, rig.Gate.Enabled.Keys.ToArray());
        Assert.False(rig.Gate.SawOverlappingInput);
    }

    [Fact]
    public async Task StopWhileSwitchWaitsForLateGrant_DoesNotStartNextTarget()
    {
        using var rig = new Rig();
        var first = rig.Coordinator.RequestAsync(rig.Join("a"));
        await rig.Gate.Entered.Task.WaitAsync(Timeout);
        var b = rig.Join("b");
        var switching = rig.Coordinator.RequestAsync(b);
        await rig.Coordinator.StopAsync().WaitAsync(Timeout);
        rig.Gate.Release.TrySetResult();
        await Task.WhenAll(first, switching).WaitAsync(Timeout);

        Assert.Equal(ControlPhase.Revoked, rig.Coordinator.Current!.Phase);
        Assert.DoesNotContain(rig.Gate.Grants, state => state.Student == b);
        Assert.Empty(rig.Gate.Enabled);
    }

    [Fact]
    public async Task LateCleanupFailure_BlocksReplacementUntilRetry()
    {
        using var rig = new Rig();
        var request = rig.Coordinator.RequestAsync(rig.Join("a"));
        await rig.Gate.Entered.Task.WaitAsync(Timeout);
        await rig.Coordinator.StopAsync().WaitAsync(Timeout);
        rig.Gate.FailRevoke = true;
        rig.Gate.Release.TrySetResult();
        await request.WaitAsync(Timeout);
        Assert.True(rig.Coordinator.IsInputRevokePending);
        Assert.Throws<InvalidOperationException>(() => rig.Coordinator.SetInputGate(UnavailableRemoteInputGate.Instance));

        rig.Gate.FailRevoke = false;
        await rig.Coordinator.ConfirmInputRevokedAsync().WaitAsync(Timeout);
        Assert.Empty(rig.Gate.Enabled);
        rig.Coordinator.SetInputGate(UnavailableRemoteInputGate.Instance);
    }

    private sealed class Rig : IDisposable
    {
        private readonly Guid _session = Guid.NewGuid();
        private readonly ParticipantRegistry _registry = new();
        public ControlledGate Gate { get; } = new();
        public ServerRemoteControlCoordinator Coordinator { get; }
        public ConcurrentQueue<RemoteControlState> Events { get; } = new();

        public Rig()
        {
            var professor = new ParticipantConnection(_session, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
            Coordinator = new ServerRemoteControlCoordinator(professor, _registry, Gate, new InMemoryLogSink());
            Coordinator.StateChanged += Events.Enqueue;
        }

        public ParticipantConnection Join(string name) => _registry.Join(name, _session, name, ParticipantRole.Student);
        public void Dispose() => Coordinator.Dispose();
    }

    private sealed class ControlledGate : IRemoteInputGate
    {
        private int _grantCount;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<Guid, bool> Enabled { get; } = new();
        public ConcurrentQueue<RemoteControlState> Grants { get; } = new();
        public ConcurrentQueue<RemoteControlState> Revokes { get; } = new();
        public bool FailAfterGrant { get; set; }
        public bool HonorCancellation { get; set; }
        public bool FailRevoke { get; set; }
        public bool SawOverlappingInput { get; private set; }

        public async Task GrantAsync(RemoteControlState requested, CancellationToken token)
        {
            Grants.Enqueue(requested);
            var first = Interlocked.Increment(ref _grantCount) == 1;
            if (first)
            {
                Entered.TrySetResult();
                await Release.Task;
            }
            if (HonorCancellation) token.ThrowIfCancellationRequested();
            Enabled[requested.Student.ConnectionId] = true;
            if (Enabled.Count > 1) SawOverlappingInput = true;
            if (first && FailAfterGrant) throw new InvalidOperationException("대역: 입력 일부 허용 후 실패");
        }

        public Task RevokeAsync(RemoteControlState revoked, CancellationToken token)
        {
            Revokes.Enqueue(revoked);
            if (FailRevoke) throw new InvalidOperationException("대역: 입력 회수 확인 실패");
            Enabled.TryRemove(revoked.Student.ConnectionId, out _);
            return Task.CompletedTask;
        }
    }
}
