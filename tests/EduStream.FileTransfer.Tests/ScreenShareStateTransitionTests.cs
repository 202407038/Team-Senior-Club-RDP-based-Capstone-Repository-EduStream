using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// ScreenShareService.State의 시작, 실패, 정상 복구, 중지 상태 전환을 검증합니다.
/// (8월 1주차 3번: 화면 송신 상태를 대기, 송신 중, 실패, 중지로 구분)
/// </summary>
public sealed class ScreenShareStateTransitionTests
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    [Fact]
    public void State_Initial_IsIdle()
    {
        var log = new InMemoryLogSink();
        var tcpServer = new TcpServerService(log, new PacketSerializer());
        var sessionManager = new SessionManager(log, tcpServer);
        var service = new ScreenShareService(sessionManager, log);

        Assert.Equal(OperationState.Idle, service.State);
    }

    [Fact]
    public async Task State_StartContinuousBroadcast_ChangesToInProgress()
    {
        var log = new InMemoryLogSink();
        var tcpServer = new TcpServerService(log, new PacketSerializer());
        var sessionManager = new SessionManager(log, tcpServer);
        var service = new ScreenShareService(sessionManager, log);

        await service.StartContinuousBroadcastAsync();

        Assert.Equal(OperationState.InProgress, service.State);

        await service.StopContinuousBroadcastAsync();
    }

    [Fact]
    public async Task State_StopContinuousBroadcast_ChangesToStopped()
    {
        var log = new InMemoryLogSink();
        var tcpServer = new TcpServerService(log, new PacketSerializer());
        var sessionManager = new SessionManager(log, tcpServer);
        var service = new ScreenShareService(sessionManager, log);

        await service.StartContinuousBroadcastAsync();
        await service.StopContinuousBroadcastAsync();

        Assert.Equal(OperationState.Stopped, service.State);
    }

    [Fact]
    public async Task State_TransmissionFailure_ChangesToFailed()
    {
        var log = new InMemoryLogSink();
        var serializer = new ControllablePacketSerializer { FailScreenPackets = true };
        var tcpServer = new TcpServerService(log, serializer);
        var sessionManager = new SessionManager(log, tcpServer);
        var service = CreateService(sessionManager, log);

        try
        {
            await service.StartContinuousBroadcastAsync();
            await WaitUntilAsync(() => service.State == OperationState.Failed, DefaultWait);

            Assert.Equal(OperationState.Failed, service.State);
            Assert.Contains("화면 프레임 전송 실패", service.LatestStatus);
            Assert.Equal(0, service.BroadcastFrameCount);
        }
        finally
        {
            await service.StopContinuousBroadcastAsync();
        }
    }

    [Fact]
    public async Task State_RecoveryAfterFailure_ChangesToInProgress()
    {
        var log = new InMemoryLogSink();
        var serializer = new ControllablePacketSerializer { FailScreenPackets = true };
        var tcpServer = new TcpServerService(log, serializer);
        var sessionManager = new SessionManager(log, tcpServer);
        var service = CreateService(sessionManager, log);

        try
        {
            await service.StartContinuousBroadcastAsync();
            await WaitUntilAsync(() => service.State == OperationState.Failed, DefaultWait);

            serializer.FailScreenPackets = false;
            await WaitUntilAsync(
                () => service.State == OperationState.InProgress && service.BroadcastFrameCount > 0,
                DefaultWait);

            Assert.Equal(OperationState.InProgress, service.State);
            Assert.True(service.BroadcastFrameCount > 0);
        }
        finally
        {
            await service.StopContinuousBroadcastAsync();
        }
    }

    private static ScreenShareService CreateService(SessionManager sessionManager, ILogSink log)
    {
        return new ScreenShareService(
            sessionManager,
            log,
            new ScreenCaptureSettings
            {
                TargetFrameIntervalMilliseconds = ScreenTransferRules.MinimumFrameIntervalMilliseconds
            });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"조건이 {timeout.TotalSeconds:F1}초 안에 충족되지 않았습니다.");
    }

    private sealed class ControllablePacketSerializer : IPacketSerializer
    {
        private readonly PacketSerializer _inner = new();
        private int _failScreenPackets;

        public bool FailScreenPackets
        {
            get => Volatile.Read(ref _failScreenPackets) == 1;
            set => Volatile.Write(ref _failScreenPackets, value ? 1 : 0);
        }

        public byte[] Serialize<TPacket>(TPacket packet) where TPacket : BasePacket
        {
            if (packet is ScreenPacket && FailScreenPackets)
            {
                throw new IOException("화면 프레임 전송 실패 테스트");
            }

            return _inner.Serialize(packet);
        }

        public TPacket? Deserialize<TPacket>(byte[] payload) where TPacket : BasePacket
        {
            return _inner.Deserialize<TPacket>(payload);
        }
    }
}
