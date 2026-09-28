using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EduStream.Core.Logging;

namespace EduStream.Server.Services;

public enum HostAddressKind { Lan, Wireless, Vpn, Virtual, LinkLocal, Loopback }

/// <summary>
/// 교수자에게 보여 줄 접속용 주소 한 건입니다. 종류는 추정값이며 인터넷에서 접속 가능하다는 뜻이 아닙니다.
/// </summary>
public sealed record HostAddressInfo(string InterfaceName, string Address, HostAddressKind Kind)
{
    /// <summary>
    /// 같은 네트워크의 학생이 입력할 후보인지 여부입니다. 루프백·링크 로컬·가상 어댑터는 제외합니다.
    /// VPN은 같은 VPN에 있는 학생만 쓸 수 있으므로 후보에 두되 종류로 구분해 표시합니다.
    /// </summary>
    public bool IsJoinCandidate => Kind is HostAddressKind.Lan or HostAddressKind.Wireless or HostAddressKind.Vpn;

    public string ToEndpoint(int port) => HostNetworkInfoService.FormatEndpoint(Address, port);
}

/// <summary>
/// 2번 구현: U01 접속용 호스트 IP·포트 표시를 위한 주소 목록을 제공합니다. 표시·복사 UI는 5번이 연결합니다.
/// </summary>
/// <remarks>
/// 현재 단계에서는 학생이 직접 입력하는 IPv4 주소만 제공합니다.
/// </remarks>
public sealed class HostNetworkInfoService
{
    private static readonly string[] VpnMarkers =
        { "vpn", "wireguard", "tap-", "tun", "tailscale", "zerotier", "openvpn", "fortinet", "anyconnect" };
    private static readonly string[] VirtualMarkers =
        { "hyper-v", "vethernet", "virtualbox", "vmware", "wsl", "docker", "virtual" };

    private readonly ILogSink _logSink;

    public HostNetworkInfoService(ILogSink logSink)
    {
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
    }

    /// <summary>
    /// 켜져 있는 어댑터의 IPv4 주소를 접속 후보 순서(LAN → 무선 → VPN → 가상 → 링크 로컬 → 루프백)로 반환합니다.
    /// 조회에 실패하면 빈 목록을 반환하고 로그만 남깁니다.
    /// </summary>
    public IReadOnlyList<HostAddressInfo> GetAddresses()
    {
        var result = new List<HostAddressInfo>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                    continue;

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    var kind = Classify(adapter.NetworkInterfaceType, adapter.Name, adapter.Description, unicast.Address);
                    result.Add(new HostAddressInfo(adapter.Name, unicast.Address.ToString(), kind));
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            _logSink.Write($"[Network] 호스트 주소 조회 실패: {ex.GetType().Name}");
            return Array.Empty<HostAddressInfo>();
        }

        return result
            .OrderBy(info => info.Kind)
            .ThenBy(info => info.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static HostAddressKind Classify(NetworkInterfaceType type, string name, string description, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (type == NetworkInterfaceType.Loopback || IPAddress.IsLoopback(address))
            return HostAddressKind.Loopback;
        if (IsLinkLocal(address))
            return HostAddressKind.LinkLocal;

        var label = $"{name} {description}".ToLowerInvariant();
        if (type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp || VpnMarkers.Any(label.Contains))
            return HostAddressKind.Vpn;
        if (VirtualMarkers.Any(label.Contains))
            return HostAddressKind.Virtual;
        if (type == NetworkInterfaceType.Wireless80211)
            return HostAddressKind.Wireless;
        return HostAddressKind.Lan;
    }

    public static string FormatEndpoint(string address, int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        return IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]:{port}"
            : $"{address}:{port}";
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
