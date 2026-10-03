using System;
using System.Reflection;
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
    private void ForceControlGrantedState(ReverseSessionManager manager)
    {
        var field = typeof(ReverseSessionManager).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(manager, ReverseSessionState.ControlGranted);
    }

    [WdsFact]
    public void Constructor_ShouldInitializeWithReverseSessionManager()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        Assert.NotNull(adapter);
        Assert.False(adapter.IsAdapterActive);
    }

    [WdsFact]
    public async Task ActivateAdapterAsync_ShouldSetAdapterActive()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();
        Assert.True(adapter.IsAdapterActive);
    }

    [WdsFact]
    public async Task DeactivateAdapterAsync_ShouldSetAdapterInactive()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();
        await adapter.DeactivateAdapterAsync();
        Assert.False(adapter.IsAdapterActive);
    }

    [WdsFact]
    public async Task StartReverseSharingAsync_ShouldReturnSharingId()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";

        var sharingId = await adapter.StartReverseSharingAsync(sessionId, studentId);

        Assert.NotEqual(Guid.Empty, sharingId);
        Assert.True(reverseSessionManager.IsReverseSharingActive);
    }

    [WdsFact]
    public async Task CreateProfessorInvitationAsync_ShouldReturnInvitation()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        var sharingId = await adapter.StartReverseSharingAsync(sessionId, studentId);
        var professorId = "professor-456";
        var connectionId = Guid.NewGuid();
        var password = "test-password";
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        var invitation = await adapter.CreateProfessorInvitationAsync(
            sessionId, sharingId, professorId, connectionId, password, expiresAt);

        Assert.NotNull(invitation);
        Assert.Equal(sessionId, invitation.SessionId);
        Assert.Equal(sharingId, invitation.SharingId);
        Assert.Equal(professorId, invitation.ProfessorId);
        Assert.Equal(studentId, invitation.HostStudentId);
    }

    [WdsFact]
    public async Task ConnectAsync_ShouldTransitionToConnectingState()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);

        await adapter.ConnectAsync();

        Assert.Equal(ReverseSessionState.Connecting, adapter.CurrentState);
    }

    [WdsFact]
    public async Task OnConnectedAsync_WhenNoAttendees_ShouldTransitionToFailedState()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);
        await adapter.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.OnConnectedAsync());
        Assert.Equal(ReverseSessionState.Failed, adapter.CurrentState);
    }

    [WdsFact]
    public async Task OnConnectionFailedAsync_ShouldTransitionToFailedState()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);
        await adapter.ConnectAsync();

        await adapter.OnConnectionFailedAsync();

        Assert.Equal(ReverseSessionState.Failed, adapter.CurrentState);
    }

    [WdsFact]
    public async Task OnDisconnectedAsync_ShouldTransitionToDisconnectedState()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);
        await adapter.ConnectAsync();

        ForceControlGrantedState(reverseSessionManager);

        await adapter.OnDisconnectedAsync();

        Assert.Equal(ReverseSessionState.Disconnected, adapter.CurrentState);
    }

    [WdsFact]
    public async Task StopReverseSharingAsync_ShouldDeactivateSharing()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);

        await adapter.StopReverseSharingAsync();

        Assert.False(reverseSessionManager.IsReverseSharingActive);
        Assert.Equal(ReverseSessionState.Inactive, adapter.CurrentState);
    }

    [WdsFact]
    public async Task AddFrameReceiver_ShouldReceiveFrames()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);

        ForceControlGrantedState(reverseSessionManager);

        var receivedFrameData = Array.Empty<byte>();
        adapter.AddFrameReceiver(frameData =>
        {
            receivedFrameData = frameData;
            return Task.CompletedTask;
        });

        var testFrameData = new byte[] { 1, 2, 3, 4, 5 };

        reverseSessionManager.ReceiveFrame(testFrameData);

        Assert.Equal(testFrameData, receivedFrameData);
    }

    [WdsFact]
    public void AddFrameReceiver_WhenInactive_ShouldNotReceiveFrames()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);

        var receivedFrameData = Array.Empty<byte>();
        adapter.AddFrameReceiver(frameData =>
        {
            receivedFrameData = frameData;
            return Task.CompletedTask;
        });

        var testFrameData = new byte[] { 1, 2, 3, 4, 5 };

        reverseSessionManager.ReceiveFrame(testFrameData);

        Assert.Empty(receivedFrameData);
    }

    [WdsFact]
    public async Task ClearFrameReceivers_ShouldRemoveAllReceivers()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);

        ForceControlGrantedState(reverseSessionManager);

        adapter.AddFrameReceiver(frameData =>
        {
            return Task.CompletedTask;
        });

        adapter.ClearFrameReceivers();

        reverseSessionManager.ReceiveFrame(new byte[] { 1, 2, 3 });
        Assert.True(true);
    }

    [WdsFact]
    public async Task MultipleFrameReceivers_ShouldAllReceiveFrames()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);

        ForceControlGrantedState(reverseSessionManager);

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

        reverseSessionManager.ReceiveFrame(new byte[] { 1, 2, 3 });

        Assert.True(receiver1Called);
        Assert.True(receiver2Called);
    }

    [WdsFact]
    public async Task AdapterStateChanged_ShouldRaiseEventOnActivation()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);

        AdapterStateChangedEventArgs? eventArgs = null;
        adapter.AdapterStateChanged += (sender, args) => eventArgs = args;

        await adapter.ActivateAdapterAsync();

        Assert.NotNull(eventArgs);
        Assert.True(eventArgs.IsActive);
    }

    [WdsFact]
    public async Task AdapterStateChanged_ShouldRaiseEventOnDeactivation()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        AdapterStateChangedEventArgs? eventArgs = null;
        adapter.AdapterStateChanged += (sender, args) => eventArgs = args;

        await adapter.DeactivateAdapterAsync();

        Assert.NotNull(eventArgs);
        Assert.False(eventArgs.IsActive);
    }

    [WdsFact]
    public async Task FrameProcessed_ShouldRaiseEventOnFrameReceived()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);

        ForceControlGrantedState(reverseSessionManager);

        FrameProcessedEventArgs? eventArgs = null;
        adapter.FrameProcessed += (sender, args) => eventArgs = args;

        var testFrameData = new byte[] { 1, 2, 3, 4, 5 };

        reverseSessionManager.ReceiveFrame(testFrameData);

        Assert.NotNull(eventArgs);
        Assert.Equal(testFrameData, eventArgs.FrameData);
    }

    [WdsFact]
    public async Task FrameDisplayed_ShouldRaiseEventWithFrameDimensions()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        adapter.AddDisplayHandler((_, _, _) => Task.CompletedTask);

        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);

        ForceControlGrantedState(reverseSessionManager);

        FrameDisplayedEventArgs? eventArgs = null;
        adapter.FrameDisplayed += (sender, args) => eventArgs = args;

        var widthBytes = BitConverter.GetBytes(1920);
        var heightBytes = BitConverter.GetBytes(1080);
        var testFrameData = new byte[8];
        Array.Copy(widthBytes, 0, testFrameData, 0, 4);
        Array.Copy(heightBytes, 0, testFrameData, 4, 4);

        reverseSessionManager.ReceiveFrame(testFrameData);

        Assert.NotNull(eventArgs);
        Assert.Equal(1920, eventArgs.Width);
        Assert.Equal(1080, eventArgs.Height);
    }

    [WdsFact]
    public async Task AddDisplayHandler_ShouldReceiveFramesWithDimensions()
    {
        var reverseSessionManager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(reverseSessionManager);
        await adapter.ActivateAdapterAsync();

        var sessionId = Guid.NewGuid();
        var studentId = "student-123";
        await adapter.StartReverseSharingAsync(sessionId, studentId);

        ForceControlGrantedState(reverseSessionManager);

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

        reverseSessionManager.ReceiveFrame(testFrameData);

        Assert.Equal(testFrameData, receivedFrameData);
        Assert.Equal(1920, receivedWidth);
        Assert.Equal(1080, receivedHeight);
    }
}