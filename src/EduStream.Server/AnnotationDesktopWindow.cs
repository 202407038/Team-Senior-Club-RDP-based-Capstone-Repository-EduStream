using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EduStream.Server.Rdp;
using EduStream.Server.ViewModels;
using MonitorInfo = EduStream.ShareHost.MonitorInfo;
using Point = System.Drawing.Point;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace EduStream.Server;

/// <summary>선택 모니터 위 판서. WDS가 실제 화면과 판서를 함께 전송하므로 수신 화면에 두 번 그리지 않는다.</summary>
public sealed class AnnotationDesktopWindow : IDisposable
{
    private readonly AnnotationOverlayLayer _layer;
    private readonly AnnotationEngineAdapter _engine = new(new AnnotationManager());
    private readonly AnnotationEngineController _controller;
    private readonly AnnotationToolsViewModel _tools;
    private readonly Window _overlay;
    private readonly Window _toolbar;
    private readonly MonitorInfo _monitor;
    private readonly List<Point> _points = new();
    private readonly TextBlock _status = new() { Foreground = Brushes.White, Margin = new Thickness(6) };
    private readonly Button _drawingButton = new() { Content = "그림 유지하고 OFF", Margin = new Thickness(3) };
    private readonly StackPanel _toolsPanel = new();
    private bool _drawing;
    private bool _busy;
    private bool _disposed;
    public event Action<bool>? DrawingChanged;

    public AnnotationDesktopWindow(MonitorInfo monitor, AnnotationToolsViewModel tools)
    {
        _monitor = monitor; _tools = tools;
        _layer = new AnnotationOverlayLayer(monitor.Width, monitor.Height);
        _layer.BindLocalRenderer(_engine);
        _controller = new AnnotationEngineController(_engine);
        _overlay = new Window
        {
            Title = "EduStream 판서 레이어", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Viewbox { Stretch = Stretch.Fill, Child = _layer.Canvas }
        };
        _toolbar = new Window
        {
            Title = "EduStream 판서 도구", WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Topmost = true,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 38)),
            WindowStartupLocation = WindowStartupLocation.Manual, Content = _toolsPanel
        };
        var selectors = new WrapPanel();
        ComboBox Select(IEnumerable<string> items, string selected, Action<string> apply)
        {
            var box = new ComboBox { ItemsSource = items, SelectedItem = selected, MinWidth = 65, Margin = new Thickness(3) };
            box.SelectionChanged += (_, _) => { if (box.SelectedItem is string value) apply(value); };
            selectors.Children.Add(box); return box;
        }
        Select(tools.Tools, tools.Tool, v => tools.Tool = v);
        Select(tools.Colors, tools.Color, v => tools.Color = v);
        Select(tools.Placements, tools.Placement, v => tools.Placement = v);
        var width = new Slider { Minimum = 1, Maximum = 20, Value = tools.StrokeWidth, Width = 80, Margin = new Thickness(6), ToolTip = "선 굵기" };
        width.ValueChanged += (_, _) => tools.StrokeWidth = width.Value;
        selectors.Children.Add(width);
        _toolsPanel.Children.Add(selectors);
        var actions = new WrapPanel();
        void Add(string label, Func<Task> action)
        {
            var button = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(6) };
            button.Click += async (_, _) => await RunAsync(action);
            actions.Children.Add(button);
        }
        _drawingButton.Click += (_, _) => SetDrawing(!_drawing);
        actions.Children.Add(_drawingButton);
        Add("숨김 / 표시", () => _engine.ToggleLayerVisibilityAsync());
        Add("실행 취소", () => _engine.UndoAsync());
        Add("전체 지우기", () => _engine.ClearAllStrokesAsync());
        Add("지우고 OFF", async () => { await _engine.ClearAllStrokesAsync(); SetDrawing(false); });
        _toolsPanel.Children.Add(actions); _toolsPanel.Children.Add(_status);
        _toolbar.Closing += (_, e) => { if (!_disposed) { e.Cancel = true; SetDrawing(false); } };
        _overlay.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SetDrawing(false); e.Handled = true; } };
        _layer.Canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (!_drawing || _busy) return;
            _points.Clear(); AddPoint(e.GetPosition(_layer.Canvas)); _layer.Canvas.CaptureMouse(); e.Handled = true;
        };
        _layer.Canvas.MouseMove += (_, e) =>
        {
            if (_layer.Canvas.IsMouseCaptured && _points.Count < 4095) AddPoint(e.GetPosition(_layer.Canvas));
        };
        _layer.Canvas.MouseLeftButtonUp += async (_, e) =>
        {
            if (!_layer.Canvas.IsMouseCaptured) return;
            AddPoint(e.GetPosition(_layer.Canvas)); _layer.Canvas.ReleaseMouseCapture();
            var points = _points.ToArray(); _points.Clear();
            await RunAsync(async () =>
            {
                var tool = tools.Tool switch { "직선" => EduStream.Core.Collaboration.AnnotationTool.Line,
                    "사각형" => EduStream.Core.Collaboration.AnnotationTool.Rectangle, "타원" => EduStream.Core.Collaboration.AnnotationTool.Ellipse,
                    "지우개" => EduStream.Core.Collaboration.AnnotationTool.Eraser, _ => EduStream.Core.Collaboration.AnnotationTool.Pen };
                uint color = tools.Color switch { "빨강" => 0xFFFF4040, "파랑" => 0xFF4080FF, "초록" => 0xFF40CC60,
                    "검정" => 0xFF000000, "흰색" => 0xFFFFFFFF, _ => 0xFFFFFF00 };
                _controller.SelectTool(tool, color, tools.StrokeWidth);
                await _controller.SubmitStrokeAsync("professor", points);
            });
        };
        _tools.PropertyChanged += ToolsChanged;
        _overlay.Show();
        // Owner는 소유 창의 HWND가 생성된 뒤 설정한다. 판서층을 클릭해도 도구창이 그 아래로 내려가지 않는다.
        _toolbar.Owner = _overlay;
        var hwnd = new WindowInteropHelper(_overlay).Handle;
        SetWindowPos(hwnd, new IntPtr(-1), monitor.Left, monitor.Top, monitor.Width, monitor.Height, 0x0010);
        _toolbar.Show(); PlaceToolbar(); SetDrawing(true);
    }

    private void AddPoint(System.Windows.Point p) => _points.Add(new Point(
        (int)Math.Clamp(p.X, 0, _monitor.Width - 1), (int)Math.Clamp(p.Y, 0, _monitor.Height - 1)));
    private void ToolsChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(AnnotationToolsViewModel.Placement)) PlaceToolbar(); }
    private void PlaceToolbar()
    {
        _toolbar.UpdateLayout();
        var source = PresentationSource.FromVisual(_toolbar);
        var scale = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var width = (int)(_toolbar.ActualWidth * scale.M11); var height = (int)(_toolbar.ActualHeight * scale.M22);
        var x = _monitor.Left + (_monitor.Width - width) / 2;
        var y = _monitor.Top + _monitor.Height - height - 24;
        if (_tools.Placement == "상단") y = _monitor.Top + 24;
        if (_tools.Placement == "왼쪽") { x = _monitor.Left + 16; y = _monitor.Top + (_monitor.Height - height) / 2; }
        if (_tools.Placement == "오른쪽") { x = _monitor.Left + _monitor.Width - width - 16; y = _monitor.Top + (_monitor.Height - height) / 2; }
        SetWindowPos(new WindowInteropHelper(_toolbar).Handle, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010);
    }
    public void SetDrawing(bool enabled)
    {
        if (_disposed) return;
        _drawing = enabled;
        _controller.SetDrawing(enabled);
        // 완전 투명한 layered 창은 OS hit test에서 밑 창으로 통과할 수 있다.
        // 그리기 중만 최소 알파 배경을 두고 OFF에는 투명/입력 통과로 복귀한다.
        _overlay.Background = enabled
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0))
            : Brushes.Transparent;
        var hwnd = new WindowInteropHelper(_overlay).Handle;
        var style = GetWindowLong(hwnd, -20);
        SetWindowLong(hwnd, -20, enabled ? style & ~0x20 : style | 0x20); // OFF는 그림을 보존하고 마우스를 아래 앱으로 통과시킨다.
        _drawingButton.Content = enabled ? "그림 유지하고 OFF" : "그리기 ON";
        _status.Text = enabled ? "그리기 중 · Esc로 화면 조작 복귀" : "화면 조작 중 · 그림 유지";
        if (!enabled) { _layer.Canvas.ReleaseMouseCapture(); _points.Clear(); }
        DrawingChanged?.Invoke(enabled);
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true;
        try { await action(); }
        catch (Exception ex) { _status.Text = "판서 실패: " + ex.GetType().Name; }
        finally { _busy = false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _tools.PropertyChanged -= ToolsChanged;
        _toolbar.Close(); _overlay.Close(); _layer.Dispose();
    }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
