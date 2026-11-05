using System.IO;
using System.Text.Json;
using EduStream.ShareHost;

namespace EduStream.Server.Services;

public sealed record MonitorSelection(string DeviceName, string Topology);

/// <summary>모니터 번호만 기억하지 않고 배치·해상도·DPI가 같은 경우에만 이전 선택을 복원한다.</summary>
public sealed class MonitorSelectionStore
{
    private readonly string _path;
    public MonitorSelectionStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EduStream", "server-monitor.json");

    public MonitorSelection? Load()
    {
        if (!File.Exists(_path)) return null;
        if (new FileInfo(_path).Length > 16384) throw new InvalidDataException("모니터 설정 파일이 너무 큽니다.");
        return JsonSerializer.Deserialize<MonitorSelection>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("모니터 설정을 읽지 못했습니다.");
    }

    public static MonitorInfo? Restore(MonitorSelection selection, IReadOnlyList<MonitorInfo> monitors) =>
        selection.Topology == Describe(monitors)
            ? monitors.FirstOrDefault(m => m.DeviceName == selection.DeviceName) : null;

    public void Save(MonitorInfo selected, IReadOnlyList<MonitorInfo> monitors)
    {
        if (!monitors.Contains(selected)) throw new ArgumentException("현재 목록의 모니터가 아닙니다.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new MonitorSelection(selected.DeviceName, Describe(monitors))));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Describe(IReadOnlyList<MonitorInfo> monitors) =>
        JsonSerializer.Serialize(monitors.OrderBy(m => m.DeviceName, StringComparer.Ordinal)
            .Select(m => new { m.DeviceName, m.Left, m.Top, m.Width, m.Height, m.DpiX, m.DpiY, m.IsPrimary }));
}
