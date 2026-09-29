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
    private sealed class StubWheelScrollAdapter : IWheelScrollAdapter
    {
        public double CalculateZoomScale(int wheelDelta, double currentZoom) => currentZoom;
    }

    private sealed class StubViewportFitAdapter : IViewportFitAdapter
    {
        public ViewportInfo CalculateFitViewport(Size sourceSize, Size containerSize, FitMode fitMode)
        {
            double scale = Math.Min((double)containerSize.Width / sourceSize.Width, (double)containerSize.Height / sourceSize.Height);
            return new ViewportInfo
            {
                ZoomLevel = scale,
                ViewportSize = containerSize,
                SourceRect = new Rectangle(0, 0, sourceSize.Width, sourceSize.Height)
            };
        }

        public Size CalculateZoomedViewport(Size sourceSize, double zoomLevel) =>
            new Size((int)(sourceSize.Width * zoomLevel), (int)(sourceSize.Height * zoomLevel));

        public bool IsValidViewportPoint(Point point, Size viewportSize) =>
            point.X >= 0 && point.X < viewportSize.Width && point.Y >= 0 && point.Y < viewportSize.Height;
    }

    private sealed class MockReverseSessionManager : IReverseSessionManager
    {
        public ReverseSessionState CurrentState => ReverseSessionState.Connected;
        public bool IsReverseSharingActive => true;
        public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

        public Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<ReverseInvitationPacket> CreateProfessorInvitationAsync(Guid s, Guid sh, string p, Guid c, string pw, DateTimeOffset e, CancellationToken ct = default) =>
            Task.FromResult(new ReverseInvitationPacket { SessionId = s, SharingId = sh, ProfessorId = p, ConnectionId = c, ConnectionString = "rdp://mock", ExpiresAt = e });
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task OnConnectedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task OnConnectionFailedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task OnDisconnectedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopReverseSharingAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ReceiveFrame(byte[] frameData)
        {
            TriggerFrame(frameData);
        }

        public void TriggerFrame(byte[] data) => FrameReceived?.Invoke(this, new FrameReceivedEventArgs { FrameData = data });
    }

    private sealed class MockAnnotationManager : IAnnotationManager
    {
        public bool IsLayerVisible => true;
        public event EventHandler<StrokeRenderedEventArgs>? OnStrokeRendered;
        public event EventHandler<StrokeDispatchedEventArgs>? OnStrokeDispatched;

        public Task AddStrokeAsync(AnnotationStroke stroke, CancellationToken ct = default)
        {
            OnStrokeRendered?.Invoke(this, new StrokeRenderedEventArgs { Stroke = stroke });
            OnStrokeDispatched?.Invoke(this, new StrokeDispatchedEventArgs { Stroke = stroke, TargetParticipantId = "all" });
            return Task.CompletedTask;
        }
        public Task UpdateStrokeAsync(Guid strokeId, AnnotationStroke stroke, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteStrokeAsync(Guid strokeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllStrokesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLayerVisibilityAsync(bool isVisible, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AnnotationStroke>> GetStrokesByParticipantAsync(string p, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AnnotationStroke>>(Array.Empty<AnnotationStroke>());
        public Task<IReadOnlyList<AnnotationStroke>> GetAllStrokesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AnnotationStroke>>(Array.Empty<AnnotationStroke>());
    }

    [Fact]
    public void WdsViewportAdapter_CalculateRenderBounds_ShouldNotDoubleMultiplyScale()
    {
        // 3절: 배율 중복 곱셈 버그 회귀 검증 (1920x1080 -> 960x540)
        var adapter = new wdsViewportAdapter(new StubWheelScrollAdapter(), new StubViewportFitAdapter());
        adapter.SetSourceSize(new Size(1920, 1080));
        adapter.SetViewportSize(new Size(960, 540));
        
        adapter.ApplyFitMode(FitMode.Fit);
        var bounds = adapter.CalculateRenderBounds(new Size(960, 540));

        Assert.Equal(0.5, adapter.CurrentZoom);
        Assert.Equal(960, bounds.Width);
        Assert.Equal(540, bounds.Height);
        Assert.Equal(0, bounds.X);
        Assert.Equal(0, bounds.Y);
    }

    [Fact]
    public void WdsViewportAdapter_DifferentAspectRatio_ShouldCalculateLetterboxAndPillarbox()
    {
        // 3절: 다른 비율 컨테이너(가로/세로 여백) 렌더 바운드 및 중앙 정렬 검증
        var adapter = new wdsViewportAdapter(new StubWheelScrollAdapter(), new StubViewportFitAdapter());
        adapter.SetSourceSize(new Size(1920, 1080));
        adapter.SetZoomLevel(0.5); // 표시 크기: 960x540

        // 1. 가로가 더 넓은 컨테이너 (1200x540) -> 좌우 여백 (Pillarbox: X=120)
        var pillarbox = adapter.CalculateRenderBounds(new Size(1200, 540));
        Assert.Equal(960, pillarbox.Width);
        Assert.Equal(540, pillarbox.Height);
        Assert.Equal(120, pillarbox.X);
        Assert.Equal(0, pillarbox.Y);

        // 2. 세로가 더 긴 컨테이너 (960x700) -> 상하 여백 (Letterbox: Y=80)
        var letterbox = adapter.CalculateRenderBounds(new Size(960, 700));
        Assert.Equal(960, letterbox.Width);
        Assert.Equal(540, letterbox.Height);
        Assert.Equal(0, letterbox.X);
        Assert.Equal(80, letterbox.Y);
    }

    [Fact]
    public void WdsViewportAdapter_TranslateViewportToSource_UnderZoom_MapsAccurately()
    {
        // 3절: 줌 확대(2.0x) 상태에서 뷰포트 클릭 좌표의 원본 역변환 정확도 검증
        var adapter = new wdsViewportAdapter(new StubWheelScrollAdapter(), new StubViewportFitAdapter());
        adapter.SetSourceSize(new Size(1920, 1080));
        adapter.SetZoomLevel(2.0); // 가시 영역 크롭: 960x540
        adapter.SetViewportSize(new Size(960, 540));

        // 뷰포트 중심(480, 270) 클릭 -> 원본 화면의 정중앙(960, 540)으로 사상
        var srcPoint = adapter.TranslateViewportToSource(new Point(480, 270));
        Assert.Equal(960, srcPoint.X);
        Assert.Equal(540, srcPoint.Y);
    }

    [Fact]
    public void ReverseScreenShareAdapter_FrameDimensions_ParsesBmpAndWdsHeaders()
    {
        // 5절: BMP 및 WDS 헤더 바이너리 해상도 파싱 검증
        byte[] bmp = new byte[30];
        bmp[0] = 0x42; bmp[1] = 0x4D;
        BitConverter.GetBytes(1920).CopyTo(bmp, 18);
        BitConverter.GetBytes(1080).CopyTo(bmp, 22);

        Assert.Equal(1920, ReverseScreenShareAdapter.EstimateFrameWidth(bmp));
        Assert.Equal(1080, ReverseScreenShareAdapter.EstimateFrameHeight(bmp));

        byte[] wds = new byte[16];
        wds[0] = 0x57; wds[1] = 0x44; wds[2] = 0x53;
        BitConverter.GetBytes(1280).CopyTo(wds, 4);
        BitConverter.GetBytes(720).CopyTo(wds, 8);

        Assert.Equal(1280, ReverseScreenShareAdapter.EstimateFrameWidth(wds));
        Assert.Equal(720, ReverseScreenShareAdapter.EstimateFrameHeight(wds));
    }

    [Fact]
    public async Task AnnotationEngineAdapter_WhenRendererFails_ShouldNotRaiseRenderedEvent()
    {
        // 2절: 판서 렌더러 실패 시 완료 이벤트 발생 차단 검증
        var mockMgr = new MockAnnotationManager();
        var adapter = new AnnotationEngineAdapter(mockMgr);
        await adapter.ActivateEngineAsync();

        bool eventRaised = false;
        adapter.OnStrokeRendered += (_, _) => eventRaised = true;

        adapter.AddRenderHandler(_ => throw new InvalidOperationException("렌더링 실패 시뮬레이션"));

        var stroke = new AnnotationStroke { StrokeId = Guid.NewGuid() };
        await adapter.ReceiveStrokeAsync(stroke);

        Assert.False(eventRaised, "렌더러 실패 시 OnStrokeRendered 이벤트가 차단되어야 합니다.");
    }

    [Fact]
    public async Task ReverseScreenShareAdapter_WhenDisplayFails_ShouldNotRaiseDisplayedEvent()
    {
        // 2절: 화면 표시 실패 시 FrameDisplayed 이벤트 차단 검증
        var sessionMgr = new MockReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(sessionMgr);
        await adapter.ActivateAdapterAsync();

        bool eventRaised = false;
        adapter.FrameDisplayed += (_, _) => eventRaised = true;

        adapter.AddDisplayHandler((_, _, _) => throw new InvalidOperationException("디스플레이 실패 시뮬레이션"));

        byte[] dummyFrame = new byte[30];
        dummyFrame[0] = 0x42; dummyFrame[1] = 0x4D;
        sessionMgr.ReceiveFrame(dummyFrame);

        Assert.False(eventRaised, "디스플레이 실패 시 FrameDisplayed 이벤트가 차단되어야 합니다.");
    }

    [Fact]
    public async Task WindowsNativeInputPipeline_BlockedTarget_ThrowsAndPreventsInput()
    {
        // 4절: 차단된 대상에 대한 원자적 차단 게이트 검증
        var pipeline = new WindowsNativeInputPipeline();
        await pipeline.ConnectAsync();
        await pipeline.InjectInputAsync("student_1");

        await pipeline.BlockInputAsync("student_1");

        await Assert.ThrowsAsync<InputPipelineException>(() =>
            pipeline.InjectMouseMoveAsync("student_1", 100, 100));
    }

    [Fact]
    public async Task WindowsNativeInputPipeline_Disconnect_ClearsTargetAndThrowsOnInput()
    {
        // 4절: 단절 시 활성 대상 즉시 해제 및 후속 입력 차단 검증
        var pipeline = new WindowsNativeInputPipeline();
        await pipeline.ConnectAsync();
        await pipeline.InjectInputAsync("student_1");

        await pipeline.DisconnectAsync();

        Assert.False(pipeline.IsConnected);
        Assert.Null(pipeline.ActiveTargetId);
        await Assert.ThrowsAsync<InputPipelineException>(() =>
            pipeline.InjectMouseMoveAsync("student_1", 100, 100));
    }
}
