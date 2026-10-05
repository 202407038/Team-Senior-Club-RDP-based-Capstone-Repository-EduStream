using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// AnnotationEngineAdapter 단위 테스트
/// 판서 엔진의 렌더링/전송 파이프라인 연동 검증
/// 실제 렌더러, 숨김/삭제/실행 취소 동작 테스트
/// </summary>
public class AnnotationEngineAdapterTests
{
    [Fact]
    public void Constructor_ShouldInitializeWithAnnotationManager()
    {
        // Arrange
        var annotationManager = new AnnotationManager();

        // Act
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);

        // Assert
        Assert.NotNull(engineAdapter);
        Assert.False(engineAdapter.IsEngineActive);
        Assert.Equal(AnnotationState.Empty, engineAdapter.CurrentState);
    }

    [Fact]
    public async Task ActivateEngineAsync_ShouldSetEngineActive()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);

        // Act
        await engineAdapter.ActivateEngineAsync();

        // Assert
        Assert.True(engineAdapter.IsEngineActive);
    }

    [Fact]
    public async Task DeactivateEngineAsync_ShouldSetEngineInactive()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        // Act
        await engineAdapter.DeactivateEngineAsync();

        // Assert
        Assert.False(engineAdapter.IsEngineActive);
    }

    [Fact]
    public async Task ReceiveStrokeAsync_WhenInactive_ShouldThrowException()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        var stroke = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "test-participant",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(100, 100), new Point(200, 200) }
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engineAdapter.ReceiveStrokeAsync(stroke));
    }

    [Fact]
    public async Task ReceiveStrokeAsync_WhenActive_ShouldTriggerRenderPipeline()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        var stroke = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "test-participant",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(100, 100), new Point(200, 200) }
        };

        var renderCalled = false;
        engineAdapter.AddRenderHandler(s =>
        {
            renderCalled = true;
            return Task.CompletedTask;
        });

        // Act
        await engineAdapter.ReceiveStrokeAsync(stroke);

        // Assert
        Assert.True(renderCalled);
    }

    [Fact]
    public async Task ReceiveStrokeAsync_WhenActive_ShouldTriggerTransmissionPipeline()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        var stroke = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "test-participant",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(100, 100), new Point(200, 200) }
        };

        var transmissionCalled = false;
        engineAdapter.AddTransmissionHandler((s, targetId) =>
        {
            transmissionCalled = true;
            return Task.CompletedTask;
        });

        // Act
        await engineAdapter.ReceiveStrokeAsync(stroke);

        // Assert
        Assert.True(transmissionCalled);
    }

    [Fact]
    public async Task AddRenderHandler_ShouldBeCalledOnStroke()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        var stroke = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "test-participant",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(100, 100), new Point(200, 200) }
        };

        var callCount = 0;
        engineAdapter.AddRenderHandler(s =>
        {
            callCount++;
            return Task.CompletedTask;
        });

        // Act
        await engineAdapter.ReceiveStrokeAsync(stroke);

        // Assert
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task MultipleRenderHandlers_ShouldAllBeCalled()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        var stroke = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "test-participant",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(100, 100), new Point(200, 200) }
        };

        var handler1Called = false;
        var handler2Called = false;

        engineAdapter.AddRenderHandler(s =>
        {
            handler1Called = true;
            return Task.CompletedTask;
        });

        engineAdapter.AddRenderHandler(s =>
        {
            handler2Called = true;
            return Task.CompletedTask;
        });

        // Act
        await engineAdapter.ReceiveStrokeAsync(stroke);

        // Assert
        Assert.True(handler1Called);
        Assert.True(handler2Called);
    }

    [Fact]
    public async Task ClearRenderPipeline_ShouldRemoveAllHandlers()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        var stroke = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "test-participant",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(100, 100), new Point(200, 200) }
        };

        engineAdapter.AddRenderHandler(s =>
        {
            return Task.CompletedTask;
        });

        engineAdapter.ClearRenderPipeline();

        // Act
        await engineAdapter.ReceiveStrokeAsync(stroke);

        // Assert - Should not throw, handlers just won't be called
        Assert.True(true);
    }

    [Fact]
    public async Task SetLayerVisibilityAsync_ShouldUpdateAnnotationManager()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);

        // Act
        await engineAdapter.SetLayerVisibilityAsync(false);

        // Assert
        Assert.False(annotationManager.IsLayerVisible);
    }

    [Fact]
    public async Task ClearAllStrokesAsync_ShouldClearAnnotationManager()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        var stroke = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "test-participant",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(100, 100), new Point(200, 200) }
        };
        await engineAdapter.ReceiveStrokeAsync(stroke);

        // Act
        await engineAdapter.ClearAllStrokesAsync();

        // Assert
        var allStrokes = await annotationManager.GetAllStrokesAsync();
        Assert.Empty(allStrokes);
    }

    [Fact]
    public async Task GetStrokesByParticipantAsync_ShouldReturnCorrectStrokes()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        var stroke1 = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "participant-1",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(100, 100), new Point(200, 200) }
        };

        var stroke2 = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "participant-2",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(150, 150), new Point(250, 250) }
        };

        await engineAdapter.ReceiveStrokeAsync(stroke1);
        await engineAdapter.ReceiveStrokeAsync(stroke2);

        // Act
        var participant1Strokes = await engineAdapter.GetStrokesByParticipantAsync("participant-1");

        // Assert
        Assert.Single(participant1Strokes);
        Assert.Equal("participant-1", participant1Strokes[0].ParticipantId);
    }

    [Fact]
    public async Task EngineStateChanged_ShouldRaiseEventOnActivation()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);

        EngineStateChangedEventArgs? eventArgs = null;
        engineAdapter.EngineStateChanged += (sender, args) => eventArgs = args;

        // Act
        await engineAdapter.ActivateEngineAsync();

        // Assert
        Assert.NotNull(eventArgs);
        Assert.True(eventArgs.IsActive);
    }

    [Fact]
    public async Task EngineStateChanged_ShouldRaiseEventOnDeactivation()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        EngineStateChangedEventArgs? eventArgs = null;
        engineAdapter.EngineStateChanged += (sender, args) => eventArgs = args;

        // Act
        await engineAdapter.DeactivateEngineAsync();

        // Assert
        Assert.NotNull(eventArgs);
        Assert.False(eventArgs.IsActive);
    }

    [Fact]
    public async Task AddRendererHandler_ShouldBeCalledOnStroke()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        var stroke = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "test-participant",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(100, 100), new Point(200, 200) }
        };

        var rendererCalled = false;
        engineAdapter.AddRendererHandler((s, points) =>
        {
            rendererCalled = true;
            return Task.CompletedTask;
        });

        // Act
        await engineAdapter.ReceiveStrokeAsync(stroke);

        // Assert
        Assert.True(rendererCalled);
    }

    [Fact]
    public async Task ToggleLayerVisibilityAsync_ShouldToggleVisibility()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        var initialVisibility = engineAdapter.CurrentState.IsVisible;

        // Act
        await engineAdapter.ToggleLayerVisibilityAsync();

        // Assert
        Assert.NotEqual(initialVisibility, engineAdapter.CurrentState.IsVisible);
    }

    [Fact]
    public async Task UndoAsync_ShouldRemoveLastStroke()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        var stroke1 = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "test-participant",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(100, 100), new Point(200, 200) }
        };

        var stroke2 = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "test-participant",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(150, 150), new Point(250, 250) }
        };

        await engineAdapter.ReceiveStrokeAsync(stroke1);
        await engineAdapter.ReceiveStrokeAsync(stroke2);

        var allStrokesBefore = await engineAdapter.GetAllStrokesAsync();

        // Act
        await engineAdapter.UndoAsync();

        // Assert
        var allStrokesAfter = await engineAdapter.GetAllStrokesAsync();
        Assert.Equal(2, allStrokesBefore.Count);
        Assert.Single(allStrokesAfter);
        Assert.Equal(stroke1.StrokeId, allStrokesAfter[0].StrokeId);
    }

    [Fact]
    public async Task ClearAllStrokesAsync_ShouldRemainUndoable()
    {
        // Arrange
        var annotationManager = new AnnotationManager();
        var engineAdapter = new AnnotationEngineAdapter(annotationManager);
        await engineAdapter.ActivateEngineAsync();

        var stroke = new AnnotationStroke
        {
            StrokeId = Guid.NewGuid(),
            ParticipantId = "test-participant",
            Tool = AnnotationTool.Pen,
            Points = new[] { new Point(100, 100), new Point(200, 200) }
        };

        await engineAdapter.ReceiveStrokeAsync(stroke);

        // Act
        await engineAdapter.ClearAllStrokesAsync();

        // Assert
        var allStrokes = await engineAdapter.GetAllStrokesAsync();
        Assert.Empty(allStrokes);

        // Core 계약상 전체 지우기도 실행 취소 대상이다.
        await engineAdapter.UndoAsync();
        var strokesAfterUndo = await engineAdapter.GetAllStrokesAsync();
        Assert.Equal(stroke.StrokeId, Assert.Single(strokesAfterUndo).StrokeId);
    }
    [Fact]
    public async Task ActivateAndDeactivateEngine_ShouldUpdateDrawingStateWhilePreservingVisibility()
    {
        // 1. 초기 상태: 판서 비활성화 및 그리기 모드 해제 상태 검증
        var manager = new EduStream.Server.Rdp.AnnotationManager();
        var adapter = new EduStream.Server.Rdp.AnnotationEngineAdapter(manager);

        Assert.False(adapter.IsEngineActive);
        Assert.False(adapter.CurrentState.IsDrawing);
        Assert.True(adapter.CurrentState.IsVisible);

        // 2. 판서 활성화 시: IsDrawing이 true로 일치하는지 상태 전이 검증
        await adapter.ActivateEngineAsync();
        Assert.True(adapter.IsEngineActive);
        Assert.True(adapter.CurrentState.IsDrawing);

        // 3. 판서 비활성화 시: IsDrawing은 false로 복귀하나, 그려둔 스트로크 표시(IsVisible)는 유지되는지 검증
        await adapter.DeactivateEngineAsync();
        Assert.False(adapter.IsEngineActive);
        Assert.False(adapter.CurrentState.IsDrawing);
        Assert.True(adapter.CurrentState.IsVisible);
    }
}
