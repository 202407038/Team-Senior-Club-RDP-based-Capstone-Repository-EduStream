using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Serialization;

namespace EduStream.Server.Services;

/// <summary>
/// TCP 리스너를 통해 클라이언트 연결을 수락하고,
/// 패킷 수신/송신을 담당하는 네트워크 서비스입니다.
/// </summary>
public sealed class TcpServerService : IDisposable
{
    private readonly IPacketSerializer _serializer;
    private readonly ILogSink _logSink;
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 클라이언트로부터 패킷이 수신되었을 때 발생합니다.
    /// (ClientId, raw JSON string) 형태로 전달됩니다.
    /// </summary>
    public event Func<string, string, Task>? PacketReceived;

    /// <summary>
    /// 클라이언트 연결이 끊어졌을 때 발생합니다.
    /// </summary>
    public event Func<string, Task>? ClientDisconnected;

    public TcpServerService(IPacketSerializer serializer, ILogSink logSink)
    {
        _serializer = serializer;
        _logSink = logSink;
    }

    public int ConnectedClientCount => _clients.Count;

    public IReadOnlyCollection<string> ConnectedClientIds => _clients.Keys.ToList().AsReadOnly();

    /// <summary>
    /// 지정된 포트에서 TCP 리스너를 시작하고 클라이언트 연결을 수락하기 시작합니다.
    /// </summary>
    public Task StartAsync(int port)
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _logSink.Write($"TCP 리스너 시작: 포트={port}");

        _ = AcceptClientsAsync(_cts.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// TCP 리스너를 중지하고 모든 클라이언트 연결을 해제합니다.
    /// </summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();

        foreach (var kvp in _clients)
        {
            kvp.Value.Dispose();
        }

        _clients.Clear();
        _logSink.Write("TCP 리스너 중지: 모든 클라이언트 연결 해제");

        await Task.CompletedTask;
    }

    /// <summary>
    /// 연결된 모든 클라이언트에게 패킷을 전송합니다.
    /// </summary>
    public async Task BroadcastAsync(BasePacket packet)
    {
        var json = JsonSerializer.Serialize<object>(packet, new JsonSerializerOptions { WriteIndented = false });
        var payload = BuildFrame(json);
        var disconnected = new List<string>();

        foreach (var kvp in _clients)
        {
            try
            {
                await kvp.Value.SendAsync(payload);
            }
            catch
            {
                disconnected.Add(kvp.Key);
            }
        }

        foreach (var clientId in disconnected)
        {
            await RemoveClientAsync(clientId);
        }
    }

    /// <summary>
    /// 특정 클라이언트에게만 패킷을 전송합니다.
    /// </summary>
    public async Task SendToClientAsync(string clientId, BasePacket packet)
    {
        if (!_clients.TryGetValue(clientId, out var client))
        {
            _logSink.Write($"클라이언트를 찾을 수 없습니다: {clientId}");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize<object>(packet, new JsonSerializerOptions { WriteIndented = false });
            var payload = BuildFrame(json);
            await client.SendAsync(payload);
        }
        catch
        {
            await RemoveClientAsync(clientId);
        }
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(ct);
                var clientId = Guid.NewGuid().ToString("N")[..8];
                var connection = new ClientConnection(tcpClient, clientId);

                if (_clients.TryAdd(clientId, connection))
                {
                    _logSink.Write($"클라이언트 연결됨: {clientId} ({tcpClient.Client.RemoteEndPoint})");
                    _ = ReceiveFromClientAsync(clientId, connection, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logSink.Write($"클라이언트 수락 오류: {ex.Message}");
                }
            }
        }
    }

    private async Task ReceiveFromClientAsync(string clientId, ClientConnection connection, CancellationToken ct)
    {
        try
        {
            var stream = connection.GetStream();
            var headerBuffer = new byte[4];

            while (!ct.IsCancellationRequested && connection.IsConnected)
            {
                // 4바이트 길이 헤더 읽기
                var headerRead = await ReadExactAsync(stream, headerBuffer, 0, 4, ct);
                if (!headerRead) break;

                var length = BitConverter.ToInt32(headerBuffer, 0);
                if (length <= 0 || length > 10 * 1024 * 1024) // 최대 10MB
                {
                    _logSink.Write($"잘못된 패킷 길이: {length}, 클라이언트={clientId}");
                    break;
                }

                // 본문 읽기
                var bodyBuffer = new byte[length];
                var bodyRead = await ReadExactAsync(stream, bodyBuffer, 0, length, ct);
                if (!bodyRead) break;

                var json = Encoding.UTF8.GetString(bodyBuffer);
                if (PacketReceived is not null)
                {
                    await PacketReceived.Invoke(clientId, json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
        catch (Exception ex)
        {
            _logSink.Write($"클라이언트 수신 오류: {clientId}, {ex.Message}");
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
            _logSink.Write($"클라이언트 연결 해제: {clientId}");

            if (ClientDisconnected is not null)
            {
                await ClientDisconnected.Invoke(clientId);
            }
        }
    }

    /// <summary>
    /// 길이 헤더(4바이트) + JSON 본문으로 구성된 프레임을 만듭니다.
    /// </summary>
    private static byte[] BuildFrame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes(body.Length);
        var frame = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        Buffer.BlockCopy(body, 0, frame, header.Length, body.Length);
        return frame;
    }

    /// <summary>
    /// 스트림에서 정확히 count 바이트를 읽습니다.
    /// </summary>
    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
            if (read == 0) return false; // 연결 끊김
            totalRead += read;
        }
        return true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _listener?.Stop();

        foreach (var kvp in _clients)
        {
            kvp.Value.Dispose();
        }

        _clients.Clear();
    }

    /// <summary>
    /// 개별 클라이언트 연결을 래핑합니다.
    /// </summary>
    private sealed class ClientConnection : IDisposable
    {
        private readonly TcpClient _tcpClient;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public ClientConnection(TcpClient tcpClient, string clientId)
        {
            _tcpClient = tcpClient;
            ClientId = clientId;
        }

        public string ClientId { get; }

        public bool IsConnected => _tcpClient.Connected;

        public NetworkStream GetStream() => _tcpClient.GetStream();

        public async Task SendAsync(byte[] data)
        {
            await _sendLock.WaitAsync();
            try
            {
                var stream = _tcpClient.GetStream();
                await stream.WriteAsync(data);
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
