using System.Net;
using EduStream.Core.Collaboration;

namespace EduStream.Core.Network;

/// <summary>
/// 코드 없는 교실 LAN 참가의 주소 계약. 화면 입력/배치나 서버 신원 확인 UI는 담당하지 않습니다.
/// 세션 포트는 기본 5000이며 보호 채널은 기존 규칙대로 세션 포트 + 1을 사용합니다.
/// </summary>
public static class LanSessionEndpoint
{
    public const int DefaultPort = 5000;

    public static IPEndPoint Create(string host, int sessionPort = DefaultPort)
    {
        if (!IPAddress.TryParse(host, out var address) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast ||
            (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
             address.GetAddressBytes()[0] is >= 224 and <= 239) ||
            sessionPort is < 1 or > 65535 - CollaborationPorts.Offset)
            throw new CollaborationException(CollaborationError.InvalidRequest);

        // 루프백은 현 PC 검증용으로 허용합니다. 다른 PC에 안내할 IP 선택은 호스트 정보 서비스 책임입니다.
        return new IPEndPoint(address, sessionPort);
    }
}
