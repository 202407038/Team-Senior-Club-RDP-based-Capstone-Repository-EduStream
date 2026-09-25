using System;
using System.Threading.Tasks;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

public class RemoteControlManagerTests
{
    private readonly RemoteControlManager _manager = new();

    [Fact]
    public void CurrentControlLevel_기본값_ViewOnly()
    {
        // Assert
        Assert.Equal(ControlLevel.ViewOnly, _manager.CurrentControlLevel);
    }

    [Fact]
    public async Task SetControlLevelAsync_레벨_변경_성공()
    {
        // Act
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);

        // Assert
        Assert.Equal(ControlLevel.KeyboardAndMouse, _manager.CurrentControlLevel);
    }

    [Fact]
    public async Task SetControlLevelAsync_ViewOnly로_변경시_모든_권한_철회()
    {
        // Arrange
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await _manager.GrantControlAsync("student1");

        // Act
        await _manager.SetControlLevelAsync(ControlLevel.ViewOnly);

        // Assert
        var permission = await _manager.GetParticipantPermissionAsync("student1");
        Assert.False(permission.HasControl);
    }

    [Fact]
    public async Task GrantControlAsync_ViewOnly_상태에서_예외_발생()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.GrantControlAsync("student1"));
    }

    [Fact]
    public async Task GrantControlAsync_제어_권한_부여_성공()
    {
        // Arrange
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);

        // Act
        await _manager.GrantControlAsync("student1");

        // Assert
        var permission = await _manager.GetParticipantPermissionAsync("student1");
        Assert.True(permission.HasControl);
        Assert.Equal("student1", permission.ParticipantId);
        Assert.Equal(ControlLevel.KeyboardAndMouse, permission.Level);
    }

    [Fact]
    public async Task RevokeControlAsync_권한_철회_성공()
    {
        // Arrange
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await _manager.GrantControlAsync("student1");

        // Act
        await _manager.RevokeControlAsync("student1");

        // Assert
        var permission = await _manager.GetParticipantPermissionAsync("student1");
        Assert.False(permission.HasControl);
    }

    [Fact]
    public async Task RevokeAllControlAsync_모든_권한_철회()
    {
        // Arrange
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await _manager.GrantControlAsync("student1");
        await _manager.GrantControlAsync("student2");

        // Act
        await _manager.RevokeAllControlAsync();

        // Assert
        var permission1 = await _manager.GetParticipantPermissionAsync("student1");
        var permission2 = await _manager.GetParticipantPermissionAsync("student2");
        Assert.False(permission1.HasControl);
        Assert.False(permission2.HasControl);
    }

    [Fact]
    public async Task GetParticipantPermissionAsync_권한이_없는_참가자_조회()
    {
        // Act
        var permission = await _manager.GetParticipantPermissionAsync("student1");

        // Assert
        Assert.False(permission.HasControl);
        Assert.Equal(ControlLevel.ViewOnly, permission.Level);
    }

    [Fact]
    public void ProcessMouseMove_권한_없으면_차단()
    {
        // Act
        var result = _manager.ProcessMouseMove("student1", 100, 200);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ProcessMouseMove_권한_있으면_처리()
    {
        // Arrange
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await _manager.GrantControlAsync("student1");

        // Act
        var result = _manager.ProcessMouseMove("student1", 100, 200);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ProcessMouseClick_권한_없으면_차단()
    {
        // Act
        var result = _manager.ProcessMouseClick("student1", MouseButton.Left, true);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ProcessMouseClick_권한_있으면_처리()
    {
        // Arrange
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await _manager.GrantControlAsync("student1");

        // Act
        var result = _manager.ProcessMouseClick("student1", MouseButton.Left, true);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ProcessMouseWheel_권한_없으면_차단()
    {
        // Act
        var result = _manager.ProcessMouseWheel("student1", 120);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ProcessMouseWheel_권한_있으면_처리()
    {
        // Arrange
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await _manager.GrantControlAsync("student1");

        // Act
        var result = _manager.ProcessMouseWheel("student1", 120);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ProcessKeyboardInput_권한_없으면_차단()
    {
        // Act
        var result = _manager.ProcessKeyboardInput("student1", 65, true);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ProcessKeyboardInput_권한_있으면_처리()
    {
        // Arrange
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await _manager.GrantControlAsync("student1");

        // Act
        var result = _manager.ProcessKeyboardInput("student1", 65, true);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ProcessMouseMove_KeyboardOnly_레벨에서_마우스_차단()
    {
        // Arrange
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardOnly);
        await _manager.GrantControlAsync("student1");

        // Act
        var result = _manager.ProcessMouseMove("student1", 100, 200);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ProcessKeyboardInput_KeyboardOnly_레벨에서_키보드_허용()
    {
        // Arrange
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardOnly);
        await _manager.GrantControlAsync("student1");

        // Act
        var result = _manager.ProcessKeyboardInput("student1", 65, true);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ProcessInput_권한_철회후_모든_입력_차단()
    {
        // Arrange
        await _manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await _manager.GrantControlAsync("student1");
        await _manager.RevokeControlAsync("student1");

        // Act
        var mouseResult = _manager.ProcessMouseMove("student1", 100, 200);
        var keyResult = _manager.ProcessKeyboardInput("student1", 65, true);

        // Assert
        Assert.False(mouseResult);
        Assert.False(keyResult);
    }
}
