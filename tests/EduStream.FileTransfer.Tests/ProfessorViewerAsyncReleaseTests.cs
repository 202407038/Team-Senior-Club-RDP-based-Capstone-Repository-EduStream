using EduStream.ShareViewer;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace EduStream.FileTransfer.Tests;

[Collection("WDS integration")]
public sealed class ProfessorViewerAsyncReleaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReleaseAsync_WaitsForTerminalEvent_AndSerializesConcurrentCalls(bool fromWorkerThread)
        => OnSta(async () =>
        {
            using var form = new Forms.Form { ShowInTaskbar = false };
            await using var reception = new ProfessorReception();
            var viewer = AddViewer(form);
            reception.Watch("student", viewer, "invite", "professor", "password");
            var first = fromWorkerThread
                ? Task.Run(() => reception.ReleaseAsync("student"))
                : reception.ReleaseAsync("student");
            await viewer.DisconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = reception.ReleaseAsync("student");
            Assert.False(first.IsCompleted);
            Assert.False(viewer.IsDisposed);
            viewer.EmitTerminated();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(viewer.IsDisposed);
            Assert.Equal(1, viewer.DisconnectCalls);
            Assert.Equal(1, viewer.Disposals);
            Assert.Equal(viewer.OwnerThread, viewer.DisposeThread);
        });

    [Fact]
    public Task CancelledRelease_RetainsRegistration_ThenRetriesWithoutSecondDisconnect()
        => OnSta(async () =>
        {
            using var form = new Forms.Form { ShowInTaskbar = false };
            await using var reception = new ProfessorReception();
            var viewer = AddViewer(form);
            reception.Watch("student", viewer, "invite", "professor", "password");
            using var cancel = new CancellationTokenSource();
            var release = reception.ReleaseAsync("student", cancel.Token);
            await viewer.DisconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => release);
            Assert.False(viewer.IsDisposed);
            using var unused = new PendingViewer();
            Assert.Throws<InvalidOperationException>(() => reception.Watch("student", unused, "invite", "professor", "password"));
            viewer.EmitTerminated();
            await reception.ReleaseAsync("student");
            Assert.True(viewer.IsDisposed);
            Assert.Equal(1, viewer.DisconnectCalls);
            var fresh = AddViewer(form);
            reception.Watch("student", fresh, "invite", "professor", "password");
            fresh.EmitFailed();
            await reception.ReleaseAsync("student");
            Assert.True(fresh.IsDisposed);
        });

    [Fact]
    public Task DisconnectFailure_IsReported_AndRetryCanFinish()
        => OnSta(async () =>
        {
            using var form = new Forms.Form { ShowInTaskbar = false };
            var viewer = AddViewer(form);
            viewer.FailDisconnectOnce = true;
            await using var connection = new ProfessorViewerConnection(viewer);
            connection.Connect("invite", "professor", "password");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ReleaseAfterSharingStoppedAsync());
            Assert.Equal("disconnect failed", error.Message);
            Assert.False(viewer.IsDisposed);
            var retry = connection.ReleaseAfterSharingStoppedAsync();
            viewer.EmitTerminated();
            await retry;
            Assert.True(viewer.IsDisposed);
            Assert.Equal(2, viewer.DisconnectCalls);
        });

    [Fact]
    public Task ReleaseFromTerminalCallback_IsDeferredUntilCallbackReturns()
        => OnSta(async () =>
        {
            using var form = new Forms.Form { ShowInTaskbar = false };
            var viewer = AddViewer(form);
            await using var connection = new ProfessorViewerConnection(viewer);
            connection.Connect("invite", "professor", "password");
            Task? release = null;
            viewer.OnConnectionTerminated += (_, _) =>
            {
                Assert.Throws<InvalidOperationException>(() => connection.ReleaseAfterSharingStopped());
                release = connection.ReleaseAfterSharingStoppedAsync();
                Assert.False(viewer.IsDisposed);
            };
            viewer.EmitTerminated();
            await release!;
            Assert.True(viewer.IsDisposed);
        });

    [Fact]
    public Task SyncReleaseAndErrorEvent_DoNotBypassDisconnectConfirmation()
        => OnSta(async () =>
        {
            using var form = new Forms.Form { ShowInTaskbar = false };
            var viewer = AddViewer(form);
            await using var connection = new ProfessorViewerConnection(viewer);
            connection.Connect("invite", "professor", "password");
            Assert.Throws<InvalidOperationException>(() => connection.ReleaseAfterSharingStopped());
            Assert.False(viewer.IsDisposed);
            viewer.EmitError();
            var release = connection.ReleaseAfterSharingStoppedAsync();
            await viewer.DisconnectEntered.Task;
            Assert.False(release.IsCompleted);
            viewer.EmitTerminated();
            await release;
            Assert.True(viewer.IsDisposed);
        });

    [Fact(Timeout = 30000)]
    public Task MissingTerminalEvent_TimesOutWithoutDisposal_AndCanRetry()
        => OnSta(async () =>
        {
            using var form = new Forms.Form { ShowInTaskbar = false };
            var viewer = AddViewer(form);
            await using var connection = new ProfessorViewerConnection(viewer);
            connection.Connect("invite", "professor", "password");
            await Assert.ThrowsAsync<TimeoutException>(() => connection.ReleaseAfterSharingStoppedAsync());
            Assert.False(viewer.IsDisposed);
            viewer.EmitTerminated();
            await connection.ReleaseAfterSharingStoppedAsync();
            Assert.True(viewer.IsDisposed);
        });

    private static PendingViewer AddViewer(Forms.Form form)
    {
        var viewer = new PendingViewer();
        form.Controls.Add(viewer);
        form.Show();
        viewer.CreateControl();
        return viewer;
    }

    public sealed class PendingViewer : Forms.Panel
    {
        public event EventHandler? OnConnectionTerminated;
        public event EventHandler? OnConnectionFailed;
        public event EventHandler? OnError;
        public TaskCompletionSource DisconnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OwnerThread { get; } = Environment.CurrentManagedThreadId;
        public int DisposeThread { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int Disposals { get; private set; }
        public bool FailDisconnectOnce { get; set; }
        public void Connect(string invite, string name, string password) { }
        public void Disconnect()
        {
            DisconnectCalls++;
            if (FailDisconnectOnce)
            {
                FailDisconnectOnce = false;
                throw new InvalidOperationException("disconnect failed");
            }
            DisconnectEntered.TrySetResult();
        }
        public void EmitTerminated() => OnConnectionTerminated?.Invoke(this, EventArgs.Empty);
        public void EmitFailed() => OnConnectionFailed?.Invoke(this, EventArgs.Empty);
        public void EmitError() => OnError?.Invoke(this, EventArgs.Empty);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposals++;
                DisposeThread = Environment.CurrentManagedThreadId;
            }
            base.Dispose(disposing);
        }
    }

    private static Task OnSta(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? failure = null;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }));
            Dispatcher.Run();
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
