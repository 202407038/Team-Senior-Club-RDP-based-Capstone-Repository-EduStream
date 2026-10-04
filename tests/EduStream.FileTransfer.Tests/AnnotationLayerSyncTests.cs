using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 판서 레이어의 숨김 / 다시 표시 / 전체 삭제 / 실행 취소가 "속성 값"이 아니라
/// 렌더러·전송 싱크의 실제 출력 내용으로 반영되는지 검증한다 (피드백 4).
/// 싱크는 자신의 출력 목록을 레이어 스냅샷으로 통째로 교체하는 최소 구현이며,
/// 실제 화면 픽셀 검증은 AdapterDefectRegressionTests 의 WDS E2E 가 맡는다.
/// </summary>
public class AnnotationLayerSyncTests
{
    private sealed class Output
    {
        private readonly List<Guid> _shown = new();
        public IReadOnlyList<Guid> Shown => _shown;
        public int DrawCalls { get; private set; }

        public Task Draw(AnnotationStroke s) { _shown.Add(s.StrokeId); DrawCalls++; return Task.CompletedTask; }

        public Task Replace(AnnotationLayerSnapshot snapshot)
        {
            _shown.Clear();
            _shown.AddRange(snapshot.VisibleStrokes.Select(x => x.StrokeId));
            return Task.CompletedTask;
        }
    }

    private static AnnotationStroke NewStroke(int offset = 0) => new()
    {
        ParticipantId = "prof-1",
        Tool = AnnotationTool.Line,
        Color = AnnotationColor.Red,
        StrokeWidth = 3,
        Points = new[] { new Point(0, offset), new Point(100, offset) },
        CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(offset)
    };

    private static async Task<(AnnotationEngineAdapter Engine, AnnotationManager Manager, Output Local, Output Remote)> CreateAsync()
    {
        var manager = new AnnotationManager();
        var engine = new AnnotationEngineAdapter(manager);
        var local = new Output();
        var remote = new Output();

        engine.AddRendererHandler((s, _) => local.Draw(s));
        engine.AddLayerSyncHandler(local.Replace);
        engine.AddTransmissionHandler((s, _) => remote.Draw(s));
        engine.AddLayerSyncHandler(remote.Replace);
        await engine.ActivateEngineAsync();
        return (engine, manager, local, remote);
    }

    [Fact]
    public async Task Hide_RemovesStrokesFromEveryOutput_ButKeepsThemInTheManager()
    {
        var (engine, manager, local, remote) = await CreateAsync();
        var a = NewStroke(1);
        var b = NewStroke(2);
        await engine.ReceiveStrokeAsync(a);
        await engine.ReceiveStrokeAsync(b);
        Assert.Equal(2, local.Shown.Count);
        Assert.Equal(2, remote.Shown.Count);

        AnnotationLayerSyncedEventArgs? synced = null;
        engine.OnLayerSynced += (_, e) => synced = e;

        await engine.SetLayerVisibilityAsync(false);

        Assert.Empty(local.Shown);
        Assert.Empty(remote.Shown);
        Assert.NotNull(synced);
        Assert.False(synced!.Snapshot.IsVisible);
        Assert.Equal(AnnotationLayerChange.VisibilityChanged, synced.Snapshot.Change);
        Assert.Equal(2, synced.AppliedHandlerCount);
        Assert.Equal(2, (await manager.GetAllStrokesAsync()).Count); // 보존
    }

    [Fact]
    public async Task ShowAgain_RestoresEveryPreservedStroke_InCreationOrder()
    {
        var (engine, _, local, remote) = await CreateAsync();
        var a = NewStroke(1);
        var b = NewStroke(2);
        await engine.ReceiveStrokeAsync(a);
        await engine.ReceiveStrokeAsync(b);

        await engine.SetLayerVisibilityAsync(false);
        await engine.SetLayerVisibilityAsync(true);

        Assert.Equal(new[] { a.StrokeId, b.StrokeId }, local.Shown);
        Assert.Equal(new[] { a.StrokeId, b.StrokeId }, remote.Shown);
    }

    [Fact]
    public async Task StrokeDrawnWhileHidden_IsNotRenderedOrTransmitted_UntilShownAgain()
    {
        var (engine, manager, local, remote) = await CreateAsync();
        int rendered = 0, dispatched = 0;
        engine.OnStrokeRendered += (_, _) => rendered++;
        engine.OnStrokeDispatched += (_, _) => dispatched++;

        await engine.SetLayerVisibilityAsync(false);
        var hiddenStroke = NewStroke(5);
        await engine.ReceiveStrokeAsync(hiddenStroke);

        Assert.Equal(0, local.DrawCalls);
        Assert.Equal(0, remote.DrawCalls);
        Assert.Equal(0, rendered);   // 완료 이벤트도 발생하지 않는다 (실제로 그리지 않았으므로)
        Assert.Equal(0, dispatched);
        Assert.Single(await manager.GetAllStrokesAsync());

        await engine.SetLayerVisibilityAsync(true);

        Assert.Equal(new[] { hiddenStroke.StrokeId }, local.Shown);
        Assert.Equal(new[] { hiddenStroke.StrokeId }, remote.Shown);
    }

    [Fact]
    public async Task Clear_EmptiesEveryOutput_AndShowingAgainDoesNotRevive()
    {
        var (engine, manager, local, remote) = await CreateAsync();
        await engine.ReceiveStrokeAsync(NewStroke(1));
        await engine.ReceiveStrokeAsync(NewStroke(2));

        AnnotationLayerSyncedEventArgs? synced = null;
        engine.OnLayerSynced += (_, e) => synced = e;

        await engine.ClearAllStrokesAsync();

        Assert.Empty(local.Shown);
        Assert.Empty(remote.Shown);
        Assert.Equal(AnnotationLayerChange.Cleared, synced!.Snapshot.Change);
        Assert.Empty(await manager.GetAllStrokesAsync());

        await engine.SetLayerVisibilityAsync(false);
        await engine.SetLayerVisibilityAsync(true);

        Assert.Empty(local.Shown);
        Assert.Empty(remote.Shown);
    }

    [Fact]
    public async Task Undo_RemovesOnlyTheLastStroke_FromEveryOutput()
    {
        var (engine, _, local, remote) = await CreateAsync();
        var a = NewStroke(1);
        var b = NewStroke(2);
        await engine.ReceiveStrokeAsync(a);
        await engine.ReceiveStrokeAsync(b);

        await engine.UndoAsync();

        Assert.Equal(new[] { a.StrokeId }, local.Shown);
        Assert.Equal(new[] { a.StrokeId }, remote.Shown);
    }

    [Fact]
    public async Task LayerSyncedEvent_IsNotRaised_WithoutSinks_OrWhenEverySinkFails()
    {
        var manager = new AnnotationManager();
        var engine = new AnnotationEngineAdapter(manager);
        int events = 0;
        engine.OnLayerSynced += (_, _) => events++;

        await engine.SetLayerVisibilityAsync(false); // 싱크 없음
        Assert.Equal(0, events);

        engine.AddLayerSyncHandler(_ => throw new InvalidOperationException("sink down"));
        await engine.SetLayerVisibilityAsync(true);  // 모든 싱크 실패
        Assert.Equal(0, events);

        engine.AddLayerSyncHandler(_ => Task.CompletedTask);
        await engine.SetLayerVisibilityAsync(false); // 하나라도 성공하면 발생
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task ClearLayerSyncHandlers_StopsFurtherPushes()
    {
        var (engine, _, local, _) = await CreateAsync();
        await engine.ReceiveStrokeAsync(NewStroke(1));
        engine.ClearLayerSyncHandlers();

        await engine.SetLayerVisibilityAsync(false);

        Assert.Single(local.Shown); // 싱크가 없으므로 더 이상 교체되지 않는다
    }
}
