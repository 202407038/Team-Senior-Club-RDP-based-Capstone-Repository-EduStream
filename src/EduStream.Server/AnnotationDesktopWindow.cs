using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EduStream.Server.Rdp;
using EduStream.Server.ViewModels;
using MonitorInfo = EduStream.ShareHost.MonitorInfo;
using Point = System.Drawing.Point;
using Brush = System.Windows.Media.Brush;
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
    private readonly TextBlock _status = new() { Foreground = (Brush)System.Windows.Application.Current.FindResource("BrushTextMuted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _drawingButton = new() { Content = "그림 유지하고 OFF" };
    private readonly Border _toolsPanel = new();
    private readonly Dictionary<string, ToggleButton> _toolButtons = new();
    private readonly Dictionary<string, ToggleButton> _colorButtons = new();
    private readonly Button _placementButton = new();
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
        BuildToolbarContent(tools);
        _toolbar = new Window
        {
            Title = "EduStream 판서 도구", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = _toolsPanel
        };
        _toolbar.Closing += (_, e) =>
        {
            if (_disposed) return;
            // 창 객체는 재사용하되 X를 누른 도구창은 실제로 숨긴다. 판서는 보존한다.
            e.Cancel = true;
            SetDrawing(false);
            _toolbar.Hide();
        };
        _overlay.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SetDrawing(false); e.Handled = true; } };
        _toolbar.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SetDrawing(false); e.Handled = true; } };
        _layer.Canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (!_drawing || _busy) return;
            if (!_engine.CurrentState.IsVisible)
            {
                _status.Text = "판서가 숨겨져 있습니다. 숨김 / 표시를 눌러 표시한 뒤 그려 주세요.";
                e.Handled = true; return;
            }
            SelectCurrentTool();
            _points.Clear(); AddPoint(e.GetPosition(_layer.Canvas)); _layer.Canvas.CaptureMouse(); e.Handled = true;
        };
        _layer.Canvas.MouseMove += (_, e) =>
        {
            if (!_layer.Canvas.IsMouseCaptured) return;
            // 긴 드래그에서도 끝점은 계속 이동한다. 메모리는 한 스트로크당 4096점으로 제한한다.
            if (_points.Count >= 4095) _points.RemoveAt(_points.Count - 1);
            AddPoint(e.GetPosition(_layer.Canvas));
            _layer.ShowPreview(_controller.CreateStroke("professor", _points));
        };
        _layer.Canvas.MouseLeftButtonUp += async (_, e) =>
        {
            if (!_layer.Canvas.IsMouseCaptured) return;
            AddPoint(e.GetPosition(_layer.Canvas));
            var points = _points.ToArray();
            CancelGesture();
            await RunAsync(() => _controller.SubmitStrokeAsync("professor", points));
        };
        _layer.Canvas.LostMouseCapture += (_, _) => { _points.Clear(); _layer.ClearPreview(); };
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
    private void SelectCurrentTool()
    {
        var tool = _tools.Tool switch { "직선" => EduStream.Core.Collaboration.AnnotationTool.Line,
            "사각형" => EduStream.Core.Collaboration.AnnotationTool.Rectangle, "타원" => EduStream.Core.Collaboration.AnnotationTool.Ellipse,
            "지우개" => EduStream.Core.Collaboration.AnnotationTool.Eraser, _ => EduStream.Core.Collaboration.AnnotationTool.Pen };
        uint color = _tools.Color switch { "빨강" => 0xFFFF4040, "파랑" => 0xFF4080FF, "초록" => 0xFF40CC60,
            "검정" => 0xFF000000, "흰색" => 0xFFFFFFFF, _ => 0xFFFFFF00 };
        _controller.SelectTool(tool, color, _tools.StrokeWidth);
    }
    private void CancelGesture()
    {
        _points.Clear(); _layer.ClearPreview(); _layer.Canvas.ReleaseMouseCapture();
    }
    private void ToolsChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshSelection();
        if (e.PropertyName == nameof(AnnotationToolsViewModel.Placement)) PlaceToolbar();
    }

    private static readonly Dictionary<string, string> ToolLabels = new()
    {
        ["자유선"] = "✏ 펜", ["직선"] = "╱ 직선", ["사각형"] = "▭ 사각형", ["타원"] = "◯ 타원", ["지우개"] = "⌫ 지우개",
    };
    private static readonly Dictionary<string, string> ColorHex = new()
    {
        ["노랑"] = "#FFFF00", ["빨강"] = "#FF4040", ["파랑"] = "#4080FF", ["초록"] = "#40CC60", ["검정"] = "#000000", ["흰색"] = "#FFFFFF",
    };

    private static T Theme<T>(string key) where T : class => (T)System.Windows.Application.Current.FindResource(key);

    private static Style ParseStyle(string xaml) => (Style)System.Windows.Markup.XamlReader.Parse(
        "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"ToggleButton\">" + xaml + "</Style>");

    private static readonly Lazy<Style> ToolToggleStyle = new(() => ParseStyle(
        "<Setter Property=\"Foreground\" Value=\"#E0E0E0\"/><Setter Property=\"Background\" Value=\"#252525\"/>" +
        "<Setter Property=\"Padding\" Value=\"10,6\"/><Setter Property=\"Margin\" Value=\"2\"/><Setter Property=\"FontSize\" Value=\"12\"/><Setter Property=\"Cursor\" Value=\"Hand\"/>" +
        "<Setter Property=\"Template\"><Setter.Value><ControlTemplate TargetType=\"ToggleButton\">" +
        "<Border x:Name=\"Root\" Background=\"{TemplateBinding Background}\" CornerRadius=\"8\" Padding=\"{TemplateBinding Padding}\">" +
        "<ContentPresenter HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"Root\" Property=\"Background\" Value=\"#333333\"/></Trigger>" +
        "<Trigger Property=\"IsChecked\" Value=\"True\"><Setter TargetName=\"Root\" Property=\"Background\" Value=\"#BB86FC\"/><Setter Property=\"Foreground\" Value=\"#121212\"/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>"));

    private static readonly Lazy<Style> ColorDotStyle = new(() => ParseStyle(
        "<Setter Property=\"Width\" Value=\"26\"/><Setter Property=\"Height\" Value=\"26\"/><Setter Property=\"Margin\" Value=\"2\"/><Setter Property=\"Cursor\" Value=\"Hand\"/>" +
        "<Setter Property=\"Template\"><Setter.Value><ControlTemplate TargetType=\"ToggleButton\"><Grid>" +
        "<Ellipse x:Name=\"Ring\" Stroke=\"Transparent\" StrokeThickness=\"2\"/>" +
        "<Ellipse Margin=\"4\" Fill=\"{TemplateBinding Background}\" Stroke=\"#555555\" StrokeThickness=\"1\"/></Grid>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"Ring\" Property=\"Stroke\" Value=\"#666666\"/></Trigger>" +
        "<Trigger Property=\"IsChecked\" Value=\"True\"><Setter TargetName=\"Ring\" Property=\"Stroke\" Value=\"#BB86FC\"/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>"));

    /// <summary>앱 테마(다크 + 퍼플/틸)에 맞춘 한 줄 도구 모음. 기능은 그대로이고 모양만 바꾼다.</summary>
    private void BuildToolbarContent(AnnotationToolsViewModel tools)
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        UIElement Separator() => new Border
        {
            Width = 1, Height = 24, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Theme<Brush>("BrushBorder"),
        };
        Button AddAction(string label, Func<Task> action, string style = "GhostButton")
        {
            var button = new Button { Content = label, Style = Theme<Style>(style), Height = double.NaN, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(2), FontSize = 12 };
            button.Click += async (_, _) => await RunAsync(action);
            row.Children.Add(button);
            return button;
        }

        var title = new StackPanel { Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.SizeAll };
        title.Children.Add(new TextBlock { Text = "교수자 판서 도구", Foreground = Theme<Brush>("BrushTextPrimary"), FontWeight = FontWeights.SemiBold, FontSize = 13 });
        title.Children.Add(new TextBlock { Text = "공유 화면 위에 표시", Foreground = Theme<Brush>("BrushTextMuted"), FontSize = 10 });
        title.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { _toolbar.DragMove(); } catch (InvalidOperationException) { } } };
        row.Children.Add(title);
        row.Children.Add(Separator());

        foreach (var name in tools.Tools)
        {
            var toggle = new ToggleButton { Content = ToolLabels.GetValueOrDefault(name, name), Style = ToolToggleStyle.Value, ToolTip = name };
            toggle.Click += (_, _) => { tools.Tool = name; RefreshSelection(); };
            _toolButtons[name] = toggle; row.Children.Add(toggle);
        }
        row.Children.Add(Separator());

        foreach (var name in tools.Colors)
        {
            var dot = new ToggleButton
            {
                Style = ColorDotStyle.Value, ToolTip = name,
                Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(ColorHex.GetValueOrDefault(name, "#FFFF00"))),
            };
            dot.Click += (_, _) => { tools.Color = name; RefreshSelection(); };
            _colorButtons[name] = dot; row.Children.Add(dot);
        }
        row.Children.Add(Separator());

        row.Children.Add(new TextBlock { Text = "굵기", Foreground = Theme<Brush>("BrushTextSecondary"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        var width = new Slider { Minimum = 1, Maximum = 20, Value = tools.StrokeWidth, Width = 90, VerticalAlignment = VerticalAlignment.Center, ToolTip = "선 굵기" };
        var widthText = new TextBlock { Text = ((int)tools.StrokeWidth).ToString(), Foreground = Theme<Brush>("BrushTextPrimary"), Width = 20, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        width.ValueChanged += (_, _) => { tools.StrokeWidth = width.Value; widthText.Text = ((int)tools.StrokeWidth).ToString(); };
        row.Children.Add(width); row.Children.Add(widthText);
        row.Children.Add(Separator());

        AddAction("↶ 실행 취소", () => _engine.UndoAsync());
        AddAction("🗑 전체 지우기", () => _engine.ClearAllStrokesAsync());
        AddAction("👁 숨김 / 표시", () => _engine.ToggleLayerVisibilityAsync());
        row.Children.Add(Separator());

        _drawingButton.Style = Theme<Style>("PrimaryButton");
        _drawingButton.Height = double.NaN; _drawingButton.Padding = new Thickness(12, 6, 12, 6); _drawingButton.Margin = new Thickness(2); _drawingButton.FontSize = 12;
        _drawingButton.Click += (_, _) => SetDrawing(!_drawing);
        row.Children.Add(_drawingButton);
        AddAction("지우고 OFF", async () => { await _engine.ClearAllStrokesAsync(); SetDrawing(false); }, "SecondaryButton");
        row.Children.Add(Separator());

        _placementButton.Style = Theme<Style>("GhostButton"); _placementButton.Height = double.NaN;
        _placementButton.Padding = new Thickness(10, 6, 10, 6); _placementButton.Margin = new Thickness(2); _placementButton.FontSize = 12;
        _placementButton.ToolTip = "도구 모음 위치 바꾸기";
        _placementButton.Click += (_, _) =>
        {
            var list = tools.Placements.ToList();
            tools.Placement = list[(list.IndexOf(tools.Placement) + 1) % list.Count];
        };
        row.Children.Add(_placementButton);

        var close = new Button { Content = "✕", Style = Theme<Style>("GhostButton"), Height = double.NaN, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(2), FontSize = 12, ToolTip = "도구 모음 닫기 (그림은 유지)" };
        close.Click += (_, _) => _toolbar.Close();
        row.Children.Add(close);

        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(new Border { Margin = new Thickness(2, 6, 2, 0), Child = _status });
        _toolsPanel.Child = stack;
        _toolsPanel.Background = Theme<Brush>("BrushBgSurface");
        _toolsPanel.BorderBrush = Theme<Brush>("BrushBorder");
        _toolsPanel.BorderThickness = new Thickness(1);
        _toolsPanel.CornerRadius = new CornerRadius(12);
        _toolsPanel.Padding = new Thickness(12, 10, 12, 10);
        _toolsPanel.MaxWidth = 1100;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var (name, button) in _toolButtons) button.IsChecked = name == _tools.Tool;
        foreach (var (name, button) in _colorButtons) button.IsChecked = name == _tools.Color;
        _placementButton.Content = "위치: " + _tools.Placement;
    }
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
        if (enabled && !_toolbar.IsVisible)
        {
            _toolbar.Show();
            PlaceToolbar();
        }
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
        if (!enabled) CancelGesture();
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
