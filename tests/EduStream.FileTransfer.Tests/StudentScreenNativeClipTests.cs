using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EduStream.Server;
using Forms = System.Windows.Forms;

namespace EduStream.FileTransfer.Tests;

public sealed class StudentScreenNativeClipTests
{
    [Fact]
    public async Task NativeReceiverClipsToViewportAndSurvivesCollapseScrollAndReparent()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = "starting STA";
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await Check(value => stage = value); done.SetResult(); }
                catch (Exception ex) { done.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Native clipping check stalled at: " + stage); }
    }

    private static async Task Check(Action<string> stage)
    {
        using var host = new ClippedWindowsFormsHost { Height = 180 };
        using var child = new Forms.Panel { BackColor = System.Drawing.Color.Black };
        host.Child = child;
        var expander = new Expander { Header = "Synthetic student", Content = host, IsExpanded = true };
        var stack = new StackPanel { Background = System.Windows.Media.Brushes.Transparent };
        stack.Children.Add(new Border { Height = 150 });
        stack.Children.Add(expander);
        stack.Children.Add(new Border { Height = 500 });
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // 실제 HWND로 겹침을 검사하되 창 활성화·입력·RDP 연결·실제 개인정보는 사용하지 않는다.
        var window = new Window { Content = scroll, Width = 400, Height = 300, ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.VirtualScreenLeft - 5000, Top = SystemParameters.VirtualScreenTop - 5000 };
        try
        {
            stage("show inactive off-monitor window");
            window.Show(); stage("initial layout"); await Pump(window);
            Assert.Equal(System.Drawing.Color.Black.ToArgb(), child.BackColor.ToArgb());
            Assert.Null(child.BackgroundImage);
            Assert.False(window.IsActive);
            Assert.True(window.Left + window.ActualWidth < SystemParameters.VirtualScreenLeft);
            var handle = host.Handle;
            var presenter = FindPresenter(scroll);
            var top = host.TransformToAncestor(presenter).Transform(new System.Windows.Point()).Y;
            stage("scroll top edge"); scroll.ScrollToVerticalOffset(top + 80); await Pump(window);
            var clipped = Region(handle);
            Assert.True(clipped.Kind > 1);
            Assert.True(clipped.Box.Top > 0);
            Assert.True(clipped.Box.Bottom > clipped.Box.Top);

            for (var i = 0; i < 50; i++)
            {
                stage("collapse/expand " + i);
                expander.IsExpanded = false; await Pump(window);
                child.Visible = true; // 네이티브 갱신으로 접힌 수신 창이 다시 나타나면 안 된다.
                Assert.False(IsWindowVisible(handle));
                Assert.Equal(1, Region(handle).Kind); // NULLREGION
                expander.IsExpanded = true; await Pump(window);
                Assert.Equal(handle, host.Handle);
                Assert.Same(child, host.Child);
                Assert.False(child.IsDisposed);
                Assert.Null(child.BackgroundImage);
            }

            stage("scroll end"); scroll.ScrollToEnd(); await Pump(window);
            Assert.Equal(1, Region(handle).Kind);
            scroll.ScrollToHome(); await Pump(window);
            Assert.True(Region(handle).Kind > 1);
            window.Height = 220; await Pump(window);
            Assert.True(Region(handle).Box.Bottom < host.ActualHeight * VisualTreeHelper.GetDpi(host).DpiScaleY);

            // 같은 수신 창을 목록 밖으로 옮기면 이전 스크롤 영역 제한이 해제되어야 한다.
            stage("reparent"); expander.Content = null;
            window.Content = host; await Pump(window);
            var full = Region(host.Handle);
            Assert.True(full.Kind > 1);
            Assert.Equal(0, full.Box.Top);
            Assert.Equal(0, full.Box.Left);
            Assert.True(full.Box.Bottom >= host.ActualHeight * VisualTreeHelper.GetDpi(host).DpiScaleY - 1);
            window.Content = scroll; expander.Content = host; await Pump(window);
            Assert.True(Region(host.Handle).Box.Bottom < full.Box.Bottom);
        }
        finally { stage("close test window"); window.Close(); }
    }

    private static async Task Pump(Window window)
    {
        window.UpdateLayout();
        // HWND 표시 갱신을 처리할 시간을 주되 Background 우선순위의 무기한 대기는 피한다.
        // WinForms 호스팅 후에도 후속 검사는 해당 WPF 디스패처에서 실행한다.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        await Task.Delay(15);
        window.UpdateLayout();
    }
    private static ScrollContentPresenter FindPresenter(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollContentPresenter presenter) return presenter;
            try { return FindPresenter(child); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("Scroll viewport not found");
    }
    private static (int Kind, NativeRect Box) Region(IntPtr window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try { var kind = GetWindowRgn(window, region); GetRgnBox(region, out var box); return (kind, box); }
        finally { DeleteObject(region); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out NativeRect rect);
}
