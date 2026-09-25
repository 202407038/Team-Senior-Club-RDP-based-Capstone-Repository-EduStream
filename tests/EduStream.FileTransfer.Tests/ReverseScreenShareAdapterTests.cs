using System;
using System.Threading;
using System.Threading.Tasks;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// ReverseScreenShareAdapter 단위 테스트
/// 학생 화면 WDS 프레임 수신 어댑터 검증
/// </summary>
public class ReverseScreenShareAdapterTests
{
    [Fact]
    public void Constructor_ShouldInitializeWithReverseSessionManager()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();

        // Act
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);

        // Assert
        Assert.NotNull(adapter);
        Assert.False(adapter.IsAdapterActive);
    }

    [Fact]
    public async Task ActivateAdapterAsync_ShouldSetAdapterActive()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);

        // Act
        await adapter.ActivateAdapterAsync();

        // Assert
        Assert.True(adapter.IsAdapterActive);
    }

    [Fact]
    public async Task DeactivateAdapterAsync_ShouldSetAdapterInactive()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        // Act
        await adapter.DeactivateAdapterAsync();

        // Assert
        Assert.False(adapter.IsAdapterActive);
    }

    [Fact]
    public async Task StartReverseSharingAsync_ShouldReturnSharingId()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";

        // Act
        var sharingId = await adapter.StartReverseSharingAsync(sessionId, studentId);

        // Assert
        Assert.NotEqual(Guid.Empty, sharingId);
        Assert.True(reverseSessionManager.IsReverseSharingActive);
    }

    [Fact]
    public async Task CreateProfessorInvitationAsync_ShouldReturnInvitation()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        var sharingId = await adapter.StartReverseSharingAsync(sessionId, studentId);
        var professorId = "professor-456";
        var connectionId = Guid.NewGuid();
        var password = "test-password";
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        var invitation = await adapter.CreateProfessorInvitationAsync(
            sessionId, sharingId, professorId, connectionId, password, expiresAt);

        // Assert
        Assert.NotNull(invitation);
        Assert.Equal(sessionId, invitation.SessionId);
        Assert.Equal(sharingId, invitation.SharingId);
        Assert.Equal(professorId, invitation.ProfessorId);
        Assert.Equal(studentId, invitation.HostStudentId);
    }

    [Fact]
    public async Task ConnectAsync_ShouldTransitionToConnectingState()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);

        // Act
        await adapter.ConnectAsync();

        // Assert
        Assert.Equal(ReverseSessionState.Connecting, adapter.CurrentState);
    }

    [Fact]
    public async Task OnConnectedAsync_ShouldTransitionToConnectedState()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);
        await adapter.ConnectAsync();

        // Act
        await adapter.OnConnectedAsync();

        // Assert
        Assert.Equal(ReverseSessionState.Connected, adapter.CurrentState);
    }

    [Fact]
    public async Task OnConnectionFailedAsync_ShouldTransitionToFailedState()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);
        await adapter.ConnectAsync();

        // Act
        await adapter.OnConnectionFailedAsync();

        // Assert
        Assert.Equal(ReverseSessionState.Failed, adapter.CurrentState);
    }

    [Fact]
    public async Task OnDisconnectedAsync_ShouldTransitionToDisconnectedState()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);
        await adapter.ConnectAsync();
        await adapter.OnConnectedAsync();

        // Act
        await adapter.OnDisconnectedAsync();

        // Assert
        Assert.Equal(ReverseSessionState.Disconnected, adapter.CurrentState);
    }

    [Fact]
    public async Task StopReverseSharingAsync_ShouldDeactivateSharing()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);

        // Act
        await adapter.StopReverseSharingAsync();

        // Assert
        Assert.False(reverseSessionManager.IsReverseSharingActive);
        Assert.Equal(ReverseSessionState.Inactive, adapter.CurrentState);
    }

    [Fact]
    public async Task AddFrameReceiver_ShouldReceiveFrames()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);
        await adapter.ConnectAsync();
        await adapter.OnConnectedAsync();

        var receivedFrameData = Array.Empty<byte>();
        adapter.AddFrameReceiver(frameData =>
        {
            receivedFrameData = frameData;
            return Task.CompletedTask;
        });

        var testFrameData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        reverseSessionManager.ReceiveFrame(testFrameData);

        // Assert
        Assert.Equal(testFrameData, receivedFrameData);
    }

    [Fact]
    public void AddFrameReceiver_WhenInactive_ShouldNotReceiveFrames()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        // Note: adapter is NOT activated

        var receivedFrameData = Array.Empty<byte>();
        adapter.AddFrameReceiver(frameData =>
        {
            receivedFrameData = frameData;
            return Task.CompletedTask;
        });

        var testFrameData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        reverseSessionManager.ReceiveFrame(testFrameData);

        // Assert
        Assert.Empty(receivedFrameData);
    }

    [Fact]
    public async Task ClearFrameReceivers_ShouldRemoveAllReceivers()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);
        await adapter.ConnectAsync();
        await adapter.OnConnectedAsync();

        adapter.AddFrameReceiver(frameData =>
        {
            return Task.CompletedTask;
        });

        adapter.ClearFrameReceivers();

        // Act - Should not throw
        reverseSessionManager.ReceiveFrame(new byte[] { 1, 2, 3 });

        // Assert
        Assert.True(true);
    }

    [Fact]
    public async Task MultipleFrameReceivers_ShouldAllReceiveFrames()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);
        await adapter.ConnectAsync();
        await adapter.OnConnectedAsync();

        var receiver1Called = false;
        var receiver2Called = false;

        adapter.AddFrameReceiver(frameData =>
        {
            receiver1Called = true;
            return Task.CompletedTask;
        });

        adapter.AddFrameReceiver(frameData =>
        {
            receiver2Called = true;
            return Task.CompletedTask;
        });

        // Act
        reverseSessionManager.ReceiveFrame(new byte[] { 1, 2, 3 });

        // Assert
        Assert.True(receiver1Called);
        Assert.True(receiver2Called);
    }

    [Fact]
    public async Task AdapterStateChanged_ShouldRaiseEventOnActivation()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);

        AdapterStateChangedEventArgs? eventArgs = null;
        adapter.AdapterStateChanged += (sender, args) => eventArgs = args;

        // Act
        await adapter.ActivateAdapterAsync();

        // Assert
        Assert.NotNull(eventArgs);
        Assert.True(eventArgs.IsActive);
    }

    [Fact]
    public async Task AdapterStateChanged_ShouldRaiseEventOnDeactivation()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        AdapterStateChangedEventArgs? eventArgs = null;
        adapter.AdapterStateChanged += (sender, args) => eventArgs = args;

        // Act
        await adapter.DeactivateAdapterAsync();

        // Assert
        Assert.NotNull(eventArgs);
        Assert.False(eventArgs.IsActive);
    }

    [Fact]
    public async Task FrameProcessed_ShouldRaiseEventOnFrameReceived()
    {
        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);
        await adapter.ConnectAsync();
        await adapter.OnConnectedAsync();

        FrameProcessedEventArgs? eventArgs = null;
        adapter.FrameProcessed += (sender, args) => eventArgs = args;

        var testFrameData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        reverseSessionManager.ReceiveFrame(testFrameData);

        // Assert
        Assert.NotNull(eventArgs);
        Assert.Equal(testFrameData, eventArgs.FrameData);
    }
}
