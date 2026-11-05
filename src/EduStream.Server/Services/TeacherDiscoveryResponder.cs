using System.Net;
using System.Net.Sockets;
using EduStream.Core.Network;

namespace EduStream.Server.Services;

/// <summary>열린 교수자 세션만 응답한다. 실패해도 기존 IP 직접 참가에는 영향을 주지 않는다.</summary>
public sealed class TeacherDiscoveryResponder : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public int ListeningPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public TeacherDiscoveryResponder(Guid sessionId, string computerName, int sessionPort, bool passwordRequired,
        Action<Exception>? onFailure = null, int discoveryPort = TeacherDiscoveryProtocol.Port)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
        _loop = RunAsync(sessionId, computerName, sessionPort, passwordRequired, onFailure);
    }
    private async Task RunAsync(Guid sessionId, string name, int port, bool password, Action<Exception>? onFailure)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult incoming;
                try { incoming = await _udp.ReceiveAsync(_stop.Token); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                { continue; }
                if (TeacherDiscoveryProtocol.ReadQuery(incoming.Buffer) is not { } nonce) continue;
                await _udp.SendAsync(TeacherDiscoveryProtocol.Reply(nonce, sessionId, name, port, password), incoming.RemoteEndPoint, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { onFailure?.Invoke(ex); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _udp.Dispose();
        await _loop;
        _stop.Dispose();
    }
}
