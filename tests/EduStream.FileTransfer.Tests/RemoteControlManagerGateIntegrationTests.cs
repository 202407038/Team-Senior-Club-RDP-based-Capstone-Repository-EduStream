using System;
using System.Threading;
using System.Threading.Tasks;
using EduStream.Core.Collaboration;
using EduStream.Server.Rdp;
using EduStream.Server.Services;
using Xunit;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// RemoteControlManager의 IRemoteInputGate 연동 테스트
/// ServerRemoteControlCoordinator와의 실제 입력 허용/차단 연동 검증
/// </summary>
public class RemoteControlManagerGateIntegrationTests
{
    [Fact]
    public async Task GrantAsync_ShouldUpdatePermissionsWhenCalled()
    {
        // Arrange
        var manager = new RemoteControlManager();
        var sessionId = Guid.NewGuid();
        var participantConnection = new ParticipantConnection(
            sessionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ParticipantRole.Student
        );
        var connectionIdStr = participantConnection.ConnectionId.ToString();

        var professorConnection = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var participantSnapshot = new ParticipantSnapshot(participantConnection, "Test Student", true, true, true, 0);
        var requestedState = RemoteControlState.Request(professorConnection, participantSnapshot, Guid.NewGuid());

        // Act
        await manager.GrantAsync(requestedState, CancellationToken.None);

        // Assert
        var permission = await manager.GetParticipantPermissionAsync(connectionIdStr);
        Assert.True(permission.HasControl);
    }

    [Fact]
    public async Task RevokeAsync_ShouldRemovePermissionsWhenCalled()
    {
        // Arrange
        var manager = new RemoteControlManager();
        var sessionId = Guid.NewGuid();
        var participantConnection = new ParticipantConnection(
            sessionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ParticipantRole.Student
        );
        var connectionIdStr = participantConnection.ConnectionId.ToString();

        var professorConnection = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var participantSnapshot = new ParticipantSnapshot(participantConnection, "Test Student", true, true, true, 0);
        var requestedState = RemoteControlState.Request(professorConnection, participantSnapshot, Guid.NewGuid());
        await manager.GrantAsync(requestedState, CancellationToken.None);

        var revokedState = requestedState.Revoke();

        // Act
        await manager.RevokeAsync(revokedState, CancellationToken.None);

        // Assert
        var permission = await manager.GetParticipantPermissionAsync(connectionIdStr);
        Assert.False(permission.HasControl);
    }

    [Fact]
    public async Task ProcessInput_AfterGrant_ShouldProcessAllowedInput()
    {
        // Arrange
        var manager = new RemoteControlManager();
        var participantId = "test-participant";
        await manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await manager.GrantControlAsync(participantId);

        // Act & Assert
        Assert.True(manager.ProcessMouseMove(participantId, 100, 200));
        Assert.True(manager.ProcessMouseClick(participantId, MouseButton.Left, true));
        Assert.True(manager.ProcessKeyboardInput(participantId, 65, true));
    }

    [Fact]
    public async Task ProcessInput_AfterRevoke_ShouldBlockInput()
    {
        // Arrange
        var manager = new RemoteControlManager();
        var sessionId = Guid.NewGuid();
        var participantConnection = new ParticipantConnection(
            sessionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ParticipantRole.Student
        );
        var connectionIdStr = participantConnection.ConnectionId.ToString();

        await manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);
        await manager.GrantControlAsync(connectionIdStr);

        var professorConnection = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var participantSnapshot = new ParticipantSnapshot(participantConnection, "Test Student", true, true, true, 0);
        var requestedState = RemoteControlState.Request(professorConnection, participantSnapshot, Guid.NewGuid());
        await manager.GrantAsync(requestedState, CancellationToken.None);
        var revokedState = requestedState.Revoke();
        await manager.RevokeAsync(revokedState, CancellationToken.None);

        // Act & Assert
        Assert.False(manager.ProcessMouseMove(connectionIdStr, 100, 200));
        Assert.False(manager.ProcessMouseClick(connectionIdStr, MouseButton.Left, true));
        Assert.False(manager.ProcessKeyboardInput(connectionIdStr, 65, true));
    }

    [Fact]
    public async Task GrantAsync_WithKeyboardOnlyLevel_ShouldBlockMouseInput()
    {
        // Arrange
        var manager = new RemoteControlManager();
        var sessionId = Guid.NewGuid();
        var participantConnection = new ParticipantConnection(
            sessionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ParticipantRole.Student
        );
        var connectionIdStr = participantConnection.ConnectionId.ToString();
        await manager.SetControlLevelAsync(ControlLevel.KeyboardOnly);

        var professorConnection = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var participantSnapshot = new ParticipantSnapshot(participantConnection, "Test Student", true, true, true, 0);
        var requestedState = RemoteControlState.Request(professorConnection, participantSnapshot, Guid.NewGuid());
        await manager.GrantAsync(requestedState, CancellationToken.None);

        // Act & Assert
        Assert.False(manager.ProcessMouseMove(connectionIdStr, 100, 200));
        Assert.False(manager.ProcessMouseClick(connectionIdStr, MouseButton.Left, true));
        Assert.True(manager.ProcessKeyboardInput(connectionIdStr, 65, true));
    }

    [Fact]
    public async Task GrantAsync_WithViewOnlyLevel_ShouldBlockAllInput()
    {
        // Arrange
        var manager = new RemoteControlManager();
        var sessionId = Guid.NewGuid();
        var participantConnection = new ParticipantConnection(
            sessionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ParticipantRole.Student
        );
        var connectionIdStr = participantConnection.ConnectionId.ToString();
        await manager.SetControlLevelAsync(ControlLevel.ViewOnly);

        var professorConnection = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var participantSnapshot = new ParticipantSnapshot(participantConnection, "Test Student", true, true, true, 0);
        var requestedState = RemoteControlState.Request(professorConnection, participantSnapshot, Guid.NewGuid());
        await manager.GrantAsync(requestedState, CancellationToken.None);

        // Act & Assert
        Assert.False(manager.ProcessMouseMove(connectionIdStr, 100, 200));
        Assert.False(manager.ProcessMouseClick(connectionIdStr, MouseButton.Left, true));
        Assert.False(manager.ProcessKeyboardInput(connectionIdStr, 65, true));
    }

    [Fact]
    public async Task RevokeAsync_Idempotent_ShouldSucceedForNonExistentParticipant()
    {
        // Arrange
        var manager = new RemoteControlManager();
        var sessionId = Guid.NewGuid();
        var participantConnection = new ParticipantConnection(
            sessionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ParticipantRole.Student
        );
        var professorConnection = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var participantSnapshot = new ParticipantSnapshot(participantConnection, "Non Existent", true, true, true, 0);
        var requestedState = RemoteControlState.Request(professorConnection, participantSnapshot, Guid.NewGuid());
        var revokedState = requestedState.Revoke();

        // Act & Assert - Should not throw
        await manager.RevokeAsync(revokedState, CancellationToken.None);
    }

    [Fact]
    public async Task MultipleGrantRevoke_ShouldMaintainCorrectState()
    {
        // Arrange
        var manager = new RemoteControlManager();
        var sessionId = Guid.NewGuid();
        var connection1 = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
        var connection2 = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
        var connectionIdStr1 = connection1.ConnectionId.ToString();
        var connectionIdStr2 = connection2.ConnectionId.ToString();

        await manager.SetControlLevelAsync(ControlLevel.KeyboardAndMouse);

        // Act
        await manager.GrantControlAsync(connectionIdStr1);
        await manager.GrantControlAsync(connectionIdStr2);

        var professorConnection = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var participantSnapshot1 = new ParticipantSnapshot(connection1, "Student 1", true, true, true, 0);
        var requestedState1 = RemoteControlState.Request(professorConnection, participantSnapshot1, Guid.NewGuid());
        var revokedState1 = requestedState1.Revoke();
        await manager.RevokeAsync(revokedState1, CancellationToken.None);

        // Assert
        var permission1 = await manager.GetParticipantPermissionAsync(connectionIdStr1);
        var permission2 = await manager.GetParticipantPermissionAsync(connectionIdStr2);

        Assert.False(permission1.HasControl);
        Assert.True(permission2.HasControl);
    }
}
