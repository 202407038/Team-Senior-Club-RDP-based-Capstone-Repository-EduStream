using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

/// <summary>제품 판서 오버레이·전송 JSON 이 테스트 전용 캔버스 없이 동작하는지 확인한다.</summary>
public class AnnotationOverlayLayerTests
{
    [Fact]
    public Task JsonRoundTrip_PreservesStroke_AndOverlayDrawsIt() => RunOnStaAsync(async () =>
    {
        var stroke = new AnnotationStroke
        {
            ParticipantId = "prof-1",
            Tool = AnnotationTool.Line,
            Color = new AnnotationColor(0, 255, 255),
            StrokeWidth = 8,
            Points = new[] { new Point(10, 10), new Point(200, 10) }
        };

        var json = AnnotationStrokeWire.ToJson(stroke);
        var restored = AnnotationStrokeWire.FromJson(json);
        Assert.Equal(stroke.ParticipantId, restored.ParticipantId);
        Assert.Equal(stroke.Tool, restored.Tool);
        Assert.Equal(2, restored.Points.Count);

        var layer = new AnnotationOverlayLayer(240, 80);
        await layer.ReceiveRemoteStrokeJsonAsync(json);
        Assert.Equal(1, layer.ShapeCount);
        Assert.True(layer.CountPixels((r, g, b) => r < 60 && g > 200 && b > 200) > 100);
    });

    [Fact]
    public Task BindLocalRenderer_HideAndShow_ReplacesOutput() => RunOnStaAsync(async () =>
    {
        var manager = new AnnotationManager();
        var engine = new AnnotationEngineAdapter(manager);
        var layer = new AnnotationOverlayLayer(240, 80);
        layer.BindLocalRenderer(engine);
        await engine.ActivateEngineAsync();

        await engine.ReceiveStrokeAsync(new AnnotationStroke
        {
            ParticipantId = "p",
            Tool = AnnotationTool.Line,
            Color = new AnnotationColor(255, 0, 0),
            StrokeWidth = 8,
            Points = new[] { new Point(10, 20), new Point(200, 20) }
        });
        Assert.Equal(1, layer.ShapeCount);

        await engine.SetLayerVisibilityAsync(false);
        Assert.Equal(0, layer.ShapeCount);

        await engine.SetLayerVisibilityAsync(true);
        Assert.Equal(1, layer.ShapeCount);
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
                try { Dispatcher.Run(); } catch (InvalidOperationException) { }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
