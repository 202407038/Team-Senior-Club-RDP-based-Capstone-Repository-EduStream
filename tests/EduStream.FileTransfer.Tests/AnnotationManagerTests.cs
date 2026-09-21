using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

public class AnnotationManagerTests
{
    private readonly AnnotationManager _manager = new();

    [Fact]
    public void IsLayerVisible_기본값_true()
    {
        // Assert
        Assert.True(_manager.IsLayerVisible);
    }

    [Fact]
    public async Task SetLayerVisibilityAsync_가시성_변경()
    {
        // Act
        await _manager.SetLayerVisibilityAsync(false);

        // Assert
        Assert.False(_manager.IsLayerVisible);

        await _manager.SetLayerVisibilityAsync(true);
        Assert.True(_manager.IsLayerVisible);
    }

    [Fact]
    public async Task AddStrokeAsync_스트로크_추가_성공()
    {
        // Arrange
        var stroke = new AnnotationStroke
        {
            ParticipantId = "student1",
            Tool = AnnotationTool.Pen,
            Color = AnnotationColor.Red,
            StrokeWidth = 2,
            Points = new[] { new Point(10, 10), new Point(20, 20) }
        };

        // Act
        await _manager.AddStrokeAsync(stroke);

        // Assert
        var allStrokes = await _manager.GetAllStrokesAsync();
        Assert.Single(allStrokes);
        Assert.Equal(stroke.StrokeId, allStrokes[0].StrokeId);
    }

    [Fact]
    public async Task UpdateStrokeAsync_스트로크_수정_성공()
    {
        // Arrange
        var stroke = new AnnotationStroke
        {
            ParticipantId = "student1",
            Tool = AnnotationTool.Pen,
            Color = AnnotationColor.Red,
            StrokeWidth = 2,
            Points = new[] { new Point(10, 10), new Point(20, 20) }
        };
        await _manager.AddStrokeAsync(stroke);

        var updatedStroke = new AnnotationStroke
        {
            StrokeId = stroke.StrokeId,
            ParticipantId = "student1",
            Tool = AnnotationTool.Pen,
            Color = AnnotationColor.Blue,
            StrokeWidth = 4,
            Points = new[] { new Point(30, 30), new Point(40, 40) }
        };

        // Act
        await _manager.UpdateStrokeAsync(stroke.StrokeId, updatedStroke);

        // Assert
        var allStrokes = await _manager.GetAllStrokesAsync();
        Assert.Equal(AnnotationColor.Blue, allStrokes[0].Color);
        Assert.Equal(4, allStrokes[0].StrokeWidth);
    }

    [Fact]
    public async Task DeleteStrokeAsync_스트로크_삭제_성공()
    {
        // Arrange
        var stroke = new AnnotationStroke
        {
            ParticipantId = "student1",
            Tool = AnnotationTool.Pen,
            Color = AnnotationColor.Red,
            StrokeWidth = 2,
            Points = new[] { new Point(10, 10), new Point(20, 20) }
        };
        await _manager.AddStrokeAsync(stroke);

        // Act
        await _manager.DeleteStrokeAsync(stroke.StrokeId);

        // Assert
        var allStrokes = await _manager.GetAllStrokesAsync();
        Assert.Empty(allStrokes);
    }

    [Fact]
    public async Task ClearAllStrokesAsync_모든_스트로크_삭제()
    {
        // Arrange
        await _manager.AddStrokeAsync(new AnnotationStroke
        {
            ParticipantId = "student1",
            Tool = AnnotationTool.Pen,
            Color = AnnotationColor.Red,
            StrokeWidth = 2,
            Points = new[] { new Point(10, 10), new Point(20, 20) }
        });
        await _manager.AddStrokeAsync(new AnnotationStroke
        {
            ParticipantId = "student2",
            Tool = AnnotationTool.Pen,
            Color = AnnotationColor.Blue,
            StrokeWidth = 2,
            Points = new[] { new Point(30, 30), new Point(40, 40) }
        });

        // Act
        await _manager.ClearAllStrokesAsync();

        // Assert
        var allStrokes = await _manager.GetAllStrokesAsync();
        Assert.Empty(allStrokes);
    }

    [Fact]
    public async Task GetStrokesByParticipantAsync_특정_참가자_스트로크_조회()
    {
        // Arrange
        await _manager.AddStrokeAsync(new AnnotationStroke
        {
            ParticipantId = "student1",
            Tool = AnnotationTool.Pen,
            Color = AnnotationColor.Red,
            StrokeWidth = 2,
            Points = new[] { new Point(10, 10), new Point(20, 20) }
        });
        await _manager.AddStrokeAsync(new AnnotationStroke
        {
            ParticipantId = "student2",
            Tool = AnnotationTool.Pen,
            Color = AnnotationColor.Blue,
            StrokeWidth = 2,
            Points = new[] { new Point(30, 30), new Point(40, 40) }
        });
        await _manager.AddStrokeAsync(new AnnotationStroke
        {
            ParticipantId = "student1",
            Tool = AnnotationTool.Highlighter,
            Color = AnnotationColor.Yellow,
            StrokeWidth = 4,
            Points = new[] { new Point(50, 50), new Point(60, 60) }
        });

        // Act
        var student1Strokes = await _manager.GetStrokesByParticipantAsync("student1");
        var student2Strokes = await _manager.GetStrokesByParticipantAsync("student2");

        // Assert
        Assert.Equal(2, student1Strokes.Count);
        Assert.Single(student2Strokes);
        Assert.All(student1Strokes, s => Assert.Equal("student1", s.ParticipantId));
        Assert.All(student2Strokes, s => Assert.Equal("student2", s.ParticipantId));
    }

    [Fact]
    public async Task GetAllStrokesAsync_모든_스트로크_조회()
    {
        // Arrange
        await _manager.AddStrokeAsync(new AnnotationStroke
        {
            ParticipantId = "student1",
            Tool = AnnotationTool.Pen,
            Color = AnnotationColor.Red,
            StrokeWidth = 2,
            Points = new[] { new Point(10, 10), new Point(20, 20) }
        });
        await _manager.AddStrokeAsync(new AnnotationStroke
        {
            ParticipantId = "student2",
            Tool = AnnotationTool.Pen,
            Color = AnnotationColor.Blue,
            StrokeWidth = 2,
            Points = new[] { new Point(30, 30), new Point(40, 40) }
        });

        // Act
        var allStrokes = await _manager.GetAllStrokesAsync();

        // Assert
        Assert.Equal(2, allStrokes.Count);
    }
}
