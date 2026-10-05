using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.Brush;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace EduStream.Server.Rdp;

/// <summary>
/// 3번 판서 출력 레이어. 교수자 로컬 렌더와 학생 화면 오버레이가 같은 클래스를 쓴다.
/// 5번은 이 창/캔버스의 위치·부모만 정하고, 그리기/숨김/삭제는 엔진 스냅샷으로 통째로 교체한다.
/// </summary>
public sealed class AnnotationOverlayLayer : IDisposable
{
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private bool _disposed;

    public Canvas Canvas { get; }
    public Window? HostWindow { get; private set; }
    public int ShapeCount => Canvas.Children.Count;

    /// <summary>인프로세스 캔버스만 필요할 때 (교수자 로컬 레이어, 단위 테스트).</summary>
    public AnnotationOverlayLayer(double width, double height)
    {
        _pixelWidth = Math.Max(1, (int)width);
        _pixelHeight = Math.Max(1, (int)height);
        Canvas = new Canvas { Width = width, Height = height, Background = System.Windows.Media.Brushes.Transparent };
        Canvas.Measure(new System.Windows.Size(width, height));
        Canvas.Arrange(new Rect(0, 0, width, height));
    }

    /// <summary>
    /// 학생 데스크톱 위에 올릴 투명 최상위 창. WDS 가 이 창을 공유 화면에 담는다.
    /// 5번이 배치를 바꾸려면 이 창의 Left/Top/Width/Height 만 조정하면 된다.
    /// </summary>
    public static AnnotationOverlayLayer CreateDesktopOverlay(Rect bounds, string title)
    {
        var layer = new AnnotationOverlayLayer(bounds.Width, bounds.Height);
        layer.HostWindow = new Window
        {
            Title = title,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Content = layer.Canvas
        };
        layer.HostWindow.Show();
        return layer;
    }

    /// <summary>엔진 렌더/전송 핸들러에 그대로 넘길 수 있는 그리기 함수.</summary>
    public Task ApplyStrokeAsync(AnnotationStroke stroke)
    {
        Draw(stroke);
        return Task.CompletedTask;
    }

    /// <summary>엔진 레이어 스냅샷으로 출력을 통째로 교체한다.</summary>
    public Task ReplaceAllAsync(AnnotationLayerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Canvas.Children.Clear();
        foreach (var stroke in snapshot.VisibleStrokes) Draw(stroke);
        Canvas.UpdateLayout();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 엔진에 로컬 렌더 + 레이어 동기화를 붙인다.
    /// 전송(학생 쪽)은 <see cref="BindTransmission"/> 으로 따로 붙인다.
    /// </summary>
    public void BindLocalRenderer(AnnotationEngineAdapter engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.AddRendererHandler((stroke, _) => ApplyStrokeAsync(stroke));
        engine.AddLayerSyncHandler(ReplaceAllAsync);
    }

    /// <summary>
    /// 전송 파이프라인: 스트로크를 JSON 으로 만든 뒤 이 레이어에 그리고, 호출자에게 전달 페이로드를 넘긴다.
    /// 실제 네트워크 전송은 1·2번 영역. 이 메서드는 페이로드와 로컬 반영만 담당한다.
    /// </summary>
    public void BindTransmission(AnnotationEngineAdapter engine, Action<string, string>? onPayloadReady = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.AddTransmissionHandler((stroke, target) =>
        {
            var json = AnnotationStrokeWire.ToJson(stroke);
            onPayloadReady?.Invoke(json, target);
            return ApplyStrokeAsync(stroke);
        });
        engine.AddLayerSyncHandler(ReplaceAllAsync);
    }

    /// <summary>2번이 전달한 JSON 스트로크를 학생 레이어에 반영할 때 사용.</summary>
    public Task ReceiveRemoteStrokeJsonAsync(string json)
        => ApplyStrokeAsync(AnnotationStrokeWire.FromJson(json));

    public void Draw(AnnotationStroke stroke)
    {
        var points = stroke.Points;
        if (points.Count < 2) throw new InvalidOperationException("선/도형은 점이 2개 이상 필요합니다.");

        var c = stroke.Color;
        var brush = new SolidColorBrush(WpfColor.FromArgb(c.A, c.R, c.G, c.B));
        var first = points[0];
        var last = points[points.Count - 1];

        UIElement element = stroke.Tool switch
        {
            AnnotationTool.Line => new Line
            {
                X1 = first.X, Y1 = first.Y, X2 = last.X, Y2 = last.Y,
                Stroke = brush, StrokeThickness = stroke.StrokeWidth
            },
            AnnotationTool.Rectangle => CreateRectangle(first, last, brush, stroke.StrokeWidth),
            AnnotationTool.Pen => CreatePolyline(points, brush, stroke.StrokeWidth),
            _ => throw new NotSupportedException($"오버레이가 지원하지 않는 도구입니다: {stroke.Tool}")
        };

        Canvas.Children.Add(element);
        Canvas.UpdateLayout();
    }

    public int CountPixels(Func<byte, byte, byte, bool> match)
    {
        var bitmap = new RenderTargetBitmap(_pixelWidth, _pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Canvas);

        int stride = _pixelWidth * 4;
        var buffer = new byte[stride * _pixelHeight];
        bitmap.CopyPixels(buffer, stride, 0);

        int count = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            if (buffer[i + 3] == 255 && match(buffer[i + 2], buffer[i + 1], buffer[i]))
                count++;
        }
        return count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (HostWindow != null)
            {
                HostWindow.Content = null;
                HostWindow.Close();
            }
        }
        catch { /* 이미 닫힘 */ }
    }

    private static WpfRectangle CreateRectangle(System.Drawing.Point first, System.Drawing.Point last, WpfBrush brush, int width)
    {
        var rectangle = new WpfRectangle
        {
            Width = Math.Abs(last.X - first.X),
            Height = Math.Abs(last.Y - first.Y),
            Stroke = brush,
            StrokeThickness = width
        };
        Canvas.SetLeft(rectangle, Math.Min(first.X, last.X));
        Canvas.SetTop(rectangle, Math.Min(first.Y, last.Y));
        return rectangle;
    }

    private static Polyline CreatePolyline(System.Collections.Generic.IReadOnlyList<System.Drawing.Point> points, WpfBrush brush, int width)
    {
        var polyline = new Polyline { Stroke = brush, StrokeThickness = width };
        foreach (var p in points) polyline.Points.Add(new System.Windows.Point(p.X, p.Y));
        return polyline;
    }
}

/// <summary>
/// 판서 스트로크의 전송용 JSON. 1·2번이 메시지를 실어 나를 때 이 형식을 쓰면 된다.
/// 네트워크 전송 자체는 이 클래스가 하지 않는다.
/// </summary>
public static class AnnotationStrokeWire
{
    public static string ToJson(AnnotationStroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        var dto = new StrokeDto
        {
            StrokeId = stroke.StrokeId,
            ParticipantId = stroke.ParticipantId,
            CreatedAt = stroke.CreatedAt,
            Tool = stroke.Tool.ToString(),
            R = stroke.Color.R,
            G = stroke.Color.G,
            B = stroke.Color.B,
            A = stroke.Color.A,
            StrokeWidth = stroke.StrokeWidth,
            IsVisible = stroke.IsVisible,
            Points = stroke.Points.Select(p => new[] { p.X, p.Y }).ToArray()
        };
        return System.Text.Json.JsonSerializer.Serialize(dto);
    }

    public static AnnotationStroke FromJson(string json)
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<StrokeDto>(json)
            ?? throw new ArgumentException("판서 스트로크 JSON 을 읽지 못했습니다.", nameof(json));
        if (!Enum.TryParse<AnnotationTool>(dto.Tool, out var tool))
            throw new ArgumentException($"알 수 없는 판서 도구입니다: {dto.Tool}");
        return new AnnotationStroke
        {
            StrokeId = dto.StrokeId,
            ParticipantId = dto.ParticipantId,
            CreatedAt = dto.CreatedAt,
            Tool = tool,
            Color = new AnnotationColor(dto.R, dto.G, dto.B, dto.A),
            StrokeWidth = dto.StrokeWidth,
            IsVisible = dto.IsVisible,
            Points = dto.Points.Select(p => new System.Drawing.Point(p[0], p[1])).ToArray()
        };
    }

    private sealed class StrokeDto
    {
        public Guid StrokeId { get; set; }
        public string ParticipantId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public string Tool { get; set; } = string.Empty;
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public byte A { get; set; }
        public int StrokeWidth { get; set; }
        public bool IsVisible { get; set; } = true;
        public int[][] Points { get; set; } = Array.Empty<int[]>();
    }
}
