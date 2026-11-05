using System.Text.Json;
using EduStream.Client.Services;
using EduStream.Client.ViewModels;
using EduStream.Server.Services;
using EduStream.ShareHost;

namespace EduStream.FileTransfer.Tests;

public sealed class UsabilityPreferencesTests
{
    [Fact]
    public void JoinSettingsRoundTripAndOptOutErasesIdentity()
    {
        using var folder = new ReadinessFiles();
        var path = Path.Combine(folder.Downloads, "join.json");
        var store = new JoinPreferencesStore(path);
        Assert.Null(store.Load());
        var value = new JoinPreferences(true, "학생 1", "192.168.0.25", 5000);
        store.Save(value);
        Assert.Equal(value, store.Load());
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
            Assert.Equal(new[] { "Remember", "DisplayName", "HostAddress", "Port" },
                json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        store.Save(value with { Remember = false });
        Assert.Equal(new JoinPreferences(false, "", "", 5000), store.Load());
        Assert.DoesNotContain("192.168.0.25", File.ReadAllText(path));
        Assert.DoesNotContain("학생", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(folder.Downloads, "*.tmp"));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("0.0.0.0")]
    public void InvalidEndpointDoesNotOverwriteGoodSettings(string host)
    {
        using var folder = new ReadinessFiles();
        var path = Path.Combine(folder.Downloads, "join.json");
        var store = new JoinPreferencesStore(path);
        var good = new JoinPreferences(true, "학생", "127.0.0.1", 5000);
        store.Save(good);
        Assert.ThrowsAny<Exception>(() => store.Save(good with { HostAddress = host }));
        Assert.Equal(good, store.Load());
    }

    [Fact]
    public async Task CorruptSettingsDoNotPreventClientStarting()
    {
        using var folder = new ReadinessFiles();
        Directory.CreateDirectory(folder.Downloads);
        var path = Path.Combine(folder.Downloads, "join.json");
        File.WriteAllText(path, "{broken");
        var vm = new ClientViewModel(joinPreferences: new JoinPreferencesStore(path));
        try
        {
            Assert.Contains("읽지 못했습니다", vm.PreferencesNotice);
            Assert.True(vm.JoinSessionCommand.CanExecute(null));
            Assert.Equal(5000, vm.Port);
            Assert.False(vm.RoomPasswordRequired);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task ClientLoadsSavedFieldsButNotPasswordAndCanForget()
    {
        using var folder = new ReadinessFiles();
        var store = new JoinPreferencesStore(Path.Combine(folder.Downloads, "join.json"));
        store.Save(new JoinPreferences(true, "홍길동", "192.168.1.3", 5010));
        var vm = new ClientViewModel(joinPreferences: store);
        try
        {
            Assert.Equal("홍길동", vm.DisplayName);
            Assert.Equal("192.168.1.3", vm.HostAddress);
            Assert.Equal(5010, vm.Port);
            Assert.False(vm.RoomPasswordRequired);
            Assert.Null(vm.RoomPasswordProvider);
            vm.RememberJoinDetails = false;
            Assert.False(store.Load()!.Remember);
        }
        finally { await vm.ShutdownAsync(); }
        var next = new ClientViewModel(joinPreferences: store);
        try
        {
            Assert.False(next.RememberJoinDetails);
            Assert.NotEqual("홍길동", next.DisplayName);
        }
        finally { await next.ShutdownAsync(); }
    }

    private static MonitorInfo Monitor(string name, int x, int width = 1920, int dpi = 96) => new()
    { DeviceName = name, Left = x, Width = width, Height = 1080, DpiX = dpi, DpiY = dpi, IsPrimary = x == 0 };

    [Fact]
    public void MonitorRestoreRequiresSameTopologyNotEnumerationOrder()
    {
        using var folder = new ReadinessFiles();
        var store = new MonitorSelectionStore(Path.Combine(folder.Downloads, "monitor.json"));
        var main = Monitor("display1", 0);
        var second = Monitor("display2", 1920);
        store.Save(second, [main, second]);
        var saved = store.Load()!;
        Assert.Same(second, MonitorSelectionStore.Restore(saved, [second, main]));
        Assert.Null(MonitorSelectionStore.Restore(saved, [main]));
        Assert.Null(MonitorSelectionStore.Restore(saved, [main, Monitor("display2", 1920, dpi: 144)]));
        Assert.Null(MonitorSelectionStore.Restore(saved, [main, Monitor("display2", -1920)]));
        Assert.Null(MonitorSelectionStore.Restore(saved, [main, Monitor("display2", 1920, width: 2560)]));
        Assert.Throws<ArgumentException>(() => store.Save(Monitor("unknown", 0), [main]));
    }
}
