using System.Drawing;
using System.Windows.Threading;
using EduStream.Core.Collaboration;
using EduStream.Server.Rdp;
using CoreTool = EduStream.Core.Collaboration.AnnotationTool;

namespace EduStream.FileTransfer.Tests;

/// <summary>1번 codec 검사와 실제 3번 엔진 출력/수신 레이어의 계약 호환성을 검증한다. 엔진은 수정하지 않는다.</summary>
public sealed class CoreAnnotationEngineCompatibilityTests
{
    [Theory]
    [InlineData(CoreTool.Pen)]
    [InlineData(CoreTool.Line)]
    [InlineData(CoreTool.Rectangle)]
    [InlineData(CoreTool.Ellipse)]
    [InlineData(CoreTool.Eraser)]
    public Task EveryUiTool_EngineOutputPassesCodecAndRendersAtReceiver(CoreTool tool) => OnSta(async () =>
    {
        var engine = new AnnotationEngineAdapter(new AnnotationManager());
        var controller = new AnnotationEngineController(engine);
        using var source = new AnnotationOverlayLayer(120, 100);
        using var receiver = new AnnotationOverlayLayer(120, 100);
        var payloads = new List<string>();
        source.BindTransmission(engine, (json, _) => payloads.Add(json));
        controller.SetDrawing(true);
        if (tool == CoreTool.Eraser)
        {
            controller.SelectTool(CoreTool.Line, 0xFFFF0000, 4);
            await controller.SubmitStrokeAsync("professor", new[] { new Point(10,10), new Point(90,60) });
            await Deliver(payloads, receiver);
        }
        controller.SelectTool(tool, 0xFFFF0000, 4);
        await controller.SubmitStrokeAsync("professor", new[] { new Point(10,10), new Point(90,60) });
        Assert.NotEmpty(payloads);
        await Deliver(payloads, receiver);
        Assert.Equal(tool == CoreTool.Eraser ? 0 : 1, receiver.ShapeCount);
        Assert.Equal(source.ShapeCount, receiver.ShapeCount);
        if (tool != CoreTool.Eraser)
            Assert.True(receiver.CountPixels((r,g,b) => r > 200 && g < 60 && b < 60) > 0);
        controller.SetDrawing(false);
    });

    [Fact]
    public Task CircleInLayerSnapshot_HideRestoreClearUndoKeepReceiverInSync() => OnSta(async () =>
    {
        var engine = new AnnotationEngineAdapter(new AnnotationManager());
        var controller = new AnnotationEngineController(engine);
        using var source = new AnnotationOverlayLayer(120,100);
        using var receiver = new AnnotationOverlayLayer(120,100);
        var payloads = new List<string>();
        source.BindTransmission(engine, (json, _) => payloads.Add(json));
        controller.SetDrawing(true);
        controller.SelectTool(CoreTool.Ellipse, 0xFFFF0000, 4);
        await controller.SubmitStrokeAsync("professor", new[] { new Point(10,10), new Point(90,60) });
        await Deliver(payloads, receiver);
        Assert.Equal(1, receiver.ShapeCount);
        controller.ToggleVisibility();
        await Deliver(payloads, receiver);
        Assert.Equal(0, receiver.ShapeCount);
        controller.ToggleVisibility();
        await Deliver(payloads, receiver);
        Assert.Equal(1, receiver.ShapeCount);
        controller.Clear(false);
        await Deliver(payloads, receiver);
        Assert.Equal(0, receiver.ShapeCount);
        controller.Undo();
        await Deliver(payloads, receiver);
        Assert.Equal(1, receiver.ShapeCount);
        Assert.True(receiver.CountPixels((r,g,b) => r > 200 && g < 60 && b < 60) > 0);
        controller.SetDrawing(false);
    });

    private static async Task Deliver(List<string> payloads, AnnotationOverlayLayer receiver)
    {
        foreach (var json in payloads.ToArray())
        {
            var notice = new AnnotationTransportNotice(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, json);
            var bytes = CollaborationMessageCodec.Encode(Guid.NewGuid(), notice);
            var decoded = CollaborationMessageCodec.Decode<AnnotationTransportNotice>(bytes, out _);
            await receiver.ReceiveRemoteStrokeJsonAsync(decoded.PayloadJson);
        }
        payloads.Clear();
    }

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
