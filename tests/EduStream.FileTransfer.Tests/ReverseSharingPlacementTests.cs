using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using EduStream.Server.Rdp;
using EduStream.ShareViewer;
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
        var viewerMonitor = new MonitorInfo
        {
            DeviceName = @"\\.\DISPLAY1",
            Left = 0,
            Top = 0,
            Width = 1920,
            Height = 1080,
            DpiX = 96,
            DpiY = 96,
            ScaleFactor = 1.0,
            IsPrimary = true
        };
        var viewport = NewViewport(new Size(1920, 1080), new Size(960, 540));

        var origin = viewport.TranslateViewerPointToDesktop(new Point(0, 0), secondary, viewerPointIsLogical: true, dpi, viewerMonitor);
        var mapped = viewport.TranslateViewerPointToDesktop(new Point(100, 50), secondary, viewerPointIsLogical: true, dpi, viewerMonitor);
        var sameScale = viewport.TranslateViewerPointToDesktop(new Point(100, 50), secondary, viewerPointIsLogical: true, dpi, secondary);
        var unset = NewViewport(Size.Empty, Size.Empty)
            .TranslateViewerPointToDesktop(new Point(10, 10), secondary, viewerPointIsLogical: true, dpi, viewerMonitor);

        Assert.Equal(new Point(1920, 120), origin);
        Assert.Equal(new Point(2120, 220), mapped);
        Assert.Equal(new Point(2220, 270), sameScale);
        Assert.Equal(Point.Empty, unset);
        Assert.Throws<ArgumentException>(() =>
            viewport.TranslateViewerPointToDesktop(new Point(100, 50), secondary, viewerPointIsLogical: true, dpi));
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
            var mapped = viewport.TranslateViewerPointToDesktop(logical, monitor, viewerPointIsLogical: true, dpi, viewerMonitor: monitor);
            Assert.Equal(monitor.Left + physical.X, mapped.X);
            Assert.Equal(monitor.Top + physical.Y, mapped.Y);

            var presentation = new WdsSharedScreenPresentation(viewport, () => new Size(monitor.Width, monitor.Height));
            presentation.SetSharedMonitor(monitor);
            Assert.False(presentation.PanSupported);
            Assert.Equal(mapped, presentation.MapViewerPointToDesktop(logical, viewerPointIsLogical: true, viewerMonitor: monitor));
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
        await using var host = new StudentDesktopHost("stu-empty");
        var empty = new MonitorInfo { Left = 10, Top = 20, Width = 0, Height = 1080 };
        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(Guid.NewGuid(), empty));
        Assert.Equal(ReverseSessionState.Inactive, host.State);
        Assert.Null(host.SharedMonitor);

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
            await using var host = new StudentDesktopHost("stu-lifetime");
            var sessionId = Guid.NewGuid();
            ViewerHost? refused = null;
            ViewerHost? live = null;
            ProfessorViewerConnection? refusedConnection = null;
            ProfessorViewerConnection? liveConnection = null;
            try
            {
                var sharingId = await host.StartAsync(sessionId);
                var password = Guid.NewGuid().ToString("N");
                var invitation = await host.CreateInvitationAsync(
                    sessionId, sharingId, "prof-lifetime", Guid.NewGuid(), password, DateTimeOffset.UtcNow.AddMinutes(5));

                refused = ViewerHost.ShowAt(40, 40);
                refusedConnection = new ProfessorViewerConnection(refused.Viewer);
                refusedConnection.Connect(invitation.ConnectionString, "prof-lifetime", "wrong-" + password);
                await WaitUntilAsync(
                    () => refusedConnection.Failed || refusedConnection.Terminated,
                    TimeSpan.FromSeconds(30),
                    () => $"거부가 오지 않았습니다. established={refusedConnection.Established} failed={refusedConnection.Failed} terminated={refusedConnection.Terminated}");

                Assert.False(refusedConnection.IsConnectionLive);
                Assert.Equal(ReverseSessionState.Hosting, host.State);
                Assert.True(host.Session.IsReverseSharingActive);

                var release = Stopwatch.StartNew();
                var stop = host.StopAsync();
                var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(20)));
                Assert.True(finished == stop, "학생 PC 공유 종료가 20초 넘게 멈췄습니다.");
                await stop;
                refusedConnection.ReleaseAfterSharingStopped();
                _output.WriteLine($"refused viewer release {release.ElapsedMilliseconds} ms established={refusedConnection.Established} failed={refusedConnection.Failed} terminated={refusedConnection.Terminated}");

                Assert.Equal(ReverseSessionState.Inactive, host.State);
                Assert.True(refused.Viewer.IsDisposed);
                Assert.Null(host.SharedMonitor);

                sharingId = await host.StartAsync(sessionId);
                invitation = await host.CreateInvitationAsync(
                    sessionId, sharingId, "prof-lifetime", Guid.NewGuid(), password, DateTimeOffset.UtcNow.AddMinutes(5));
                var approved = new TaskCompletionSource<ReverseAttendeeEventKind>(TaskCreationOptions.RunContinuationsAsynchronously);
                host.Session.AttendeeLifecycleChanged += (_, e) =>
                {
                    if (e.Kind is ReverseAttendeeEventKind.Approved or ReverseAttendeeEventKind.Rejected)
                        approved.TrySetResult(e.Kind);
                };

                live = ViewerHost.ShowAt(400, 40);
                liveConnection = new ProfessorViewerConnection(live.Viewer);
                liveConnection.Connect(invitation.ConnectionString, "prof-lifetime", password);
                var approval = await Task.WhenAny(approved.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                Assert.True(approval == approved.Task, "재접속 승인이 30초 안에 오지 않았습니다.");
                Assert.Equal(ReverseAttendeeEventKind.Approved, await approved.Task);
                await WaitUntilAsync(() => liveConnection.IsConnectionLive, TimeSpan.FromSeconds(10),
                    () => $"재접속 뷰어가 살아 있지 않습니다. established={liveConnection.Established} failed={liveConnection.Failed} terminated={liveConnection.Terminated}");

                var liveRelease = Stopwatch.StartNew();
                await host.StopAsync();
                liveConnection.ReleaseAfterSharingStopped();
                _output.WriteLine($"live viewer release {liveRelease.ElapsedMilliseconds} ms");
                Assert.True(liveRelease.Elapsed < TimeSpan.FromSeconds(20));
                Assert.True(live.Viewer.IsDisposed);
                Assert.Equal(ReverseSessionState.Inactive, host.State);
            }
            finally
            {
                try { await host.StopAsync(); } catch { /* 이미 종료 */ }
                try { refusedConnection?.ReleaseAfterSharingStopped(); } catch { /* 이미 해제 */ }
                try { liveConnection?.ReleaseAfterSharingStopped(); } catch { /* 이미 해제 */ }
                refused?.Dispose();
                live?.Dispose();
            }
        });

    [Fact]
    public void StudentHost_AndProfessorViewer_LiveInSeparateAssemblies()
    {
        var hostAssembly = typeof(StudentDesktopHost).Assembly;
        var viewerAssembly = typeof(ProfessorReception).Assembly;
        Assert.Equal("EduStream.ShareHost", hostAssembly.GetName().Name);
        Assert.Equal("EduStream.ShareViewer", viewerAssembly.GetName().Name);
        Assert.DoesNotContain(hostAssembly.GetReferencedAssemblies(), a => a.Name == "EduStream.Server");
        Assert.DoesNotContain(viewerAssembly.GetReferencedAssemblies(), a => a.Name is "EduStream.Server" or "EduStream.ShareHost");
        Assert.Null(typeof(ProfessorReception).GetMethod("StartAsync"));
        Assert.Null(typeof(ProfessorReception).GetMethod("StartReverseSharingAsync"));

        var root = FindRepositoryRoot();
        var studentProject = File.ReadAllText(Path.Combine(root, "src", "EduStream.Client", "EduStream.Client.csproj"));
        var professorProject = File.ReadAllText(Path.Combine(root, "src", "EduStream.Server", "EduStream.Server.csproj"));
        Assert.Contains("EduStream.ShareHost.csproj", studentProject);
        Assert.DoesNotContain("EduStream.Server.csproj", studentProject);
        Assert.Contains("EduStream.ShareViewer.csproj", professorProject);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EduStream.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("EduStream.sln 을 찾지 못했습니다.");
    }

    private static WdsViewportAdapter NewViewport(Size source, Size viewport)
    {
        var adapter = new WdsViewportAdapter(new WheelScrollAdapter(), new ViewportFitAdapter());
        if (source.Width > 0 && source.Height > 0)
            adapter.SetSourceSize(source);
        if (viewport.Width > 0 && viewport.Height > 0)
            adapter.SetViewportSize(viewport);
        return adapter;
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
