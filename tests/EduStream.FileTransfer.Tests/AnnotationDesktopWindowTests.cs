using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using EduStream.Server;
using EduStream.Server.ViewModels;

namespace EduStream.FileTransfer.Tests;

[Collection("WDS integration")]
public sealed class AnnotationDesktopWindowTests
{
    // 실제 창 생성/소유/종료 검사다. 포인터 입력이나 학생 수신 픽셀 검사를 대체하지 않는다.
    [WdsFact(Timeout = 30000)]
    public Task ToolbarIsOwnedByShownOverlay_AndDrawingCanBeDisabled()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Exception? failure = null;
            dispatcher.BeginInvoke((Action)(() =>
            {
            try
            {
                var monitor = new EduStream.ShareHost.MonitorInfo
                { Width = 640, Height = 360, DpiX = 96, DpiY = 96, ScaleFactor = 1 };
                using (var drawing = new AnnotationDesktopWindow(monitor, new AnnotationToolsViewModel()))
                {
                    var overlay = Field<Window>(drawing, "_overlay");
                    var toolbar = Field<Window>(drawing, "_toolbar");
                    Assert.True(overlay.IsVisible);
                    Assert.True(toolbar.IsVisible);
                    Assert.Same(overlay, toolbar.Owner);
                    bool? enabled = null;
                    drawing.DrawingChanged += value => enabled = value;
                    drawing.SetDrawing(false);
                    Assert.Equal(false, enabled);
                    Assert.True(overlay.IsVisible); // OFF는 레이어를 없애는 동작이 아니다.
                    // 제목 표시줄 X/Alt+F4는 Closing 경로로 들어간다.
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        drawing.SetDrawing(true);
                        Assert.True(toolbar.IsVisible);
                        toolbar.Close();
                        Assert.False(toolbar.IsVisible);
                        Assert.True(overlay.IsVisible);
                        Assert.Equal(false, enabled);
                    }
                    drawing.SetDrawing(true);
                    Assert.True(toolbar.IsVisible);
                    Assert.Equal(true, enabled);
                }
            }
            catch (Exception error) { failure = error; }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            // 실제 창의 메시지 큐와 Dispatcher 자원을 종료한 뒤 다음 native 검사를 시작한다.
            Dispatcher.Run();
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
}
