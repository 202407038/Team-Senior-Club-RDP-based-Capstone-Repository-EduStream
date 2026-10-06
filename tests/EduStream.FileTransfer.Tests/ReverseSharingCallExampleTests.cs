using System.ComponentModel;
using System.Drawing;
using System.Windows.Threading;
using EduStream.Server.Rdp;
using EduStream.ShareHost;
using EduStream.ShareViewer;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// docs/work/REVERSE_SHARING_CALL_EXAMPLE.md 의 표시 순서를 그대로 실행합니다.
/// 뷰어를 붙이기 전에 맞춤을 호출하면 실패하고, 붙인 뒤에는 맞춤과 배율이 컨트롤에 적용됩니다.
/// </summary>
[Collection("WDS integration")]
public sealed class ReverseSharingCallExampleTests
{
    [Fact]
    public async Task FitAsync_WithoutViewer_ThrowsNotInitialized()
    {
        var viewport = new WdsViewportAdapter(new WheelScrollAdapter(), new ViewportFitAdapter());
        viewport.SetSourceSize(new Size(1920, 1080));
        var presentation = new WdsSharedScreenPresentation(viewport, () => new Size(960, 540));

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => presentation.FitAsync());
        Assert.Equal("WDS Viewer가 초기화되지 않았습니다.", missing.Message);
    }

    [WdsFact(Timeout = 120000)]
    public Task InvalidNativeConnection_ReleasesViewerAndAllowsFreshAttempt()
        => RunOnStaAsync(() =>
        {
            using var form = new Forms.Form { ShowInTaskbar = false, Width = 320, Height = 240 };
            using var reception = new ProfessorReception();
            form.Show();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var viewer = new AxRDPCOMAPILib.AxRDPViewer();
                ((ISupportInitialize)viewer).BeginInit();
                form.Controls.Add(viewer);
                ((ISupportInitialize)viewer).EndInit();
                viewer.CreateControl();
                var error = Assert.ThrowsAny<Exception>(() =>
                    reception.Watch("student", viewer, "invalid-invitation", "professor", "password"));
                Assert.DoesNotContain("이미 있습니다", error.Message);
                Assert.True(viewer.IsDisposed);
                Assert.Null(viewer.Parent);
            }
            return Task.CompletedTask;
        });

    [WdsFact(Timeout = 120000)]
    public Task DocumentedSequence_AttachesRealViewer_ThenFitsZoomsAndReleases()
        => RunSharingSequenceAsync(waitForConnection: false);

    [WdsFact(Timeout = 120000)]
    public Task EstablishedSequence_AttachesRealViewer_ThenFitsZoomsAndReleases()
        => RunSharingSequenceAsync(waitForConnection: true);

    // 연결 전 조기 종료 재현과 정상 연결 후 종료를 분리한다. 연결 전 종료 결함을 대기로 숨기지 않는다.
    private static Task RunSharingSequenceAsync(bool waitForConnection)
        => RunOnStaAsync(async () =>
        {
            await using var host = new StudentDesktopHost("stu-example");
            await using var reception = new ProfessorReception();
            Forms.Form? form = null;
            try
            {
                var share = new MonitorDpiAdapter().GetMonitors().First(m => m.IsPrimary);
                var sessionId = Guid.NewGuid();
                var sharingId = await host.StartAsync(sessionId, share);
                var password = Guid.NewGuid().ToString("N");
                var notice = await host.CreateInvitationAsync(
                    sessionId, sharingId, "prof-example", Guid.NewGuid(), password, DateTimeOffset.UtcNow.AddMinutes(5));

                form = new Forms.Form { ShowInTaskbar = false, Width = 1000, Height = 700 };
                var surface = new Forms.Panel { Dock = Forms.DockStyle.Fill };
                var viewer = new AxRDPCOMAPILib.AxRDPViewer();
                ((ISupportInitialize)viewer).BeginInit();
                surface.Controls.Add(viewer);
                ((ISupportInitialize)viewer).EndInit();
                form.Controls.Add(surface);
                form.Show();
                viewer.CreateControl();
                viewer.Dock = Forms.DockStyle.None;
                viewer.Bounds = new Rectangle(0, 0, 960, 540);

                var connection = reception.Watch("stu-example", viewer, notice.ConnectionString, notice.ProfessorId, password);
                if (waitForConnection)
                {
                    var connectedBy = DateTime.UtcNow.AddSeconds(15);
                    while (!connection.IsConnectionLive && !connection.Failed && !connection.Terminated && DateTime.UtcNow < connectedBy)
                        await Task.Delay(20);
                    Assert.True(connection.IsConnectionLive,
                        $"실제 연결 미완료: established={connection.Established}, failed={connection.Failed}, terminated={connection.Terminated}");
                }

                var viewport = new WdsViewportAdapter(new WheelScrollAdapter(), new ViewportFitAdapter());
                viewport.SetAxViewer(viewer);
                viewport.SetSourceSize(new Size(share.Width, share.Height));
                var presentation = new WdsSharedScreenPresentation(viewport, () => new Size(960, 540));
                presentation.SetSharedMonitor(host.SharedMonitor);

                await presentation.FitAsync();
                Assert.True(viewer.SmartSizing);
                Assert.True(viewport.LastAppliedViewerBounds.Width > 0);
                var fitted = viewport.LastAppliedViewerBounds;

                await presentation.ZoomAsync(1.25, normalizedX: 0, normalizedY: 0);
                Assert.True(viewport.LastAppliedViewerBounds.Width > fitted.Width);

                var professorMonitor = new MonitorDpiAdapter().GetMonitors().First(m => m.IsPrimary);
                var desktop = presentation.MapViewerPointToDesktop(new Point(100, 50), viewerPointIsLogical: true, viewerMonitor: professorMonitor);
                Assert.True(desktop.X >= professorMonitor.Left && desktop.Y >= professorMonitor.Top);

                var stopped = host.StopAsync();
                Assert.Same(stopped, await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(20))));
                await stopped;
                await reception.ReleaseAsync("stu-example");
                Assert.True(viewer.IsDisposed);
            }
            finally
            {
                try { await host.StopAsync(); } catch { /* 이미 종료 */ }
                await reception.DisposeAsync();
                try { form?.Close(); } catch { /* 창이 이미 닫힘 */ }
            }
        });

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
                try { Dispatcher.Run(); } catch (InvalidOperationException) { /* 본문이 동기 완료 */ }
            }
        })
        { IsBackground = true, Name = "EduStream-CallExample-STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
