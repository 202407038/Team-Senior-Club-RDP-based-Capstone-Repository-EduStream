using System.Collections.Specialized;
using System.Reflection;
using EduStream.Client.ViewModels;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;

namespace EduStream.FileTransfer.Tests;

public sealed class ClientLogRefreshConcurrencyTests
{
    [Fact]
    public async Task ConcurrentRefresh_WaitsForWholeSnapshot_WithoutDuplicateOrLostEntries()
    {
        var vm = new ClientViewModel(new NoopViewer());
        var sink = (InMemoryLogSink)typeof(ClientViewModel)
            .GetField("_logSink", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        var refresh = typeof(ClientViewModel).GetMethod("SyncLogs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        sink.Write("first");
        sink.Write("second");
        sink.Write("third");
        var expected = sink.Snapshot().Reverse().ToArray();
        using var firstReset = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        var resetCount = 0;
        NotifyCollectionChangedEventHandler handler = (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Reset) return;
            if (Interlocked.Increment(ref resetCount) == 1)
            {
                firstReset.Set();
                if (!releaseFirst.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("test gate was not released");
            }
        };
        vm.ActivityLogs.CollectionChanged += handler;
        Task? first = null;
        Task? second = null;
        bool overlapped = false;
        string[]? actual = null;
        try
        {
            first = Task.Factory.StartNew(() => refresh.Invoke(vm, null), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(firstReset.Wait(TimeSpan.FromSeconds(3)));
            second = Task.Factory.StartNew(() =>
            {
                secondStarted.Set();
                refresh.Invoke(vm, null);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(3)));
            // 첫 Clear 이벤트 안에서 멈춘 동안 두 번째 Clear/Add가 완료되면 같은 목록을 동시 수정한 것이다.
            overlapped = await Task.WhenAny(second, Task.Delay(250)) == second;
        }
        finally
        {
            releaseFirst.Set();
            try
            {
                if (first is not null) await first.WaitAsync(TimeSpan.FromSeconds(5));
                if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                vm.ActivityLogs.CollectionChanged -= handler;
                actual = vm.ActivityLogs.ToArray();
                await vm.ShutdownAsync();
            }
        }
        Assert.False(overlapped, "로그 스냅샷 갱신 중 다른 스레드가 목록을 동시에 수정했습니다.");
        Assert.Equal(2, resetCount);
        Assert.Equal(expected, actual);
    }

    private sealed class NoopViewer : IRdpViewerService
    {
        public event Action<RdpConnectionStatus>? StatusChanged { add { } remove { } }
        public Task ConnectAsync(RdpInvitationPacket invitation, string invitationPassword,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
