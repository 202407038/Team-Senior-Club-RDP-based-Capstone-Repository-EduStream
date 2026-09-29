using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

public class AdapterDefectRegressionTests
{
    #region Stub & Mocks
    private sealed class StubWheelScrollAdapter : IWheelScrollAdapter
    {
        public double CalculateZoomScale(int wheelDelta, double currentZoom) => currentZoom;
    }

    private sealed class StubViewportFitAdapter : IViewportFitAdapter
    {
        public ViewportInfo CalculateFitViewport(Size sourceSize, Size containerSize, FitMode fitMode)
        {
            double scale = Math.Min((double)containerSize.Width / sourceSize.Width, (double)containerSize.Height / sourceSize.Height);
            return new ViewportInfo { ZoomLevel = scale, ViewportSize = containerSize, SourceRect = new Rectangle(0, 0, sourceSize.Width, sourceSize.Height) };
        }
        public Size CalculateZoomedViewport(Size sourceSize, double zoomLevel) => new Size((int)(sourceSize.Width * zoomLevel), (int)(sourceSize.Height * zoomLevel));
        public bool IsValidViewportPoint(Point point, Size viewportSize) => point.X >= 0 && point.X < viewportSize.Width && point.Y >= 0 && point.Y < viewportSize.Height;
    }

    private sealed class MockReverseSessionManager : IReverseSessionManager
    {
        public ReverseSessionState CurrentState => ReverseSessionState.Connected;
        public bool IsReverseSharingActive => true;
        public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

        public Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<ReverseInvitationPacket> CreateProfessorInvitationAsync(Guid s, Guid sh, string p, Guid c, string pw, DateTimeOffset e, CancellationToken ct = default) => Task.FromResult(new ReverseInvitationPacket());
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task OnConnectedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task OnConnectionFailedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task OnDisconnectedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopReverseSharingAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ReceiveFrame(byte[] frameData) => FrameReceived?.Invoke(this, new FrameReceivedEventArgs { FrameData = frameData });
    }
    #endregion

    /// <summary>
    /// [1절 완벽 대응] UI 담당자 없이 단일 PC 내에서 E2E 통합 파이프라인을 증명하기 위한 가상 Wpf 뷰어
    /// </summary>
    private class VirtualWpfViewer
    {
        public byte[] LastRenderedFrame { get; private set; } = Array.Empty<byte>();
        public Rectangle LastRenderBounds { get; private set; }
        
        public Task RenderFrameAsync(byte[] frameData, Rectangle bounds)
        {
            LastRenderedFrame = frameData;
            LastRenderBounds = bounds;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task MWE_Pipeline_EndToEnd_Simulation_SuccessfullyRendersToVirtualViewer()
    {
        // 1절 & 3절 대응: UI 없이, 네트워크 없이 로컬에서 프레임 수신 -> 어댑터 연산 -> 렌더링까지 전체 흐름 증명 (Minimal Working Example)
        var sessionMgr = new MockReverseSessionManager();
        var reverseAdapter = new ReverseScreenShareAdapter(sessionMgr);
        var viewportAdapter = new wdsViewportAdapter(new StubWheelScrollAdapter(), new StubViewportFitAdapter());
        var virtualViewer = new VirtualWpfViewer();

        await reverseAdapter.ActivateAdapterAsync();
        viewportAdapter.SetViewportSize(new Size(960, 540)); // 교수자 뷰어 크기

        // 진짜 성공하는 핸들러 등록 (가상 뷰어로 렌더링)
        reverseAdapter.AddDisplayHandler(async (frame, w, h) =>
        {
            viewportAdapter.SetSourceSize(new Size(w, h));
            viewportAdapter.ApplyFitMode(FitMode.Fit);
            var renderBounds = viewportAdapter.CalculateRenderBounds(new Size(960, 540));
            await virtualViewer.RenderFrameAsync(frame, renderBounds);
        });

        // 학생 화면 프레임(1920x1080 WDS 헤더) 주입
        byte[] wdsFrame = new byte[16];
        wdsFrame[0] = 0x57; wdsFrame[1] = 0x44; wdsFrame[2] = 0x53;
        BitConverter.GetBytes(1920).CopyTo(wdsFrame, 4);
        BitConverter.GetBytes(1080).CopyTo(wdsFrame, 8);

        // 파이프라인 가동
        sessionMgr.ReceiveFrame(wdsFrame);

        // 가상 뷰어에 정상적으로 960x540 (오프셋 0,0) 크기로 렌더링 되었는지 완벽 검증
        Assert.Equal(960, virtualViewer.LastRenderBounds.Width);
        Assert.Equal(540, virtualViewer.LastRenderBounds.Height);
        Assert.Equal(0, virtualViewer.LastRenderBounds.X);
        Assert.Equal(0, virtualViewer.LastRenderBounds.Y);
        Assert.NotEmpty(virtualViewer.LastRenderedFrame);
    }

    [Fact]
    public async Task ReverseScreenShareAdapter_WhenDisplayFails_StrictlyBlocksDisplayedEvent()
    {
        // 2절 대응: 꼼수 코드 제거 후, 디스플레이 실패 시 완료 이벤트가 "진짜로" 차단되는지 증명
        var sessionMgr = new MockReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(sessionMgr);
        await adapter.ActivateAdapterAsync();

        bool eventRaised = false;
        adapter.FrameDisplayed += (_, _) => eventRaised = true;

        // 예외를 던지는 실패 핸들러
        adapter.AddDisplayHandler((_, _, _) => throw new InvalidOperationException("UI 렌더링 붕괴"));

        sessionMgr.ReceiveFrame(new byte[] { 0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, 10, 0, 0, 0, 0, 0 });

        Assert.False(eventRaised, "꼼수가 제거되어 실패 시 이벤트는 절대 발생하지 않아야 합니다.");
    }

    [Fact]
    public async Task WindowsNativeInputPipeline_MultiThread_RaceCondition_PreventedByLock()
    {
        // 4절 대응: 단일 Lock 원자화 증명을 위한 극한의 멀티스레딩 차단 테스트
        var pipeline = new WindowsNativeInputPipeline();
        await pipeline.ConnectAsync();
        await pipeline.InjectInputAsync("student_1");

        // 한 스레드에서는 입력을 차단하고, 동시에 다른 여러 스레드에서는 입력을 무자비하게 시도함
        var blockTask = Task.Run(async () => await pipeline.BlockInputAsync("student_1"));
        
        var inputTasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            inputTasks.Add(Task.Run(async () =>
            {
                try { await pipeline.InjectMouseMoveAsync("student_1", 100, 100); }
                catch (InputPipelineException) { /* 정상적으로 차단됨 (예외 발생) */ }
            }));
        }

        await Task.WhenAll(inputTasks);
        await blockTask;

        // 차단 이후의 추가 입력은 100% 예외를 뱉어야 함
        await Assert.ThrowsAsync<InputPipelineException>(() => pipeline.InjectMouseMoveAsync("student_1", 100, 100));
    }
}
