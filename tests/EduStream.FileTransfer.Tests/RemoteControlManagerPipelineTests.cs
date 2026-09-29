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
        public Task BlockInputAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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
}
