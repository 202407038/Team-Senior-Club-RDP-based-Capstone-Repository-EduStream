using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 9월 5주차 2번: 실제 TCP 참가 연결 기준으로 원격 제어 승인과 회수(권한 OFF·끊김·세션 종료)를 검증합니다.
/// 3번 입력 엔진은 아직 없으므로 호출 기록 대역으로 회수 요청 도달까지만 확인합니다.
/// </summary>
public sealed class SessionManagerRemoteControlTests
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task WithoutInputEngine_RequestFailsAndNeverBecomesActive()
    {
        await using var rig = await Rig.OpenAsync(attachGate: false);
        await rig.ConnectAndJoinAsync("Alice");

        var error = await Assert.ThrowsAsync<CollaborationException>(() => rig.SessionManager.RequestControlAsync("Alice"));

        Assert.Equal(CollaborationError.UnsupportedCapability, error.Code);
        Assert.Equal(ControlPhase.Failed, rig.SessionManager.CurrentControlState!.Phase);
    }

    [Fact]
    public async Task StudentTurnsControlOff_RevokesActiveControlAndInput()
    {
        await using var rig = await Rig.OpenAsync();
        await rig.ConnectAndJoinAsync("Alice");
        await rig.SessionManager.RequestControlAsync("Alice");
        Assert.Equal(ControlPhase.Active, rig.SessionManager.CurrentControlState!.Phase);

        var updated = await rig.SessionManager.UpdateParticipantPermissionsAsync("Alice", allowViewing: true, allowControl: false);

        Assert.True(updated);
        Assert.Equal(ControlPhase.Revoked, rig.SessionManager.CurrentControlState!.Phase);
        Assert.Single(rig.Gate.Revoked);
        await Assert.ThrowsAsync<CollaborationException>(() => rig.SessionManager.RequestControlAsync("Alice"));
    }

    [Fact]
    public async Task StudentDisconnects_RevokesActiveControlAndInput()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.ConnectAndJoinAsync("Alice");
        await rig.SessionManager.RequestControlAsync("Alice");

        rig.Disconnect(alice);

        await WaitUntilAsync(() => !rig.Gate.Revoked.IsEmpty, DefaultWait);
        Assert.Equal(ControlPhase.Revoked, rig.SessionManager.CurrentControlState!.Phase);
    }

    [Fact]
    public async Task SwitchingTarget_RevokesPreviousStudentFirst()
    {
        await using var rig = await Rig.OpenAsync();
        await rig.ConnectAndJoinAsync("Alice");
        await rig.ConnectAndJoinAsync("Bob");

        await rig.SessionManager.RequestControlAsync("Alice");
        var alice = rig.SessionManager.CurrentControlState!.Student;
        await rig.SessionManager.RequestControlAsync("Bob");

        var current = rig.SessionManager.CurrentControlState!;
        Assert.Equal(ControlPhase.Active, current.Phase);
        Assert.NotEqual(alice, current.Student);
        Assert.Equal(alice, Assert.Single(rig.Gate.Revoked).Student);
    }

    [Fact]
    public async Task CloseSession_RevokesActiveControl()
    {
        var rig = await Rig.OpenAsync();
        await rig.ConnectAndJoinAsync("Alice");
        await rig.SessionManager.RequestControlAsync("Alice");

        await rig.DisposeAsync();

        Assert.Single(rig.Gate.Revoked);
        Assert.Null(rig.SessionManager.CurrentControlState);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException("조건이 제한 시간 안에 충족되지 않았습니다.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class RecordingInputGate : IRemoteInputGate
    {
        public ConcurrentQueue<RemoteControlState> Granted { get; } = new();
        public ConcurrentQueue<RemoteControlState> Revoked { get; } = new();

        public Task GrantAsync(RemoteControlState requested, CancellationToken cancellationToken)
        {
            Granted.Enqueue(requested);
            return Task.CompletedTask;
        }

        public Task RevokeAsync(RemoteControlState revoked, CancellationToken cancellationToken)
        {
            Revoked.Enqueue(revoked);
            return Task.CompletedTask;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<TcpClientService> _clients = new();
        private readonly PacketSerializer _serializer = new();
        private bool _disposed;

        private Rig(SessionManager sessionManager, int port)
        {
            SessionManager = sessionManager;
            Port = port;
        }

        public SessionManager SessionManager { get; }
        public int Port { get; }
        public RecordingInputGate Gate { get; } = new();

        public static async Task<Rig> OpenAsync(bool attachGate = true)
        {
            var port = GetFreePort();
            var logSink = new InMemoryLogSink();
            var sessionManager = new SessionManager(logSink, new TcpServerService(logSink, new PacketSerializer()));
            var rig = new Rig(sessionManager, port);
            if (attachGate)
                sessionManager.AttachRemoteInputGate(rig.Gate);
            await sessionManager.OpenSessionAsync("RemoteControlTest", port);
            return rig;
        }

        public async Task<TcpClientService> ConnectAndJoinAsync(string displayName)
        {
            var client = new TcpClientService(new InMemoryLogSink(), _serializer);
            await client.ConnectAsync("127.0.0.1", Port);

            var ackReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PacketReceived += (packetType, _) =>
            {
                if (packetType is PacketType.Ack or PacketType.Error)
                    ackReceived.TrySetResult(true);
                return Task.CompletedTask;
            };
            await client.SendAsync(PacketFactory.CreateSessionJoin(
                senderId: displayName, displayName: displayName,
                targetAddress: "127.0.0.1", targetPort: Port));
            await ackReceived.Task.WaitAsync(DefaultWait);

            _clients.Add(client);
            return client;
        }

        public void Disconnect(TcpClientService client)
        {
            _clients.Remove(client);
            client.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var client in _clients)
            {
                try { client.Dispose(); } catch { }
            }
            _clients.Clear();
            await SessionManager.CloseSessionAsync();
        }
    }
}
