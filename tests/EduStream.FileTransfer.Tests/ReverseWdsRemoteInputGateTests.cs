using System;
using System.Threading;
using System.Threading.Tasks;
using EduStream.Core.Collaboration;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 IRemoteInputGate 호출이 3번 역방향 WDS Grant/Revoke 로 이어지는지 확인한다.
/// 이벤트 구독으로 대체하지 않는다.
/// </summary>
public class ReverseWdsRemoteInputGateTests
{
    [Fact]
    public async Task GrantAsync_CallsWdsGrant_ThenOptionalOsPipeline()
    {
        string? granted = null;
        string? injected = null;
        var pipeline = new RecordingPipeline(onInject: id => injected = id);

        var gate = new ReverseWdsRemoteInputGate(
            (id, _) => { granted = id; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask,
            _ => "prof-wds-1",
            pipeline);

        await pipeline.ConnectAsync();
        await gate.GrantAsync(SampleState(), CancellationToken.None);

        Assert.Equal("prof-wds-1", granted);
        Assert.Equal("prof-wds-1", injected);
    }

    [Fact]
    public async Task GrantAsync_WhenWdsGrantFails_DoesNotOpenOsPipeline()
    {
        var injected = false;
        var pipeline = new RecordingPipeline(onInject: _ => injected = true);
        var gate = new ReverseWdsRemoteInputGate(
            (_, _) => throw new InvalidOperationException("WDS 거부"),
            (_, _) => Task.CompletedTask,
            _ => "prof-wds-1",
            pipeline);

        await pipeline.ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.GrantAsync(SampleState(), CancellationToken.None));
        Assert.False(injected);
    }

    [Fact]
    public async Task RevokeAsync_CallsWdsRevoke_AndBlocksOsPipeline()
    {
        string? revoked = null;
        string? blocked = null;
        var pipeline = new RecordingPipeline(onBlock: id => blocked = id);

        var gate = new ReverseWdsRemoteInputGate(
            (_, _) => Task.CompletedTask,
            (id, _) => { revoked = id; return Task.CompletedTask; },
            _ => "prof-wds-1",
            pipeline);

        await pipeline.ConnectAsync();
        await gate.RevokeAsync(SampleState().Revoke(), CancellationToken.None);

        Assert.Equal("prof-wds-1", revoked);
        Assert.Equal("prof-wds-1", blocked);
    }

    [Fact]
    public async Task GrantAsync_WithoutOsPipeline_StillGrantsWds()
    {
        string? granted = null;
        var gate = new ReverseWdsRemoteInputGate(
            (id, _) => { granted = id; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask,
            _ => "prof-wds-1");

        await gate.GrantAsync(SampleState(), CancellationToken.None);
        Assert.Equal("prof-wds-1", granted);
    }

    private static RemoteControlState SampleState()
    {
        var session = Guid.NewGuid();
        var professor = new ParticipantConnection(session, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var student = new ParticipantConnection(session, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
        var snapshot = new ParticipantSnapshot(student, "stu", true, true, true, 1);
        return RemoteControlState.Request(professor, snapshot, Guid.NewGuid());
    }

    private sealed class RecordingPipeline : INativeInputPipeline
    {
        private readonly Action<string>? _onInject;
        private readonly Action<string>? _onBlock;
        public RecordingPipeline(Action<string>? onInject = null, Action<string>? onBlock = null)
        {
            _onInject = onInject;
            _onBlock = onBlock;
        }

        public bool IsConnected { get; private set; }
        public Task ConnectAsync(CancellationToken cancellationToken = default) { IsConnected = true; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { IsConnected = false; return Task.CompletedTask; }
        public Task InjectInputAsync(string targetId, CancellationToken cancellationToken = default) { _onInject?.Invoke(targetId); return Task.CompletedTask; }
        public Task BlockInputAsync(string targetId, CancellationToken cancellationToken = default) { _onBlock?.Invoke(targetId); return Task.CompletedTask; }
        public Task InjectMouseMoveAsync(string targetId, int x, int y, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InjectMouseClickAsync(string targetId, MouseButton button, bool isPressed, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InjectMouseWheelAsync(string targetId, int delta, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InjectKeyboardInputAsync(string targetId, int keyCode, bool isPressed, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
