using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EduStream.Core.Network;

namespace EduStream.Client.Services;

public interface ITeacherDiscoveryClient
{
    Task<IReadOnlyList<DiscoveredTeacher>> FindAsync(CancellationToken cancellationToken);
}

public sealed class TeacherDiscoveryClient : ITeacherDiscoveryClient
{
    public Task<IReadOnlyList<DiscoveredTeacher>> FindAsync(CancellationToken cancellationToken) =>
        FindAsync(GetBroadcastTargets(), TimeSpan.FromSeconds(3), cancellationToken);

    public async Task<IReadOnlyList<DiscoveredTeacher>> FindAsync(IEnumerable<IPEndPoint> targets, TimeSpan duration, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var nonce = Guid.NewGuid(); var query = TeacherDiscoveryProtocol.Query(nonce);
        foreach (var target in targets.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await udp.SendAsync(query, target, timeout.Token); }
            catch (SocketException) { /* 일부 어댑터 실패는 다른 후보 검색을 막지 않는다. */ }
        }
        var found = new Dictionary<(Guid, string, int), DiscoveredTeacher>();
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                UdpReceiveResult response;
                try { response = await udp.ReceiveAsync(timeout.Token); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                { continue; } // 종료된 후보의 ICMP 응답 때문에 다른 교수자 검색까지 중단하지 않는다.
                if (TeacherDiscoveryProtocol.ReadReply(response.Buffer, response.RemoteEndPoint, nonce) is { } teacher)
                    found[(teacher.SessionId, teacher.Address, teacher.Port)] = teacher;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        return found.Values.OrderBy(x => x.ComputerName).ThenBy(x => x.Address).ToArray();
    }

    private static IReadOnlyList<IPEndPoint> GetBroadcastTargets()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null) continue;
                var bytes = address.Address.GetAddressBytes(); var mask = address.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < 4; i++) bytes[i] |= (byte)~mask[i];
                addresses.Add(new IPAddress(bytes));
            }
        }
        return addresses.Select(x => new IPEndPoint(x, TeacherDiscoveryProtocol.Port)).ToArray();
    }
}
