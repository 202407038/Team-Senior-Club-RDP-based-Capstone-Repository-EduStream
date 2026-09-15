using System.Net.Sockets;
using System.Net;
using System.Reflection;
using EduStream.Client.Services;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class TcpShutdownRaceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public async Task ServerConnection_DisposeWithQueuedSend_ShouldNotDestroyPendingWaiter(int count)
    {
        var type = typeof(TcpServerService).GetNestedType("ClientConnection", BindingFlags.NonPublic)!;
        var connection = Activator.CreateInstance(type, new TcpClient())!;
        var gate = (SemaphoreSlim)type.GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        await gate.WaitAsync();
        var sends = Enumerable.Range(0, count).Select(_ => (Task)type.GetMethod("SendAsync")!.Invoke(connection, [new byte[4]])!).ToArray();
        var send = Task.WhenAll(sends);
        Assert.False(send.IsCompleted);
        ((IDisposable)connection).Dispose();
        // 정상 송신자의 finally가 실행되는 순서를 재현한다. Dispose가 gate를 파괴하면 Release가 실패한다.
        gate.Release();
        var failure = await Record.ExceptionAsync(() => send.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.NotNull(failure);
        Assert.IsNotType<TimeoutException>(failure);
        Assert.All(sends, task => Assert.True(task.IsCompleted));
        ((IDisposable)connection).Dispose();
    }

    [Fact]
    public async Task ClientDispose_WithQueuedSend_ShouldFinishWithoutDeadlock()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClientService(new InMemoryLogSink(), new PacketSerializer());
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
            using var peer = await listener.AcceptTcpClientAsync();
            var gate = (SemaphoreSlim)typeof(TcpClientService).GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            await gate.WaitAsync();
            var send = client.SendAsync(new ChatPacket { Message = "shutdown race" });
            Assert.False(send.IsCompleted);
            client.Dispose();
            gate.Release();
            var failure = await Record.ExceptionAsync(() => send.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.NotNull(failure);
            Assert.IsNotType<TimeoutException>(failure);
        }
        finally { listener.Stop(); }
    }
}
