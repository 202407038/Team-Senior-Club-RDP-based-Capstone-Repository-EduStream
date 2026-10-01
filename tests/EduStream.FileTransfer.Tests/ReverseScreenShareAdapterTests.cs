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
    // 🌟 추가된 탐지기 메서드
    private bool IsWdsEngineAvailable()
    {
        return Type.GetTypeFromProgID("RDPCOMAPILib.RDPSession") != null;
    }

    [Fact]
    public void Constructor_ShouldInitializeWithReverseSessionManager()
    {
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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
        if (!IsWdsEngineAvailable()) return;

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

    [Fact]
    public async Task FrameDisplayed_ShouldRaiseEventWithFrameDimensions()
    {
        if (!IsWdsEngineAvailable()) return;

        // Arrange
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        adapter.AddDisplayHandler((_, _, _) => Task.CompletedTask);

        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);
        await adapter.ConnectAsync();
        await adapter.OnConnectedAsync();

        FrameDisplayedEventArgs? eventArgs = null;
        adapter.FrameDisplayed += (sender, args) => eventArgs = args;

        // Create frame data with embedded dimensions (simulated WDS format)
        var widthBytes = BitConverter.GetBytes(1920);
        var heightBytes = BitConverter.GetBytes(1080);
        var testFrameData = new byte[8];
        Array.Copy(widthBytes, 0, testFrameData, 0, 4);
        Array.Copy(heightBytes, 0, testFrameData, 4, 4);

        // Act
        reverseSessionManager.ReceiveFrame(testFrameData);

        // Assert
        Assert.NotNull(eventArgs);
        Assert.Equal(1920, eventArgs.Width);
        Assert.Equal(1080, eventArgs.Height);
    }

    [Fact]
    public async Task AddDisplayHandler_ShouldReceiveFramesWithDimensions()
    {
        if (!IsWdsEngineAvailable()) return;

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
        var receivedWidth = 0;
        var receivedHeight = 0;

        adapter.AddDisplayHandler((frameData, width, height) =>
        {
            receivedFrameData = frameData;
            receivedWidth = width;
            receivedHeight = height;
            return Task.CompletedTask;
        });

        var widthBytes = BitConverter.GetBytes(1920);
        var heightBytes = BitConverter.GetBytes(1080);
        var testFrameData = new byte[8];
        Array.Copy(widthBytes, 0, testFrameData, 0, 4);
        Array.Copy(heightBytes, 0, testFrameData, 4, 4);

        // Act
        reverseSessionManager.ReceiveFrame(testFrameData);

        // Assert
        Assert.Equal(testFrameData, receivedFrameData);
        Assert.Equal(1920, receivedWidth);
        Assert.Equal(1080, receivedHeight);
    }
}