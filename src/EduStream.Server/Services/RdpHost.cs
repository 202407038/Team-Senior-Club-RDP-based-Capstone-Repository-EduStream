using EduStream.Core.Logging;

namespace EduStream.Server.Services;

/// <summary>
/// 추후 MSTSCLib 또는 RDP 래퍼를 붙일 위치를 명확히 하기 위한 호스트 스텁입니다.
/// </summary>
public sealed class RdpHost
{
    private readonly ILogSink _logSink;

    public RdpHost(ILogSink logSink)
    {
        _logSink = logSink;
    }

    public Task StartHostAsync()
    {
        _logSink.Write("RDP 호스트 시작 준비를 완료했습니다.");
        return Task.CompletedTask;
    }

    public Task StopHostAsync()
    {
        _logSink.Write("RDP 호스트를 정리했습니다.");
        return Task.CompletedTask;
    }
}
