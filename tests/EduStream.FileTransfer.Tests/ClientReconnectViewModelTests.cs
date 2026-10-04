using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using EduStream.Client.ViewModels;
using EduStream.Core.Collaboration;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당(U03, #58 리뷰): 실제 ClientViewModel 참가·재연결 경로에서 서버의 참가 승인을 받은 뒤에만 참가 성공으로
/// 처리하는지 확인합니다. 보호 채널은 제품 TLS 리스너·참가 게이트를 쓰고, 재연결 토큰 검증기와 TCP 상대만
/// 승인·승인 전 끊김·거부·무응답을 제어하는 테스트 대역입니다.
/// </summary>
public sealed class ClientReconnectViewModelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Reconnect_WithApproval_RejoinsAfterAck()
    {
        await using var rig = await Rig.OpenAsync();
        var vm = rig.CreateViewModel();
        try
        {
            await rig.JoinAndDropAsync(vm, JoinReply.Approve);

            await WaitUntilAsync(() => vm.IsConnected && !vm.IsConnecting);
            Assert.Equal(1, rig.ReconnectValidations);
            Assert.Equal(2, rig.JoinRequests);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task Reconnect_DisconnectBeforeAck_ReturnsToManualJoin()
    {
        await using var rig = await Rig.OpenAsync();
        var vm = rig.CreateViewModel();
        try
        {
            await rig.JoinAndDropAsync(vm, JoinReply.CloseWithoutAck);

            await WaitUntilAsync(() => !vm.IsConnecting);
            AssertManualJoinAvailable(vm);
            // 소비된 토큰으로 다시 시도하지 않는다(첫 재시도 간격 1초보다 오래 기다려 확인).
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Assert.Equal(1, rig.ReconnectValidations);
            Assert.False(vm.IsConnecting);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task Reconnect_JoinRejected_StopsWithoutRetry()
    {
        await using var rig = await Rig.OpenAsync();
        var vm = rig.CreateViewModel();
        try
        {
            await rig.JoinAndDropAsync(vm, JoinReply.Reject);

            await WaitUntilAsync(() => !vm.IsConnecting);
            AssertManualJoinAvailable(vm);
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Assert.Equal(1, rig.ReconnectValidations);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task Reconnect_AckTimeout_ClosesConnectionAndReturnsToManualJoin()
    {
        await using var rig = await Rig.OpenAsync();
        var vm = rig.CreateViewModel();
        vm.JoinAckTimeout = TimeSpan.FromMilliseconds(500);
        try
        {
            await rig.JoinAndDropAsync(vm, JoinReply.Silent);

            await WaitUntilAsync(() => !vm.IsConnecting);
            AssertManualJoinAvailable(vm);
            await rig.WaitForServerSideCloseAsync(connectionIndex: 1);
            Assert.Equal(1, rig.ReconnectValidations);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task Join_DisconnectBeforeAck_ReturnsToManualJoin()
    {
        await using var rig = await Rig.OpenAsync();
        rig.Replies.Enqueue(JoinReply.CloseWithoutAck);
        var vm = rig.CreateViewModel();
        try
        {
            vm.JoinSessionCommand.Execute(null);

            await WaitUntilAsync(() => rig.JoinRequests == 1 && !vm.IsConnecting);
            AssertManualJoinAvailable(vm);
            Assert.Equal("연결 실패", vm.ConnectionState);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task Join_UserLeavesWhileAwaitingAck_ClearsProgress()
    {
        await using var rig = await Rig.OpenAsync();
        rig.Replies.Enqueue(JoinReply.Silent);
        var vm = rig.CreateViewModel();
        try
        {
            vm.JoinSessionCommand.Execute(null);
            await WaitUntilAsync(() => rig.JoinRequests == 1);
            Assert.True(vm.IsConnecting);
            Assert.True(vm.DisconnectCommand.CanExecute(null));

            vm.DisconnectCommand.Execute(null);

            await WaitUntilAsync(() => !vm.IsConnecting);
            AssertManualJoinAvailable(vm);
            await rig.WaitForServerSideCloseAsync(connectionIndex: 0);
        }
        finally { await vm.ShutdownAsync(); }
    }

    private static void AssertManualJoinAvailable(ClientViewModel vm)
    {
        Assert.False(vm.IsConnected);
        Assert.False(vm.IsConnecting);
        Assert.True(vm.JoinSessionCommand.CanExecute(null));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건이 제한 시간 안에 충족되지 않았습니다.");
            await Task.Delay(20);
        }
    }

    private enum JoinReply { Approve, CloseWithoutAck, Reject, Silent }

    /// <summary>제품 보호 채널(리스너·게이트)과, 참가 요청에 대한 응답을 고를 수 있는 TCP 상대입니다.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private const string GrantedToken = "test-reconnect-token";
        private readonly X509Certificate2 _certificate = ProfessorCertificateStore.CreateEphemeral();
        private readonly InMemoryLogSink _log = new();
        private readonly PacketSerializer _serializer = new();
        private readonly Guid _sessionId = Guid.NewGuid();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<ServerConnection> _connections = new();
        private SecureCollaborationListener _listener = null!;
        private SecureRoomGate _gate = null!;
        private TcpListener _tcp = null!;
        private int _joinRequests;
        private int _reconnectValidations;
        private int _tokenUsed;

        public Queue<JoinReply> Replies { get; } = new();
        public int Port { get; private set; }
        public int JoinRequests => Volatile.Read(ref _joinRequests);
        public int ReconnectValidations => Volatile.Read(ref _reconnectValidations);

        public static async Task<Rig> OpenAsync()
        {
            var rig = new Rig();
            for (var attempt = 0; ; attempt++)
            {
                rig.Port = TestPortAllocator.GetFreePortPair();
                try
                {
                    rig.Start();
                    return rig;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 5)
                {
                    await rig.StopListenersAsync();
                }
            }
        }

        private void Start()
        {
            _listener = new SecureCollaborationListener(_certificate, _log);
            _gate = new SecureRoomGate(_listener, _sessionId, null, _log)
            {
                // 토큰은 한 번만 통과시킨다. 두 번째 검증 시도가 오면 소비된 토큰을 재사용한 것이다.
                ReconnectValidator = (_, token) =>
                {
                    Interlocked.Increment(ref _reconnectValidations);
                    var valid = token == GrantedToken && Interlocked.Exchange(ref _tokenUsed, 1) == 0;
                    return Task.FromResult<object?>(valid ? new object() : null);
                }
            };
            _listener.Start(CollaborationPorts.ForSession(Port));
            _tcp = new TcpListener(IPAddress.Loopback, Port);
            _tcp.Start();
            _ = AcceptLoopAsync();
        }

        public ClientViewModel CreateViewModel() => new()
        {
            HostAddress = "127.0.0.1",
            Port = Port,
            ConnectionCode = _listener.ConnectionCode,
            DisplayName = "Alice"
        };

        /// <summary>승인으로 참가시킨 뒤 서버 쪽에서 TCP를 끊어, 재연결 참가 요청에는 <paramref name="reconnectReply"/>로 응답합니다.</summary>
        public async Task JoinAndDropAsync(ClientViewModel vm, JoinReply reconnectReply)
        {
            lock (Replies)
            {
                Replies.Enqueue(JoinReply.Approve);
                Replies.Enqueue(reconnectReply);
            }
            vm.JoinSessionCommand.Execute(null);
            await WaitUntilAsync(() => vm.IsConnected);
            // 재연결 토큰이 학생에게 도착한 뒤 끊는다.
            var tokenField = typeof(ClientViewModel).GetField("_reconnectToken", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await WaitUntilAsync(() => tokenField.GetValue(vm) is not null);
            ServerConnection first;
            lock (_connections) first = _connections[0];
            first.Client.Dispose();
            await WaitUntilAsync(() => JoinRequests == 2);
        }

        public async Task WaitForServerSideCloseAsync(int connectionIndex)
        {
            ServerConnection connection;
            lock (_connections) connection = _connections[connectionIndex];
            await connection.Closed.Task.WaitAsync(Wait);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _tcp.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                var connection = new ServerConnection(client);
                lock (_connections) _connections.Add(connection);
                _ = ServeAsync(connection);
            }
        }

        private async Task ServeAsync(ServerConnection connection)
        {
            var stream = connection.Client.GetStream();
            try
            {
                var join = _serializer.Deserialize<SessionJoinPacket>(await ReadFrameAsync(stream))!;
                Interlocked.Increment(ref _joinRequests);
                JoinReply reply;
                lock (Replies) reply = Replies.Count > 0 ? Replies.Dequeue() : JoinReply.Approve;

                switch (reply)
                {
                    case JoinReply.Approve:
                        var secure = _gate.Redeem(join.JoinTicket, join.DisplayName)!.Connection;
                        await WriteFrameAsync(stream, _serializer.Serialize(PacketFactory.CreateAck(
                            "Server", AckCodes.SessionJoined, "참가", _sessionId, join.CorrelationId)));
                        await secure.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(),
                            new ReconnectGrantNotice(GrantedToken, 120)));
                        break;
                    case JoinReply.CloseWithoutAck:
                        connection.Client.Dispose();
                        return;
                    case JoinReply.Reject:
                        await WriteFrameAsync(stream, _serializer.Serialize(PacketFactory.CreateError(
                            "Server", ErrorCodes.JoinRejected, "참가 거부", false, _sessionId, join.CorrelationId)));
                        break;
                }

                // 이후 프레임(RDP 초대 요청 등)은 읽고 버린다. 상대가 닫으면 끝난다.
                while (true) await ReadFrameAsync(stream);
            }
            catch { }
            finally
            {
                connection.Closed.TrySetResult();
            }
        }

        private static async Task<byte[]> ReadFrameAsync(NetworkStream stream)
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header);
            var payload = new byte[BitConverter.ToInt32(header)];
            await stream.ReadExactlyAsync(payload);
            return payload;
        }

        private static async Task WriteFrameAsync(NetworkStream stream, byte[] payload)
        {
            await stream.WriteAsync(BitConverter.GetBytes(payload.Length));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }

        private async Task StopListenersAsync()
        {
            try { _tcp?.Stop(); } catch { }
            if (_gate is not null) await _gate.DisposeAsync();
            if (_listener is not null) await _listener.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await StopListenersAsync();
            lock (_connections)
                foreach (var connection in _connections) connection.Client.Dispose();
            _certificate.Dispose();
        }
    }

    private sealed class ServerConnection(TcpClient client)
    {
        public TcpClient Client { get; } = client;
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
