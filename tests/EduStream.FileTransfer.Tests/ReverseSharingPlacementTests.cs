using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using EduStream.Server.Rdp;
using Xunit.Abstractions;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2단계에서 채운 연결의 실제 동작.
/// 좌표·DPI 는 엔진 없이 확인하고, 세션 수명과 모니터 공유 영역은 실제 WDS COM 으로 확인합니다.
/// </summary>
[Collection("WDS integration")]
public sealed class ReverseSharingPlacementTests
{
    private readonly ITestOutputHelper _output;

    public ReverseSharingPlacementTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TranslateViewerPointToDesktop_AppliesDpiThenMonitorOrigin()
    {
        var dpi = new MonitorDpiAdapter();
        var secondary = new MonitorInfo
        {
            DeviceName = @"\\.\DISPLAY2",
            Left = 1920,
            Top = 120,
            Width = 1920,
            Height = 1080,
            DpiX = 144,
            DpiY = 144,
            ScaleFactor = 1.5,
            IsPrimary = false
        };
        var viewport = NewViewport(new Size(1920, 1080), new Size(960, 540));

        var origin = viewport.TranslateViewerPointToDesktop(new Point(0, 0), secondary, viewerPointIsLogical: true, dpi);
        var mapped = viewport.TranslateViewerPointToDesktop(new Point(100, 50), secondary, viewerPointIsLogical: true, dpi);
        var unset = NewViewport(Size.Empty, Size.Empty)
            .TranslateViewerPointToDesktop(new Point(10, 10), secondary, viewerPointIsLogical: true, dpi);

        Assert.Equal(new Point(1920, 120), origin);
        Assert.Equal(new Point(2220, 270), mapped);
        Assert.Equal(Point.Empty, unset);
    }

    [Fact]
    public void TranslateViewerPointToDesktop_UsesEveryRealMonitorOriginAndScale()
    {
        var dpi = new MonitorDpiAdapter();
        var monitors = dpi.GetMonitors();
        Assert.NotEmpty(monitors);

        foreach (var monitor in monitors)
        {
            _output.WriteLine(
                $"monitor {monitor.DeviceName} primary={monitor.IsPrimary} origin=({monitor.Left},{monitor.Top}) size={monitor.Width}x{monitor.Height} dpi={monitor.DpiX} scale={monitor.ScaleFactor}");
            Assert.True(monitor.Width > 0 && monitor.Height > 0);

            var viewport = NewViewport(new Size(monitor.Width, monitor.Height), new Size(monitor.Width, monitor.Height));
            var origin = viewport.TranslateViewerPointToDesktop(Point.Empty, monitor, viewerPointIsLogical: false, dpi);
            Assert.Equal(monitor.Left, origin.X);
            Assert.Equal(monitor.Top, origin.Y);

            var logical = new Point(10, 20);
            var physical = dpi.LogicalToPhysical(logical, monitor);
            var mapped = viewport.TranslateViewerPointToDesktop(logical, monitor, viewerPointIsLogical: true, dpi);
            Assert.Equal(monitor.Left + physical.X, mapped.X);
            Assert.Equal(monitor.Top + physical.Y, mapped.Y);

            var presentation = new WdsSharedScreenPresentation(viewport, () => new Size(monitor.Width, monitor.Height));
            presentation.SetSharedMonitor(monitor);
            Assert.False(presentation.PanSupported);
            Assert.Equal(mapped, presentation.MapViewerPointToDesktop(logical, viewerPointIsLogical: true));
        }
    }

    [Fact]
    public async Task PanAsync_StaysUnsupported()
    {
        var presentation = new WdsSharedScreenPresentation(NewViewport(new Size(100, 100), new Size(100, 100)), () => new Size(100, 100));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => presentation.PanAsync(0.1, 0.2));

        Assert.False(presentation.PanSupported);
        Assert.Equal(WdsSharedScreenPresentation.PanNotSupportedMessage, ex.Message);
    }

    [Fact]
    public async Task MonitorShare_RejectsEmptyMonitor_AndForeignManager()
    {
        await using var station = new ReverseStudentStation("stu-empty");
        var empty = new MonitorInfo { Left = 10, Top = 20, Width = 0, Height = 1080 };
        await Assert.ThrowsAsync<ArgumentException>(() => station.StartAsync(Guid.NewGuid(), empty));
        Assert.Equal(ReverseSessionState.Inactive, station.Host.CurrentState);
        Assert.Null(station.SharedMonitor);

        var foreign = new ReverseScreenShareAdapter(new UnusedReverseSession());
        var monitor = new MonitorInfo { Width = 100, Height = 100 };
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            foreign.StartReverseSharingAsync(Guid.NewGuid(), "stu", monitor));
        Assert.Contains("ReverseSessionManager", ex.Message);
    }

    [WdsFact(Timeout = 90000)]
    public async Task Sharing_AppliesEnumeratedMonitorRect()
    {
        var monitors = new MonitorDpiAdapter().GetMonitors();
        Assert.NotEmpty(monitors);
        var share = monitors.FirstOrDefault(m => !m.IsPrimary) ?? monitors[0];
        _output.WriteLine(
            $"share target primary={share.IsPrimary} origin=({share.Left},{share.Top}) size={share.Width}x{share.Height} monitors={monitors.Count}");

        var manager = new ReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(manager);
        try
        {
            var sharingId = await adapter.StartReverseSharingAsync(Guid.NewGuid(), "stu-monitor", share);
            Assert.NotEqual(Guid.Empty, sharingId);
            Assert.Equal(ReverseSessionState.Hosting, manager.CurrentState);
            var stored = manager.SharedMonitor;
            Assert.NotNull(stored);
            Assert.Equal(share.Left, stored.Left);
            Assert.Equal(share.Top, stored.Top);
            Assert.Equal(share.Width, stored.Width);
            Assert.Equal(share.Height, stored.Height);
        }
        finally
        {
            await manager.StopReverseSharingAsync();
            manager.Dispose();
        }

        Assert.Null(manager.SharedMonitor);
        Assert.Equal(ReverseSessionState.Inactive, manager.CurrentState);
    }

    [WdsFact(Timeout = 120000)]
    public Task ComLifetime_RefusedViewerIsReleasedAfterStop_ThenSameStationReconnects()
        => RunOnStaAsync(async () =>
        {
            await using var station = new ReverseStudentStation("stu-lifetime");
            var sessionId = Guid.NewGuid();
            ViewerHost? refused = null;
            ViewerHost? live = null;
            try
            {
                var sharingId = await station.StartAsync(sessionId);
                var password = Guid.NewGuid().ToString("N");
                var invitation = await station.Host.CreateProfessorInvitationAsync(
                    sessionId, sharingId, "prof-lifetime", Guid.NewGuid(), password, DateTimeOffset.UtcNow.AddMinutes(5));

                refused = ViewerHost.ShowAt(40, 40);
                var refusedConnection = station.ConnectProfessor(
                    refused.Viewer, invitation.ConnectionString, "prof-lifetime", "wrong-" + password);
                await WaitUntilAsync(
                    () => refusedConnection.Failed || refusedConnection.Terminated,
                    TimeSpan.FromSeconds(30),
                    () => $"거부가 오지 않았습니다. established={refusedConnection.Established} failed={refusedConnection.Failed} terminated={refusedConnection.Terminated}");

                Assert.False(refusedConnection.IsConnectionLive);
                Assert.Equal(ReverseSessionState.Hosting, station.Host.CurrentState);
                Assert.True(station.Host.IsReverseSharingActive);

                var release = Stopwatch.StartNew();
                var stop = station.StopAsync();
                var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(20)));
                Assert.True(finished == stop, "거절된 뷰어를 공유 종료 뒤에 해제하는 동안 20초 넘게 멈췄습니다.");
                await stop;
                _output.WriteLine($"refused viewer release {release.ElapsedMilliseconds} ms established={refusedConnection.Established} failed={refusedConnection.Failed} terminated={refusedConnection.Terminated}");

                Assert.Equal(ReverseSessionState.Inactive, station.Host.CurrentState);
                Assert.True(refused.Viewer.IsDisposed);
                Assert.Null(station.SharedMonitor);

                sharingId = await station.StartAsync(sessionId);
                invitation = await station.Host.CreateProfessorInvitationAsync(
                    sessionId, sharingId, "prof-lifetime", Guid.NewGuid(), password, DateTimeOffset.UtcNow.AddMinutes(5));
                var approved = new TaskCompletionSource<ReverseAttendeeEventKind>(TaskCreationOptions.RunContinuationsAsynchronously);
                station.Host.AttendeeLifecycleChanged += (_, e) =>
                {
                    if (e.Kind is ReverseAttendeeEventKind.Approved or ReverseAttendeeEventKind.Rejected)
                        approved.TrySetResult(e.Kind);
                };

                live = ViewerHost.ShowAt(400, 40);
                var liveConnection = station.ConnectProfessor(
                    live.Viewer, invitation.ConnectionString, "prof-lifetime", password);
                var approval = await Task.WhenAny(approved.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                Assert.True(approval == approved.Task, "재접속 승인이 30초 안에 오지 않았습니다.");
                Assert.Equal(ReverseAttendeeEventKind.Approved, await approved.Task);
                await WaitUntilAsync(() => liveConnection.IsConnectionLive, TimeSpan.FromSeconds(10),
                    () => $"재접속 뷰어가 살아 있지 않습니다. established={liveConnection.Established} failed={liveConnection.Failed} terminated={liveConnection.Terminated}");

                var liveRelease = Stopwatch.StartNew();
                await station.StopAsync();
                _output.WriteLine($"live viewer release {liveRelease.ElapsedMilliseconds} ms");
                Assert.True(liveRelease.Elapsed < TimeSpan.FromSeconds(20));
                Assert.True(live.Viewer.IsDisposed);
                Assert.Equal(ReverseSessionState.Inactive, station.Host.CurrentState);
            }
            finally
            {
                try { await station.StopAsync(); } catch { /* 이미 종료 */ }
                refused?.Dispose();
                live?.Dispose();
            }
        });

    [WdsFact(Timeout = 180000)]
    public Task TwoStudents_KeepIndependentSessionsAndViewers()
        => RunOnStaAsync(async () =>
        {
            await using var room = new ReverseClassroomPlacement();
            var first = room.StationFor("stu-a");
            var second = room.StationFor("stu-b");
            Assert.NotSame(first, second);
            Assert.NotSame(first.Host, second.Host);
            Assert.Same(first, room.StationFor("stu-a"));

            var sessionId = Guid.NewGuid();
            ViewerHost? firstView = null;
            ViewerHost? secondView = null;
            try
            {
                var firstSharing = await first.StartAsync(sessionId);
                var secondSharing = await second.StartAsync(sessionId);
                Assert.NotEqual(firstSharing, secondSharing);
                Assert.Equal(ReverseSessionState.Hosting, first.Host.CurrentState);
                Assert.Equal(ReverseSessionState.Hosting, second.Host.CurrentState);
                await Assert.ThrowsAsync<InvalidOperationException>(() => first.StartAsync(sessionId));

                var firstPassword = Guid.NewGuid().ToString("N");
                var secondPassword = Guid.NewGuid().ToString("N");
                var firstInvite = await first.Host.CreateProfessorInvitationAsync(
                    sessionId, firstSharing, "prof-a", Guid.NewGuid(), firstPassword, DateTimeOffset.UtcNow.AddMinutes(5));
                var secondInvite = await second.Host.CreateProfessorInvitationAsync(
                    sessionId, secondSharing, "prof-b", Guid.NewGuid(), secondPassword, DateTimeOffset.UtcNow.AddMinutes(5));
                Assert.NotEqual(firstInvite.ConnectionString, secondInvite.ConnectionString);

                var firstApproved = WatchApproval(first);
                var secondApproved = WatchApproval(second);
                firstView = ViewerHost.ShowAt(40, 40);
                secondView = ViewerHost.ShowAt(400, 40);
                first.ConnectProfessor(firstView.Viewer, firstInvite.ConnectionString, "prof-a", firstPassword);
                second.ConnectProfessor(secondView.Viewer, secondInvite.ConnectionString, "prof-b", secondPassword);

                await WaitApprovalAsync(firstApproved, "stu-a");
                await WaitApprovalAsync(secondApproved, "stu-b");
                Assert.Equal(1, first.Host.ActiveAttendeeCount);
                Assert.Equal(1, second.Host.ActiveAttendeeCount);

                await first.StopAsync();
                Assert.Equal(ReverseSessionState.Inactive, first.Host.CurrentState);
                Assert.True(firstView.Viewer.IsDisposed);
                Assert.Equal(ReverseSessionState.Connected, second.Host.CurrentState);
                Assert.Equal(1, second.Host.ActiveAttendeeCount);
                Assert.True(second.Host.IsReverseSharingActive);

                await second.StopAsync();
                Assert.Equal(ReverseSessionState.Inactive, second.Host.CurrentState);
                Assert.True(secondView.Viewer.IsDisposed);
            }
            finally
            {
                try { await room.StopAllAsync(); } catch { /* 이미 종료 */ }
                firstView?.Dispose();
                secondView?.Dispose();
            }
        });

    private static WdsViewportAdapter NewViewport(Size source, Size viewport)
    {
        var adapter = new WdsViewportAdapter(new WheelScrollAdapter(), new ViewportFitAdapter());
        if (source.Width > 0 && source.Height > 0)
            adapter.SetSourceSize(source);
        if (viewport.Width > 0 && viewport.Height > 0)
            adapter.SetViewportSize(viewport);
        return adapter;
    }

    private static TaskCompletionSource<ReverseAttendeeEventKind> WatchApproval(ReverseStudentStation station)
    {
        var approved = new TaskCompletionSource<ReverseAttendeeEventKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        station.Host.AttendeeLifecycleChanged += (_, e) =>
        {
            if (e.Kind is ReverseAttendeeEventKind.Approved or ReverseAttendeeEventKind.Rejected)
                approved.TrySetResult(e.Kind);
        };
        return approved;
    }

    private static async Task WaitApprovalAsync(TaskCompletionSource<ReverseAttendeeEventKind> approved, string studentId)
    {
        var finished = await Task.WhenAny(approved.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(finished == approved.Task, $"{studentId} 승인이 30초 안에 오지 않았습니다.");
        Assert.Equal(ReverseAttendeeEventKind.Approved, await approved.Task);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string> describeFailure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(describeFailure());
            await Task.Delay(200);
        }
    }

    private static Task RunOnStaAsync(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Task task;
            try { task = body(); }
            catch (Exception ex) { task = Task.FromException(ex); }

            task.ContinueWith(t =>
            {
                if (t.IsFaulted) completion.TrySetException(t.Exception!.InnerExceptions);
                else if (t.IsCanceled) completion.TrySetCanceled();
                else completion.TrySetResult();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }, TaskScheduler.Default);

            if (!dispatcher.HasShutdownStarted)
            {
                try { Dispatcher.Run(); } catch (InvalidOperationException) { /* 본문이 동기 완료된 경우 */ }
            }
        })
        { IsBackground = true, Name = "EduStream-Placement-STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class ViewerHost : IDisposable
    {
        private readonly Wpf.Window _window;
        public AxRDPCOMAPILib.AxRDPViewer Viewer { get; }

        private ViewerHost(double left, double top)
        {
            var surface = new Forms.Panel { Dock = Forms.DockStyle.Fill, BackColor = Color.Black };
            Viewer = new AxRDPCOMAPILib.AxRDPViewer();
            ((ISupportInitialize)Viewer).BeginInit();
            surface.Controls.Add(Viewer);
            ((ISupportInitialize)Viewer).EndInit();
            _window = new Wpf.Window
            {
                Title = "EduStream placement viewer",
                WindowStyle = Wpf.WindowStyle.None,
                ResizeMode = Wpf.ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Width = 320,
                Height = 240,
                Left = left,
                Top = top,
                Content = new WindowsFormsHost { Child = surface }
            };
        }

        public static ViewerHost ShowAt(double left, double top)
        {
            var host = new ViewerHost(left, top);
            host._window.Show();
            host.Viewer.CreateControl();
            return host;
        }

        public void Dispose()
        {
            try { _window.Close(); } catch { /* 뷰어 해제 뒤 창이 이미 닫힘 */ }
        }
    }

    private sealed class UnusedReverseSession : IReverseSessionManager
    {
        public bool IsReverseSharingActive => false;
        public ReverseSessionState CurrentState => ReverseSessionState.Inactive;
        public event EventHandler<FrameReceivedEventArgs>? FrameReceived;
        public Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, CancellationToken cancellationToken = default) => throw new NotSupportedException("unused");
        public Task<ReverseInvitationPacket> CreateProfessorInvitationAsync(Guid sessionId, Guid sharingId, string professorId, Guid connectionId, string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) => throw new NotSupportedException("unused");
        public Task ConnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("unused");
        public Task OnConnectedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("unused");
        public Task OnConnectionFailedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("unused");
        public Task OnDisconnectedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("unused");
        public void ReceiveFrame(byte[] frameData) => FrameReceived?.Invoke(this, new FrameReceivedEventArgs { FrameData = frameData });
        public Task StopReverseSharingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
