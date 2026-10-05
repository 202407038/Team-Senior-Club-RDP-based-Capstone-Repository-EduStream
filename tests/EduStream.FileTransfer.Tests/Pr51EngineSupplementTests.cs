using System.Drawing;
using System.Threading;
using System.Windows.Threading;
using EduStream.Server.Rdp;
using Xunit;
using CoreTool = EduStream.Core.Collaboration.AnnotationTool;

namespace EduStream.FileTransfer.Tests;

public class Pr51EngineSupplementTests
{
    [Fact]
    public Task ClearWithoutOff_AndUndo_RestoreContentWithoutChangingDrawing() => OnSta(async () =>
    {
        var engine = new AnnotationEngineAdapter(new AnnotationManager());
        using var layer = new AnnotationOverlayLayer(240, 100);
        layer.BindLocalRenderer(engine);
        var controller = new AnnotationEngineController(engine);
        controller.SetDrawing(true);
        controller.SelectTool(CoreTool.Line, 0xFFFF0000, 6);
        await controller.SubmitStrokeAsync("prof", new[] { new Point(10, 20), new Point(200, 20) });
        Assert.Equal(1, layer.ShapeCount);
        controller.Clear(false);
        Assert.True(controller.State.Drawing);
        Assert.True(engine.IsEngineActive);
        Assert.Equal(0, layer.ShapeCount);
        controller.Undo();
        Assert.Equal(1, layer.ShapeCount);
        Assert.True(layer.CountPixels((r,g,b) => r > 200 && g < 60 && b < 60) > 100);
        controller.Clear(true);
        Assert.False(controller.State.Drawing);
        Assert.False(engine.IsEngineActive);
        controller.Undo();
        Assert.Equal(1, layer.ShapeCount);
        Assert.False(controller.State.Drawing); // 복원은 판서 ON을 강제하지 않는다.
    });

    [Fact]
    public Task ToolSelection_EllipseColorThickness_EraserAndUndo_AffectRealLayer() => OnSta(async () =>
    {
        var engine = new AnnotationEngineAdapter(new AnnotationManager());
        using var layer = new AnnotationOverlayLayer(240, 100);
        layer.BindLocalRenderer(engine);
        var controller = new AnnotationEngineController(engine);
        controller.SetDrawing(true);
        controller.SelectTool(CoreTool.Ellipse, 0xFF00FF00, 8);
        await controller.SubmitStrokeAsync("prof", new[] { new Point(20,20), new Point(100,80) });
        var stroke = Assert.Single(await engine.GetAllStrokesAsync());
        Assert.Equal(AnnotationTool.Circle, stroke.Tool);
        Assert.Equal(AnnotationColor.Green, stroke.Color);
        Assert.Equal(8, stroke.StrokeWidth);
        Assert.True(layer.CountPixels((r,g,b) => r < 60 && g > 200 && b < 60) > 100);
        controller.SelectTool(CoreTool.Eraser, 0xFFFFFFFF, 10);
        // 도형 안쪽만 지나면 외곽선은 지워지지 않는다.
        await controller.SubmitStrokeAsync("prof", new[] { new Point(50,50), new Point(60,50) });
        Assert.Equal(1, layer.ShapeCount);
        await controller.SubmitStrokeAsync("prof", new[] { new Point(10,50), new Point(30,50) });
        Assert.Equal(0, layer.ShapeCount);
        Assert.Empty(await engine.GetAllStrokesAsync());
        controller.Undo();
        Assert.Equal(1, layer.ShapeCount);
        Assert.Single(await engine.GetAllStrokesAsync());
    });

    [Fact]
    public Task SeparateJsonReceiver_TracksHideHiddenDrawingClearAndUndo() => OnSta(async () =>
    {
        var engine = new AnnotationEngineAdapter(new AnnotationManager());
        using var sender = new AnnotationOverlayLayer(240,100);
        using var receiver = new AnnotationOverlayLayer(240,100);
        var payloads = new Queue<string>();
        sender.BindTransmission(engine, (json, _) => payloads.Enqueue(json));
        async Task Deliver()
        {
            while (payloads.TryDequeue(out var json))
                await receiver.ReceiveRemoteStrokeJsonAsync(json);
        }
        await engine.ActivateEngineAsync();
        await engine.ReceiveStrokeAsync(Stroke());
        await Deliver();
        Assert.Equal(1, receiver.ShapeCount);
        await engine.SetLayerVisibilityAsync(false);
        await Deliver();
        Assert.Equal(0, receiver.ShapeCount);
        await engine.ReceiveStrokeAsync(Stroke());
        Assert.Empty(payloads);
        await engine.SetLayerVisibilityAsync(true);
        await Deliver();
        Assert.Equal(2, receiver.ShapeCount);
        await engine.ClearAllStrokesAsync();
        await Deliver();
        Assert.Equal(0, receiver.ShapeCount);
        await engine.UndoAsync();
        await Deliver();
        Assert.Equal(2, receiver.ShapeCount);
        await engine.ReceiveStrokeAsync(new AnnotationStroke {
            ParticipantId="p", Tool=AnnotationTool.Eraser, StrokeWidth=10,
            Points=new[] {new Point(50,10), new Point(50,30)} });
        await Deliver();
        Assert.Equal(0, receiver.ShapeCount);
        await engine.UndoAsync();
        await Deliver();
        Assert.Equal(2, receiver.ShapeCount);
    });

    [Fact]
    public Task BackgroundReceive_IsMarshalledToOverlayDispatcher() => OnSta(async () =>
    {
        using var layer = new AnnotationOverlayLayer(240,100);
        await Task.Run(() => layer.ReceiveRemoteStrokeJsonAsync(AnnotationStrokeWire.ToJson(Stroke())));
        Assert.Equal(1, layer.ShapeCount);
    });

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ZoomCoordinates_UseActualRenderedBounds(double zoom)
    {
        var viewport = new WdsViewportAdapter(new WheelScrollAdapter(), new ViewportFitAdapter());
        viewport.SetSourceSize(new Size(1920,1080));
        viewport.SetViewportSize(new Size(960,540));
        viewport.ApplyFitMode(FitMode.Fit);
        viewport.SetZoomLevel(zoom);
        var bounds = viewport.CalculateRenderBounds(new Size(960,540));
        Assert.Equal(bounds.Size, viewport.CurrentViewportSize);
        Assert.Equal(new Point(960,540), viewport.TranslateViewportToSource(
            new Point(bounds.Width/2,bounds.Height/2)));
        Assert.Equal(new Point(0,0), viewport.TranslateViewportToSource(Point.Empty));
        Assert.Equal(new Point(1919,1079), viewport.TranslateViewportToSource(
            new Point(bounds.Width,bounds.Height)));
    }

    [Fact]
    public async Task ReceiveStroke_WaitsForRendererInsteadOfReturningBeforeOutput()
    {
        var engine = new AnnotationEngineAdapter(new AnnotationManager());
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AddRenderHandler(_ => applied.Task);
        await engine.ActivateEngineAsync();
        var receiving = engine.ReceiveStrokeAsync(Stroke());
        Assert.False(receiving.IsCompleted);
        applied.SetResult();
        await receiving;
    }

    [Fact]
    public void ResizeAndRefit_KeepContainerSeparateFromZoomedImage()
    {
        var viewport = new WdsViewportAdapter(new WheelScrollAdapter(), new ViewportFitAdapter());
        viewport.SetSourceSize(new Size(1920,1080));
        viewport.SetViewportSize(new Size(960,540));
        viewport.SetZoomLevel(2);
        viewport.SetViewportSize(new Size(800,600));
        viewport.ApplyFitMode(FitMode.Fit);
        Assert.Equal(new Rectangle(0,75,800,450), viewport.CalculateRenderBounds(new Size(800,600)));
        Assert.Equal(new Point(960,540), viewport.TranslateViewportToSource(new Point(400,225)));
    }

    private static AnnotationStroke Stroke() => new() {
        ParticipantId="p", Tool=AnnotationTool.Line, Color=AnnotationColor.Red, StrokeWidth=6,
        Points=new[] {new Point(10,20),new Point(200,20)}
    };

    private static Task OnSta(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
