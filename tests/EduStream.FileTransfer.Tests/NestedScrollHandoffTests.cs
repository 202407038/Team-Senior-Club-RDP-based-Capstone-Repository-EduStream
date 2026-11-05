using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EduStream.Client.Behaviors;

namespace EduStream.FileTransfer.Tests;

// Window/Show/네이티브 입력 없이 레이아웃과 라우트 이벤트만 검사한다.
public sealed class NestedScrollHandoffTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public Task EmptyOrShortFileListScrollsOuterPanel(int count) => OnSta(() =>
    {
        var (outer, list) = Create(count);
        var wheel = Wheel(list, -120);
        outer.UpdateLayout();
        Assert.True(wheel.Handled);
        Assert.True(outer.VerticalOffset > 0);
    });

    [Fact]
    public Task LongListKeepsWheelUntilItReachesTheBottom() => OnSta(() =>
    {
        var (outer, list) = Create(60);
        var inner = FindScroll(list)!;
        Assert.True(inner.ScrollableHeight > 0);
        Assert.False(Wheel(list, -120).Handled);
        Assert.Equal(0, outer.VerticalOffset);
        inner.ScrollToEnd(); outer.UpdateLayout();
        Assert.True(Wheel(list, -120).Handled);
        outer.UpdateLayout();
        Assert.True(outer.VerticalOffset > 0);
    });

    [Fact]
    public Task TopBoundaryHandsUpwardWheelToOuterPanel() => OnSta(() =>
    {
        var (outer, list) = Create(60);
        outer.ScrollToVerticalOffset(200); outer.UpdateLayout();
        var before = outer.VerticalOffset;
        Assert.True(Wheel(list, 120).Handled);
        outer.UpdateLayout();
        Assert.True(outer.VerticalOffset < before);
    });

    [Fact]
    public Task StandaloneListAndZeroDeltaAreNotIntercepted() => OnSta(() =>
    {
        var list = new ListBox { Height = 100 };
        list.Measure(new Size(400, 200)); list.Arrange(new Rect(0, 0, 400, 200));
        Assert.False(Wheel(list, -120).Handled);
        var (outer, nested) = Create(0);
        Assert.False(Wheel(nested, 0).Handled);
        Assert.Equal(0, outer.VerticalOffset);
    });

    private static (ScrollViewer, ListBox) Create(int count)
    {
        var list = new ListBox { Height = 100 };
        for (var i = 0; i < count; i++) list.Items.Add("합성 파일 " + i);
        var stack = new StackPanel(); stack.Children.Add(list); stack.Children.Add(new Border { Height = 1000 });
        var outer = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        outer.Measure(new Size(400, 200)); outer.Arrange(new Rect(0, 0, 400, 200)); outer.UpdateLayout();
        Assert.NotNull(FindScroll(list));
        return (outer, list);
    }

    private static MouseWheelEventArgs Wheel(ListBox list, int delta)
    {
        var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        NestedScrollHandoff.Handle(list, e);
        return e;
    }
    private static ScrollViewer? FindScroll(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroll(child) is { } nested) return nested;
        }
        return null;
    }
    private static Task OnSta(Action body)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { body(); result.SetResult(); } catch (Exception ex) { result.SetException(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
}
