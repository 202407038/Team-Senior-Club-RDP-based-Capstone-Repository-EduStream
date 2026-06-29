using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Serialization;
using EduStream.Core.Utils;

namespace EduStream.Client.Services;

/// <summary>
/// TCP 클라이언트: 서버에 연결하고 길이-헤더 프레이밍 프로토콜로 패킷을 송수신합니다.
/// TcpServerService의 클라이언트 대응입니다.
/// </summary>
public sealed class TcpClientService : IDisposable
{
    private const int MaxPacketSize = 10 * 1024 * 1024; // 10MB

    private readonly ILogSink _logSink;
    private readonly IPacketSerializer _serializer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 서버로부터 패킷을 수신했을 때 발생합니다.
    /// (packetType, raw payload)
    /// </summary>
    public event Func<PacketType, byte[], Task>? PacketReceived;

    /// <summary>
    /// 서버 연결이 끊어졌을 때 발생합니다.
    /// </summary>
    public event Func<string, Task>? Disconnected;

    public TcpClientService(ILogSink logSink, IPacketSerializer serializer)
    {
        _logSink = logSink;
        _serializer = serializer;
    }

    public bool IsConnected => _tcpClient?.Connected == true;

    /// <summary>
    /// 서버에 TCP 연결을 수립하고 수신 루프를 시작합니다.
    /// </summary>
    public async Task ConnectAsync(string host, int port)
    {
        _cts = new CancellationTokenSource();
        _tcpClient = new TcpClient();

        await _tcpClient.ConnectAsync(host, port);
        _stream = _tcpClient.GetStream();

        _logSink.Write($"서버 연결 완료: {host}:{port}");

        _ = ReceiveLoopAsync(_cts.Token);
    }

    /// <summary>
    /// 서버 연결을 종료합니다.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        _stream?.Dispose();
        _stream = null;

        _tcpClient?.Dispose();
        _tcpClient = null;

        _logSink.Write("서버 연결 종료");
        await Task.CompletedTask;
    }

    /// <summary>
    /// 서버에 패킷을 전송합니다.
    /// </summary>
    public async Task SendAsync(BasePacket packet)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("서버에 연결되어 있지 않습니다.");
        }

        var data = _serializer.Serialize(packet);
        var frame = BuildFrame(data);

        await _sendLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(frame);
            await _stream.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                // 4바이트 길이 헤더 읽기
                var headerBuf = new byte[4];
                await ReadExactAsync(_stream, headerBuf, ct);
                var length = BitConverter.ToInt32(headerBuf, 0);

                if (length <= 0 || length > MaxPacketSize)
                {
                    _logSink.Write($"잘못된 패킷 크기 수신: {length}");
                    break;
                }

                // 본문 읽기
                var payload = new byte[length];
                await ReadExactAsync(_stream, payload, ct);

                // MessageType 추출
                var packetType = ExtractPacketType(payload);

                if (PacketReceived is not null)
                {
                    await PacketReceived.Invoke(packetType, payload);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logSink.Write($"서버 수신 오류: {ex.Message}");
        }
        finally
        {
            if (Disconnected is not null)
            {
                await Disconnected.Invoke("연결이 종료되었습니다.");
            }
        }
    }

    private static PacketType ExtractPacketType(byte[] payload)
    {
        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(payload);
            if (element.TryGetProperty("MessageType", out var mt))
            {
                var packetType = (PacketType)mt.GetInt32();
                PacketContractUtility.ValidatePacketType(packetType);
                return packetType;
            }
        }
        catch { }

        return PacketType.Unknown;
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

    private static byte[] BuildFrame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
        payload.CopyTo(frame, 4);
        return frame;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _sendLock.Dispose();
        _stream?.Dispose();
        _tcpClient?.Dispose();
    }
}
