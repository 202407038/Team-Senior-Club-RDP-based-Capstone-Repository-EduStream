using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Serialization;

namespace EduStream.Server.Services;

/// <summary>
/// TCP 서버: 클라이언트 연결 수락, 길이-헤더 프레이밍 프로토콜로 패킷 송수신을 담당합니다.
/// </summary>
public sealed class TcpServerService
{
    private const int MaxPacketSize = 10 * 1024 * 1024; // 10MB

    private readonly ILogSink _logSink;
    private readonly IPacketSerializer _serializer;
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 클라이언트로부터 패킷을 수신했을 때 발생합니다.
    /// (clientId, raw payload)
    /// </summary>
    public event Func<string, byte[], Task>? PacketReceived;

    /// <summary>
    /// 클라이언트 연결이 끊어졌을 때 발생합니다.
    /// </summary>
    public event Func<string, Task>? ClientDisconnected;

    public TcpServerService(ILogSink logSink, IPacketSerializer serializer)
    {
        _logSink = logSink;
        _serializer = serializer;
    }

    public int ConnectedClientCount => _clients.Count;

    /// <summary>
    /// 지정한 포트에서 클라이언트 연결 수락을 시작합니다.
    /// </summary>
    public void Start(int port)
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _logSink.Write($"[Tcp] 서버 시작: 포트={port}");

        _ = AcceptClientsAsync(_cts.Token);
    }

    /// <summary>
    /// 서버를 중지하고 모든 클라이언트 연결을 정리합니다.
    /// </summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();

        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
        _clients.Clear();

        _logSink.Write("[Tcp] 서버 중지");
        await Task.CompletedTask;
    }

    /// <summary>
    /// 모든 연결된 클라이언트에게 패킷을 브로드캐스트합니다.
    /// 전송 실패한 클라이언트는 자동 제거됩니다.
    /// </summary>
    public async Task BroadcastAsync(BasePacket packet)
    {
        var data = _serializer.Serialize(packet);
        var frame = BuildFrame(data);
        var failedClients = new List<string>();

        foreach (var (clientId, connection) in _clients)
        {
            try
            {
                await connection.SendAsync(frame);
            }
            catch
            {
                failedClients.Add(clientId);
            }
        }

        foreach (var clientId in failedClients)
        {
            await RemoveClientAsync(clientId);
        }
    }

    /// <summary>
    /// 특정 클라이언트에게만 패킷을 전송합니다.
    /// </summary>
    public async Task SendToClientAsync(string clientId, BasePacket packet)
    {
        if (!_clients.TryGetValue(clientId, out var connection))
        {
            _logSink.Write($"[Tcp] 클라이언트 찾을 수 없음: {clientId}");
            return;
        }

        try
        {
            var data = _serializer.Serialize(packet);
            var frame = BuildFrame(data);
            await connection.SendAsync(frame);
        }
        catch
        {
            await RemoveClientAsync(clientId);
        }
    }

    /// <summary>
    /// 특정 클라이언트 연결을 강제로 정리합니다.
    /// </summary>
    public Task DisconnectClientAsync(string clientId)
    {
        return RemoveClientAsync(clientId);
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(ct);
                var clientId = Guid.NewGuid().ToString("N")[..8];
                var connection = new ClientConnection(tcpClient);
                _clients.TryAdd(clientId, connection);

                _logSink.Write($"[Tcp] 클라이언트 연결: {clientId} ({tcpClient.Client.RemoteEndPoint})");
                _ = ReceiveFromClientAsync(clientId, connection, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logSink.Write($"[Tcp] 수락 오류: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task ReceiveFromClientAsync(string clientId, ClientConnection connection, CancellationToken ct)
    {
        try
        {
            var stream = connection.GetStream();

            while (!ct.IsCancellationRequested)
            {
                // 4바이트 길이 헤더 읽기
                var headerBuf = new byte[4];
                await ReadExactAsync(stream, headerBuf, ct);
                var length = BitConverter.ToInt32(headerBuf, 0);

                if (length <= 0 || length > MaxPacketSize)
                {
                    _logSink.Write($"[Tcp] 잘못된 패킷 크기: {length} (clientId={clientId})");
                    break;
                }

                // 본문 읽기
                var payload = new byte[length];
                await ReadExactAsync(stream, payload, ct);

                if (PacketReceived is not null)
                {
                    await PacketReceived.Invoke(clientId, payload);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logSink.Write($"[Tcp] 수신 오류: clientId={clientId}, {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await RemoveClientAsync(clientId);
        }
    }

    private async Task RemoveClientAsync(string clientId)
    {
        if (_clients.TryRemove(clientId, out var connection))
        {
            connection.Dispose();
            _logSink.Write($"[Tcp] 클라이언트 제거: {clientId}");

            if (ClientDisconnected is not null)
            {
                await ClientDisconnected.Invoke(clientId);
            }
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                throw new IOException("연결이 종료되었습니다.");
            offset += read;
        }
    }

    /// <summary>
    /// 4바이트 길이 헤더 + 본문으로 구성된 프레임을 만듭니다.
    /// </summary>
    private static byte[] BuildFrame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>
    /// 개별 클라이언트 소켓을 래핑하며, 송신 시 SemaphoreSlim으로 동기화합니다.
    /// </summary>
    private sealed class ClientConnection : IDisposable
    {
        private readonly TcpClient _tcpClient;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public ClientConnection(TcpClient tcpClient)
        {
            _tcpClient = tcpClient;
        }

        public NetworkStream GetStream() => _tcpClient.GetStream();

        public async Task SendAsync(byte[] frame)
        {
            await _sendLock.WaitAsync();
            try
            {
                var stream = _tcpClient.GetStream();
                await stream.WriteAsync(frame);
                await stream.FlushAsync();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _sendLock.Dispose();
            _tcpClient.Dispose();
        }
    }
}
