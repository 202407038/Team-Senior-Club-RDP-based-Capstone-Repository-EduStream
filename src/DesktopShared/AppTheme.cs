using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Data;
using EduStream.Core.Common;

namespace EduStream.Desktop;

public static class AppTheme
{
    private sealed class ThemeColor : DependencyObject
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(System.Windows.Media.Color), typeof(ThemeColor));
        public System.Windows.Media.Color Value { get => (System.Windows.Media.Color)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    }
    private static readonly Dictionary<string, ThemeColor> LiveColors = new();
    private static bool _initialized;
    public static bool IsLight { get; private set; }
    private static readonly string PreferencePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EduStream", "theme.txt");
    public static void Initialize()
    {
        if (_initialized) return;
        bool light = true;
        try { light = !File.Exists(PreferencePath) || File.ReadAllText(PreferencePath).Trim() != "dark"; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Apply(light);
    }
    public static void Apply(bool light)
    {
        var app = System.Windows.Application.Current ?? throw new InvalidOperationException("UI 초기화가 필요합니다.");
        app.Dispatcher.VerifyAccess();
        foreach (var (key, value) in AppThemePalette.Colors(light))
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            // 바인딩이 있는 브러시는 WPF가 동결하지 않아 열린 코드 생성 도구창에도 반영된다.
            if (!LiveColors.TryGetValue(key, out var source))
            {
                LiveColors[key] = source = new ThemeColor();
                var brush = new SolidColorBrush();
                BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new System.Windows.Data.Binding(nameof(ThemeColor.Value)) { Source = source, Mode = BindingMode.OneWay });
                app.Resources[key] = brush;
            }
            source.Value = color;
        }
        app.Resources["ThemeToggleGlyph"] = light ? "\uE708" : "\uE706";
        app.Resources["ThemeToggleLabel"] = light ? "다크 테마로 전환" : "라이트 테마로 전환";
        IsLight = light; _initialized = true;
    }
    public static string ToggleAndSave()
    {
        Apply(!IsLight);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, IsLight ? "light" : "dark");
            return IsLight ? "라이트 테마 · 다음 실행에도 유지" : "다크 테마 · 다음 실행에도 유지";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return "현재 테마는 적용했지만 다음 실행용 저장에 실패했습니다."; }
    }
}
