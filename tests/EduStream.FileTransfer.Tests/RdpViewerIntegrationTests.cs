using System.Reflection;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Threading;
using EduStream.Client.Services;
using EduStream.Client.ViewModels;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

[Collection("WDS integration")]
public sealed class RdpViewerIntegrationTests
{
    [WdsFact]
    public async Task TwoViewers_ShouldConnectViewOnly_AndOneRevocationShouldPreserveOther()
    {
        var log = new InMemoryLogSink();
        await using var sharer = new RdpSharingService(log);
        await using var first = await ViewerRig.Create();
        await using var second = await ViewerRig.Create();
        var session = Guid.NewGuid();
        var sharing = await sharer.StartAsync(session);
        var password = Guid.NewGuid().ToString("N");
        var invite1 = await sharer.CreateInvitationAsync(session, sharing, "Smoke1", Guid.NewGuid(), password, DateTimeOffset.UtcNow.AddMinutes(2));
        var invite2 = await sharer.CreateInvitationAsync(session, sharing, "Smoke2", Guid.NewGuid(), password, DateTimeOffset.UtcNow.AddMinutes(2));
        await first.Viewer.ConnectAsync(invite1, password);
        await first.Connected.Task.WaitAsync(TimeSpan.FromSeconds(25));
        await second.Viewer.ConnectAsync(invite2, password);
        await second.Connected.Task.WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(2, log.Snapshot().Count(x => x.Contains("보기 전용 참가 승인")));
        await sharer.RevokeInvitationAsync(invite1.InvitationId);
        await first.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RdpConnectionState.Connected, second.Latest?.State);
        var replacement = await sharer.CreateInvitationAsync(session, sharing, "Smoke1", Guid.NewGuid(), password, DateTimeOffset.UtcNow.AddMinutes(2));
        first.Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await first.Viewer.ConnectAsync(replacement, password);
        await first.Connected.Task.WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(RdpConnectionState.Connected, second.Latest?.State);
    }

    [Fact]
    public async Task InvalidInvitation_ShouldNotReachViewer_AndStaleStatusShouldBeIgnored()
    {
        var viewer = new FakeViewer();
        var vm = new ClientViewModel(viewer);
        Set(vm, "_currentRdpConnectionId", Guid.NewGuid());
        var id = (Guid)Get(vm, "_currentRdpConnectionId")!;
        var invalid = new RdpInvitationPacket { SessionId = Guid.NewGuid(), ParticipantId = "wrong", ConnectionId = id, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await (Task)Invoke(vm, "HandleRdpInvitationAsync", invalid)!;
        Assert.Equal(0, viewer.ConnectCalls);
        var old = Guid.NewGuid();
        var before = vm.RdpStatusText;
        viewer.Emit(RdpConnectionStatus.Create(Guid.NewGuid(), "old").BeginConnect(old).Connected(old));
        Assert.Equal(before, vm.RdpStatusText);
        await vm.ShutdownAsync();
    }

    [Fact]
    public async Task PasswordMustBeUserSupplied_AndDisconnectMustResetRdp()
    {
        var viewer = new FakeViewer();
        var vm = new ClientViewModel(viewer);
        var session = Guid.NewGuid();
        var connection = Guid.NewGuid();
        Set(vm, "_currentRdpConnectionId", connection);
        Set(vm, "_rdpSessionId", session);
        Set(vm, "_rdpParticipant", "Alice");
        typeof(ClientViewModel).GetProperty("IsConnected")!.SetValue(vm, true);
        var invitation = new RdpInvitationPacket { SessionId = session, ParticipantId = "Alice", ConnectionId = connection,
            SharingId = Guid.NewGuid(), InvitationId = Guid.NewGuid(), ConnectionString = "test", DataLength = 4, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1), ViewOnly = true };
        await (Task)Invoke(vm, "HandleRdpInvitationAsync", invitation)!;
        Assert.Equal(0, viewer.ConnectCalls);
        await vm.ConnectRdpWithPasswordAsync("separate-test-password");
        Assert.Equal(1, viewer.ConnectCalls);
        Assert.Equal("separate-test-password", viewer.Password);
        await (Task)Invoke(vm, "OnDisconnectedAsync", "test")!;
        Assert.True(viewer.DisconnectCalls > 0);
        Assert.Equal(Guid.Empty, Get(vm, "_currentRdpConnectionId"));
        Assert.False(vm.IsRdpActive);
        await vm.ShutdownAsync();
    }

    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Get(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);

    private sealed class FakeViewer : IRdpViewerService
    {
        public event Action<RdpConnectionStatus>? StatusChanged;
        public int ConnectCalls, DisconnectCalls;
        public string? Password;
        public void Emit(RdpConnectionStatus status) => StatusChanged?.Invoke(status);
        public Task ConnectAsync(RdpInvitationPacket invitation, string password, CancellationToken token = default)
        { ConnectCalls++; Password = password; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken token = default) { DisconnectCalls++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class ViewerRig : IAsyncDisposable
    {
        public RdpViewerService Viewer { get; } = new();
        public TaskCompletionSource<bool> Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RdpConnectionStatus? Latest;
        private Dispatcher _dispatcher = null!;
        private HwndSource _window = null!;
        private Thread _thread = null!;

        public static async Task<ViewerRig> Create()
        {
            var rig = new ViewerRig();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig._thread = new Thread(() =>
            {
                try
                {
                    rig._dispatcher = Dispatcher.CurrentDispatcher;
                    var host = new WindowsFormsHost();
                    rig._window = new HwndSource(new HwndSourceParameters("EduStream RDP smoke")
                    { Width = 320, Height = 180, PositionX = -30000, PositionY = -30000, WindowStyle = unchecked((int)0x80000000) });
                    rig._window.RootVisual = host;
                    host.Measure(new Size(320, 180));
                    host.Arrange(new Rect(0, 0, 320, 180));
                    host.UpdateLayout();
                    rig.Viewer.AttachTo(host);
                    rig.Viewer.StatusChanged += status =>
                    {
                        rig.Latest = status;
                        if (status.State == RdpConnectionState.Connected) rig.Connected.TrySetResult(true);
                        if (status.State == RdpConnectionState.Failed)
                        {
                            rig.Connected.TrySetException(new InvalidOperationException("Viewer failed: " + status.Failure));
                            rig.Terminated.TrySetResult(true);
                        }
                    };
                    ready.SetResult(true);
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true };
            rig._thread.SetApartmentState(ApartmentState.STA);
            rig._thread.Start();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return rig;
        }
        public async ValueTask DisposeAsync()
        {
            await Viewer.DisposeAsync();
            await _dispatcher.InvokeAsync(() => _window.Dispose());
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            await Task.Run(() => _thread.Join());
        }
    }
}
public sealed class WdsFactAttribute : FactAttribute
{
    public WdsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EDUSTREAM_WDS_SMOKE") != "1")
            Skip = "실제 Windows WDS 검증은 EDUSTREAM_WDS_SMOKE=1로 별도 실행";
    }
}
