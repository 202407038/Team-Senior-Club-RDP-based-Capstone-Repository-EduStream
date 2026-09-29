using System;
using System.Threading;
using System.Threading.Tasks;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

public class RemoteControlManagerPipelineTests
{
    private sealed class FailingNativeInputPipeline : INativeInputPipeline
    {
        public bool IsConnected => true;
        public Task InjectInputAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromException(new InputPipelineException("네이티브 입력 주입 실패 대역"));
        public Task InjectMouseMoveAsync(string targetId, int x, int y, CancellationToken cancellationToken = default)
            => Task.FromException(new InputPipelineException("네이티브 입력 주입 실패 대역"));
        public Task InjectMouseClickAsync(string targetId, MouseButton button, bool isPressed, CancellationToken cancellationToken = default)
            => Task.FromException(new InputPipelineException("네이티브 입력 주입 실패 대역"));
        public Task InjectMouseWheelAsync(string targetId, int delta, CancellationToken cancellationToken = default)
            => Task.FromException(new InputPipelineException("네이티브 입력 주입 실패 대역"));
        public Task InjectKeyboardInputAsync(string targetId, int keyCode, bool isPressed, CancellationToken cancellationToken = default)
            => Task.FromException(new InputPipelineException("네이티브 입력 주입 실패 대역"));
        public Task BlockInputAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SeverableNativeInputPipeline : INativeInputPipeline
    {
        public bool IsConnected { get; set; } = true;
        public int InjectedCallCount { get; private set; }

        public Task InjectInputAsync(string targetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InjectMouseMoveAsync(string targetId, int x, int y, CancellationToken cancellationToken = default)
        {
            InjectedCallCount++;
            return Task.CompletedTask;
        }
        public Task InjectMouseClickAsync(string targetId, MouseButton button, bool isPressed, CancellationToken cancellationToken = default)
        {
            InjectedCallCount++;
            return Task.CompletedTask;
        }
        public Task InjectMouseWheelAsync(string targetId, int delta, CancellationToken cancellationToken = default)
        {
            InjectedCallCount++;
            return Task.CompletedTask;
        }
        public Task InjectKeyboardInputAsync(string targetId, int keyCode, bool isPressed, CancellationToken cancellationToken = default)
        {
            InjectedCallCount++;
            return Task.CompletedTask;
        }
        public Task BlockInputAsync(string targetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task ProcessInput_WhenPipelineFails_ShouldReturnFalse()
    {
        // Arrange
        var manager = new RemoteControlManager();
        var failingPipeline = new FailingNativeInputPipeline();
        await manager.ConnectNativeEngineAsync(failingPipeline);
        await manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await manager.GrantControlAsync("student1");

        // Act & Assert
        Assert.False(manager.ProcessMouseMove("student1", 100, 200));
        Assert.False(manager.ProcessMouseClick("student1", MouseButton.Left, true));
        Assert.False(manager.ProcessMouseWheel("student1", 120));
        Assert.False(manager.ProcessKeyboardInput("student1", 65, true));
    }

    [Fact]
    public async Task ProcessInput_WhenEngineNotConnected_EvenWithPermission_ShouldReturnFalse()
    {
        // Arrange: 엔진 미연결 상태에서 권한만 부여된 경우 (피드백 3-1 검증)
        var manager = new RemoteControlManager();
        await manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await manager.GrantControlAsync("student1");

        // Act & Assert: 엔진이 연결되지 않았으므로 입력 4종 모두 false 반환
        Assert.False(manager.ProcessMouseMove("student1", 100, 200));
        Assert.False(manager.ProcessMouseClick("student1", MouseButton.Left, true));
        Assert.False(manager.ProcessMouseWheel("student1", 120));
        Assert.False(manager.ProcessKeyboardInput("student1", 65, true));
    }

    [Fact]
    public async Task ProcessInput_WhenEngineSevered_ShouldReturnFalseAndNotInvoke()
    {
        // Arrange: 연결 후 단절된 상태 (피드백 3-1 검증)
        var manager = new RemoteControlManager();
        var pipeline = new SeverableNativeInputPipeline();
        await manager.ConnectNativeEngineAsync(pipeline);
        await manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await manager.GrantControlAsync("student1");

        // 정상 연결 시 1회 성공 확인
        Assert.True(manager.ProcessMouseMove("student1", 100, 200));
        Assert.Equal(1, pipeline.InjectedCallCount);

        // 엔진 단절 발생
        pipeline.IsConnected = false;

        // Act & Assert: 단절 후 입력 4종 모두 false이며 호출 카운트가 증가하지 않음
        Assert.False(manager.ProcessMouseMove("student1", 100, 200));
        Assert.False(manager.ProcessMouseClick("student1", MouseButton.Left, true));
        Assert.False(manager.ProcessMouseWheel("student1", 120));
        Assert.False(manager.ProcessKeyboardInput("student1", 65, true));
        Assert.Equal(1, pipeline.InjectedCallCount);
    }

    [Fact]
    public async Task ProcessInput_AfterExplicitDisconnect_ShouldReturnFalse()
    {
        // Arrange: 명시적 연결 해제 후 상태 (피드백 3-1 검증)
        var manager = new RemoteControlManager();
        var pipeline = new SeverableNativeInputPipeline();
        await manager.ConnectNativeEngineAsync(pipeline);
        await manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await manager.GrantControlAsync("student1");

        // 명시적 엔진 연결 해제
        await manager.DisconnectNativeEngineAsync();

        // Act & Assert: 남아 있는 권한과 무관하게 입력 4종 모두 false
        Assert.False(manager.ProcessMouseMove("student1", 100, 200));
        Assert.False(manager.ProcessMouseClick("student1", MouseButton.Left, true));
        Assert.False(manager.ProcessMouseWheel("student1", 120));
        Assert.False(manager.ProcessKeyboardInput("student1", 65, true));
    }

    [Fact]
    public async Task WindowsNativeInputPipeline_TargetValidation_ShouldEnforceGating()
    {
        // Arrange: 실구현 WindowsNativeInputPipeline 검증 (피드백 3-3 검증)
        var pipeline = new WindowsNativeInputPipeline();

        // 1. 미연결 시 예외
        await Assert.ThrowsAsync<InputPipelineException>(() => pipeline.InjectMouseMoveAsync("student1", 100, 100));

        // 2. 연결 후 대상 미지정 시 차단
        await pipeline.ConnectAsync();
        await Assert.ThrowsAsync<InputPipelineException>(() => pipeline.InjectMouseMoveAsync("student1", 100, 100));

        // 3. 학생1 선택 시 성공
        await pipeline.InjectInputAsync("student1");
        await pipeline.InjectMouseMoveAsync("student1", 100, 100);

        // 4. 선택되지 않은 학생2 주입 시 차단
        await Assert.ThrowsAsync<InputPipelineException>(() => pipeline.InjectMouseMoveAsync("student2", 200, 200));

        // 5. 제어 철회(Block) 시 차단
        await pipeline.BlockInputAsync("student1");
        await Assert.ThrowsAsync<InputPipelineException>(() => pipeline.InjectMouseMoveAsync("student1", 100, 100));
    }
}
