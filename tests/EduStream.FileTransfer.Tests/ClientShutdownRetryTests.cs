using EduStream.Client.ViewModels;
using EduStream.Core.Models;
using EduStream.Core.Network;

namespace EduStream.FileTransfer.Tests;

public sealed class ClientShutdownRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedNativeCleanup_CanBeRetried_AndSuccessIsIdempotent(bool failDispose)
    {
        var viewer = new RetryViewer { FailDispose = failDispose, FailDisconnect = !failDispose };
        var model = new ClientViewModel(viewer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.ShutdownAsync());
        Assert.False(viewer.Disposed);
        viewer.FailDisconnect = viewer.FailDispose = false;
        await model.ShutdownAsync();
        Assert.True(viewer.Disposed);
        Assert.Equal(2, viewer.DisconnectCalls);
        var calls = viewer.DisposeCalls;
        await model.ShutdownAsync();
        Assert.Equal(calls, viewer.DisposeCalls);
    }

    private sealed class RetryViewer : IRdpViewerService
    {
        public bool FailDisconnect, FailDispose, Disposed;
        public int DisconnectCalls, DisposeCalls;
        public event Action<RdpConnectionStatus>? StatusChanged { add { } remove { } }
        public Task ConnectAsync(RdpInvitationPacket invitation, string password, CancellationToken token = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token = default)
        {
            DisconnectCalls++;
            return FailDisconnect ? Task.FromException(new InvalidOperationException("native disconnect failed")) : Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (FailDispose) throw new InvalidOperationException("native dispose failed");
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
