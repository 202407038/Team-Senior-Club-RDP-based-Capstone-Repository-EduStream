using System.Net;
using System.Net.Sockets;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 세션 포트와 보호 채널 포트(+1)를 함께 쓰는 테스트용 포트 쌍입니다.
/// 다른 테스트가 포트 0으로 받는 OS 동적 범위(49152 이상)와 겹치지 않는 구간에서 골라,
/// 병렬 테스트가 미리 골라 둔 포트를 가로채지 않게 합니다.
/// </summary>
internal static class TestPortAllocator
{
    private const int RangeStart = 20000;
    private const int RangeEnd = 40000;
    private static int _next = RangeStart + Random.Shared.Next(0, (RangeEnd - RangeStart) / 2) * 2;

    public static int GetFreePortPair()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = Interlocked.Add(ref _next, 2) - 2;
            if (port + 1 >= RangeEnd)
            {
                Interlocked.CompareExchange(ref _next, RangeStart, port + 2);
                continue;
            }
            if (IsFree(port) && IsFree(port + 1)) return port;
        }
        throw new InvalidOperationException("빈 포트 쌍을 찾지 못했습니다.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
