using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using EduStream.Server.Rdp;
using Xunit;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfMedia = System.Windows.Media;
using WpfShapes = System.Windows.Shapes;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 3번(화면/RDP) 어댑터 결함 회귀 테스트.
///
/// - [Fact] 계열: 외부 엔진 없이 도는 단위 수준 검증. 파일 안에서 Stub/Mock 을 쓰는 곳은 모두 여기이며,
///   E2E 로 표기하지 않습니다.
/// - [WdsFact] 계열(EDUSTREAM_WDS_SMOKE=1): 실제 WDS 세션/실제 AxRDPViewer/실제 화면 픽셀로 검증합니다.
///   상태값 강제 주입, 이벤트 강제 호출, 가짜 뷰어는 사용하지 않습니다.
/// </summary>
[Collection("WDS integration")]
public class AdapterDefectRegressionTests
{
    // 같은 "WDS integration" 컬렉션 안에서는 테스트가 직렬 실행되므로 정적 출력 핸들로 충분하다.
    private static Xunit.Abstractions.ITestOutputHelper? s_output;

    public AdapterDefectRegressionTests(Xunit.Abstractions.ITestOutputHelper output) => s_output = output;

    private static void Log(string message) => s_output?.WriteLine(message);

    #region 단위 테스트 전용 Stub & Mock (E2E 에서는 사용하지 않음)
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

    /// <summary>
    /// SmartSizing 속성을 가진 "실제 WinForms Control". (어댑터 단위 검증 전용 — dynamic 접근이 가능하도록 public)
    /// </summary>
    public sealed class SmartSizingSurface : Forms.Panel
    {
        public bool SmartSizing { get; set; }
    }
    #endregion

    // ════════════════════════════════════════════════════════════════════
    // 단위 수준: 역방향 초대 계약 (피드백 2)
    // ════════════════════════════════════════════════════════════════════
    #region 역방향 초대 DTO 계약

    private static ReverseInvitationPacket NewSamplePacket() => new()
    {
        SessionId = Guid.NewGuid(),
        SharingId = Guid.NewGuid(),
        InvitationId = Guid.NewGuid(),
        ConnectionId = Guid.NewGuid(),
        ProfessorId = "prof-01",
        HostStudentId = "stu-01",
        ConnectionString = "<E><A KH=\"sample\" ID=\"sample\"/></E>",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        ControlMode = ReverseControlMode.HostGrantedInteractive
    };

    [Fact]
    public void ReverseInvitationWire_JsonRoundTrip_PreservesIdentityGuidsAndInteractiveContract()
    {
        var packet = NewSamplePacket();

        var json = ReverseInvitationWire.From(packet).ToJson();
        var restored = ReverseInvitationWire.FromJson(json);

        restored.Validate(packet.SessionId, "prof-01", packet.ConnectionId, DateTimeOffset.UtcNow, "stu-01");

        Assert.Equal(packet.SessionId, restored.SessionId);
        Assert.Equal(packet.SharingId, restored.SharingId);
        Assert.Equal(packet.InvitationId, restored.InvitationId);
        Assert.Equal(packet.ConnectionId, restored.ConnectionId);
        Assert.Equal("prof-01", restored.ProfessorId);
        Assert.Equal("stu-01", restored.StudentId);
        Assert.Equal("prof-01", restored.ParticipantId);
        Assert.Equal(packet.ConnectionString, restored.ConnectionString);
        Assert.Equal(packet.ExpiresAt, restored.ExpiresAt);
        Assert.Equal(ReverseControlMode.HostGrantedInteractive, restored.ControlMode);
        Assert.False(restored.ViewOnly);

        var back = restored.ToPacket();
        Assert.Equal(packet.SessionId, back.SessionId);
        Assert.Equal(packet.SharingId, back.SharingId);
        Assert.Equal(packet.InvitationId, back.InvitationId);
        Assert.Equal(packet.ConnectionId, back.ConnectionId);
        Assert.Equal(packet.ProfessorId, back.ProfessorId);
        Assert.Equal(packet.HostStudentId, back.HostStudentId);
        Assert.Equal(packet.ConnectionString, back.ConnectionString);
        Assert.Equal(packet.ExpiresAt, back.ExpiresAt);
        Assert.Equal(packet.ControlMode, back.ControlMode);
    }

    [Fact]
    public void ReverseInvitationWire_MissingOrInconsistentFields_AreRejectedBeforeConnect()
    {
        var packet = NewSamplePacket();
        var json = ReverseInvitationWire.From(packet).ToJson();
        string Mutate(Action<JsonObject> edit)
        {
            var node = JsonNode.Parse(json)!.AsObject();
            edit(node);
            return node.ToJsonString();
        }

        // 필수 항목이 JSON 에서 유실되면 역직렬화 단계에서 즉시 실패 (조용히 빈 값으로 채워지지 않음)
        Assert.Throws<JsonException>(() => ReverseInvitationWire.FromJson(Mutate(n => n.Remove("ProfessorId"))));
        Assert.Throws<JsonException>(() => ReverseInvitationWire.FromJson(Mutate(n => n.Remove("StudentId"))));
        Assert.Throws<JsonException>(() => ReverseInvitationWire.FromJson(Mutate(n => n.Remove("InvitationId"))));
        Assert.Throws<JsonException>(() => ReverseInvitationWire.FromJson(Mutate(n => n.Remove("ConnectionString"))));

        // 값이 비었거나 모순되면 접속 이전 검증에서 거부
        var now = DateTimeOffset.UtcNow;
        void AssertRejected(string mutated) => Assert.Throws<ArgumentException>(() =>
            ReverseInvitationWire.FromJson(mutated).Validate(packet.SessionId, "prof-01", packet.ConnectionId, now, "stu-01"));

        AssertRejected(Mutate(n => n["InvitationId"] = Guid.Empty.ToString()));
        AssertRejected(Mutate(n => n["SharingId"] = Guid.Empty.ToString()));
        AssertRejected(Mutate(n => n["ProfessorId"] = ""));
        AssertRejected(Mutate(n => n["ParticipantId"] = "someone-else"));
        AssertRejected(Mutate(n => n["ViewOnly"] = true)); // 제어 모드와 모순
        AssertRejected(Mutate(n => n["DataLength"] = 1));

        // 기대 값과 다른 세션/교수자/학생/만료
        var wire = ReverseInvitationWire.FromJson(json);
        Assert.Throws<ArgumentException>(() => wire.Validate(Guid.NewGuid(), "prof-01", packet.ConnectionId, now, "stu-01"));
        Assert.Throws<ArgumentException>(() => wire.Validate(packet.SessionId, "prof-99", packet.ConnectionId, now, "stu-01"));
        Assert.Throws<ArgumentException>(() => wire.Validate(packet.SessionId, "prof-01", packet.ConnectionId, now, "stu-99"));
        Assert.Throws<ArgumentException>(() => wire.Validate(packet.SessionId, "prof-01", packet.ConnectionId, packet.ExpiresAt.AddSeconds(1), "stu-01"));
    }

    [Fact]
    public void ForwardContract_StillRejectsNonViewOnlyPackets_SoReverseUsesItsOwnContract()
    {
        var session = Guid.NewGuid();
        var connection = Guid.NewGuid();
        EduStream.Core.Models.RdpInvitationPacket Build(bool viewOnly) => new()
        {
            SessionId = session,
            ParticipantId = "Alice",
            ConnectionId = connection,
            SharingId = Guid.NewGuid(),
            InvitationId = Guid.NewGuid(),
            ConnectionString = "test",
            DataLength = 4,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            ViewOnly = viewOnly
        };

        // 정방향(교수자→학생) 보기 전용 계약은 그대로 통과
        EduStream.Core.Utils.RdpInvitationContract.Validate(Build(true), session, "Alice", connection, DateTimeOffset.UtcNow);

        // ViewOnly=false 를 정방향 패킷에 끼워 넣는 방식은 계약 위반으로 계속 거부 → 역방향은 별도 계약(ReverseInvitationWire) 사용
        var ex = Assert.Throws<ArgumentException>(() =>
            EduStream.Core.Utils.RdpInvitationContract.Validate(Build(false), session, "Alice", connection, DateTimeOffset.UtcNow));
        Assert.Contains("보기 전용", ex.Message);
        Assert.True(new EduStream.Core.Models.RdpInvitationPacket().ViewOnly);
    }
    #endregion

    // ════════════════════════════════════════════════════════════════════
    // 실제 WDS: 7단계 생명주기 E2E (피드백 3)
    // ════════════════════════════════════════════════════════════════════
    #region E2E 7단계

    /// <summary>
    /// 1) 학생 공유 세션 생성 → 2) 초대 생성·전달 → 3) 교수 뷰어 접속 요청 → 4) 실제 OnAttendeeConnected 승인
    /// → 5) 실제 화면 픽셀 표시 → 6) 제어 허용(ControlLevel=3)과 입력 → 7) 회수·종료·자원 정리
    /// </summary>
    [WdsFact]
    public Task Reverse_SevenStepLifecycle_StudentHost_ToProfessorViewer_UsesOnlyRealNativeEvents()
        => RunOnStaAsync(RunSevenStepLifecycleAsync);

    private static async Task RunSevenStepLifecycleAsync()
    {
        await using var ctx = new ReverseE2EContext();
        var pipeline = new WindowsNativeInputPipeline(); // 실제 Win32 입력 엔진
        var originalCursor = Forms.Cursor.Position;

        try
        {
            await pipeline.ConnectAsync();

            // 2번 IRemoteInputGate 계약 → 3번 WDS Grant/Revoke + (선택) OS 입력 파이프라인.
            // 이벤트 구독으로 대체하지 않는다. 뷰어발 WDS 입력 경로는 이 게이트가 열지 않는다.
            var gate = new ReverseWdsRemoteInputGate(
                ctx.Host,
                _ => ReverseE2EContext.ProfessorId,
                pipeline);

            // ── 1~5단계 ──
            await ctx.Step1_StartStudentSharingAsync();
            await ctx.Step2_CreateAndDeliverInvitationAsync();
            ctx.Step3_ProfessorRequestsConnection();
            await ctx.Step4_AwaitNativeApprovalAsync();
            await ctx.Step5_AwaitRealDisplayAsync();

            var controlState = CreateGateState(ctx.SessionId);

            // ── 6단계: 제어 권한 부여와 입력 ──
            // 6-a) 호스트 허용 없이 교수자 뷰어가 제어를 요청해도 ControlLevel 은 올라가지 않는다.
            ctx.Viewer!.RequestInteractiveControl();
            await Task.Delay(1500);
            Assert.Equal(ReverseSessionManager.ControlLevelView,
                await ctx.Host.GetAttendeeControlLevelAsync(ReverseE2EContext.ProfessorId));
            Assert.Equal(ReverseSessionState.Connected, ctx.Host.CurrentState);
            await Assert.ThrowsAsync<InputPipelineException>(() =>
                pipeline.InjectMouseMoveAsync(ReverseE2EContext.ProfessorId, originalCursor.X, originalCursor.Y));

            // 6-b) IRemoteInputGate.GrantAsync → 실제 호스트 COM 의 ControlLevel 이 3 으로 읽힌다.
            await gate.GrantAsync(controlState, CancellationToken.None);
            var granted = await ctx.Events.WaitAsync(ReverseE2EContext.NativeEventTimeout, ReverseAttendeeEventKind.ControlGranted);
            Assert.Equal(ReverseSessionManager.ControlLevelInteractive, granted.ControlLevel);
            Assert.Equal(ReverseSessionManager.ControlLevelInteractive,
                await ctx.Host.GetAttendeeControlLevelAsync(ReverseE2EContext.ProfessorId));
            Assert.Equal(ReverseSessionState.ControlGranted, ctx.Host.CurrentState);
            Assert.Equal(ReverseE2EContext.ProfessorId, pipeline.ActiveTargetId);

            // 6-c) 승인된 대상의 입력은 실제 OS 커서를 움직이고, 승인되지 않은 대상은 차단된다.
            var target = PickOffsetPoint(originalCursor);
            await pipeline.InjectMouseMoveAsync(ReverseE2EContext.ProfessorId, target.X, target.Y);
            AssertCursorNear(target);

            await Assert.ThrowsAsync<InputPipelineException>(() =>
                pipeline.InjectMouseMoveAsync("intruder", originalCursor.X, originalCursor.Y));
            AssertCursorNear(target);

            // ── 7단계: 권한 회수 → 추가 입력 차단 → 종료 → 자원 정리 ──
            await gate.RevokeAsync(controlState.Revoke(), CancellationToken.None);
            var revoked = await ctx.Events.WaitAsync(ReverseE2EContext.NativeEventTimeout, ReverseAttendeeEventKind.ControlRevoked);
            Assert.Equal(ReverseSessionManager.ControlLevelView, revoked.ControlLevel);
            Assert.Equal(ReverseSessionManager.ControlLevelView,
                await ctx.Host.GetAttendeeControlLevelAsync(ReverseE2EContext.ProfessorId));
            Assert.Equal(ReverseSessionState.Connected, ctx.Host.CurrentState);
            Assert.Null(pipeline.ActiveTargetId);

            var cursorAfterRevoke = Forms.Cursor.Position;
            await Assert.ThrowsAsync<InputPipelineException>(() =>
                pipeline.InjectMouseMoveAsync(ReverseE2EContext.ProfessorId, originalCursor.X, originalCursor.Y));
            AssertCursorNear(cursorAfterRevoke); // 회수 뒤 입력 시도는 실제 커서를 움직이지 못한다.

            await ctx.Host.StopReverseSharingAsync();
            await ctx.Viewer.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(10)); // 뷰어도 실제로 끊겼다.

            Assert.Equal(ReverseSessionState.Inactive, ctx.Host.CurrentState);
            Assert.False(ctx.Host.IsReverseSharingActive);
            Assert.Equal(0, ctx.Host.PendingInvitationCount);  // ConnectionString 매핑 정리
            Assert.Equal(0, ctx.Host.ActiveAttendeeCount);     // 승인 참석자 정리
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ctx.Host.GrantControlAsync(ReverseE2EContext.ProfessorId)); // 종료 후 제어 부여 불가
        }
        finally
        {
            await pipeline.DisconnectAsync();
            Forms.Cursor.Position = originalCursor;
        }
    }

    /// <summary>뷰어가 먼저 끊으면 호스트가 실제 OnAttendeeDisconnected 로 매핑·권한을 정리해야 한다.</summary>
    [WdsFact]
    public Task Reverse_ViewerDisconnect_RaisesNativeDisconnectEvent_AndCleansMappings()
        => RunOnStaAsync(async () =>
        {
            await using var ctx = new ReverseE2EContext();
            await ctx.Step1_StartStudentSharingAsync();
            await ctx.Step2_CreateAndDeliverInvitationAsync();
            ctx.Step3_ProfessorRequestsConnection();
            await ctx.Step4_AwaitNativeApprovalAsync();

            ctx.Viewer!.Viewer.Disconnect();

            var gone = await ctx.Events.WaitAsync(ReverseE2EContext.NativeEventTimeout, ReverseAttendeeEventKind.Disconnected);
            Assert.Equal(ReverseE2EContext.ProfessorId, gone.ProfessorId);
            Assert.Equal(ReverseSessionState.Disconnected, ctx.Host.CurrentState);
            Assert.Equal(0, ctx.Host.ActiveAttendeeCount);
            Assert.Equal(0, ctx.Host.PendingInvitationCount);

            await ctx.Host.StopReverseSharingAsync();
            Assert.Equal(ReverseSessionState.Inactive, ctx.Host.CurrentState);
        });
    #endregion

    // ════════════════════════════════════════════════════════════════════
    // 실제 WDS: 거부 경로 · 재접속 (피드백 7)
    // 잘못된 초대 / 만료 / 승인되지 않은 접속자 / 소진된 초대 / 종료 후 재접속
    // ════════════════════════════════════════════════════════════════════
    #region 거부 경로·재접속

    /// <summary>접속 시도 전용 보조 뷰어(픽셀 검증용이 아니므로 작게 띄운다)</summary>
    private static ProfessorViewerHarness NewAttemptViewer(ReverseE2EContext ctx)
    {
        var area = ctx.Layout.Annotation;
        var viewer = new ProfessorViewerHarness(new Wpf.Rect(area.Left, area.Top, 320, 240));
        viewer.Show();
        ctx.RetainAttempt(viewer);
        return viewer;
    }

    /// <summary>접속 시도. COM 이 호출 즉시 거부하면 그 사유를, 아니면 null 을 돌려준다.</summary>
    private static string? TryConnect(ProfessorViewerHarness viewer, string connectionString, string name, string password)
    {
        try
        {
            viewer.Connect(connectionString, name, password);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 잘못된 접속 시도는 (a) 호출 즉시 거부되거나 (b) WDS 가 연결 실패를 알리거나 (c) 호스트가 승인하지 않고 끊어야 한다.
    /// 연결된 채로 남아 있으면 실패다. 어느 계층에서 거부됐는지를 문자열로 돌려준다.
    /// </summary>
    private static async Task<string> AssertAttemptIsRefusedAsync(ProfessorViewerHarness viewer, string? synchronousError, string what)
    {
        string layer;
        if (synchronousError != null)
        {
            layer = $"호출 즉시 거부({synchronousError})";
        }
        else
        {
            var finished = await Task.WhenAny(viewer.Failed.Task, viewer.Terminated.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (finished == viewer.Failed.Task) layer = $"WDS 연결 실패 이벤트({viewer.Failed.Task.Result}), 연결 확립 여부={viewer.Established.Task.IsCompleted}";
            else if (finished == viewer.Terminated.Task) layer = $"호스트가 연결을 끊음(OnConnectionTerminated), 연결 확립 여부={viewer.Established.Task.IsCompleted}";
            else throw new Xunit.Sdk.XunitException($"{what}: 30초가 지나도 거부/종료되지 않고 연결 시도가 남아 있습니다.");
        }

        Log($"[거부 확인] {what} → {layer}");
        return layer;
    }

    /// <summary>거부 COM 콜백이 스택에서 빠진 뒤에만 다음 접속을 연다. 뷰어 해제는 공유 세션 Stop 이후.</summary>
    private static async Task DisposeRefusedViewerAsync(ProfessorViewerHarness viewer)
    {
        _ = viewer;
        await Dispatcher.Yield();
    }

    private static void AssertNoExtraApproval(ReverseE2EContext ctx, int expectedApprovals, int expectedActive, string what)
    {
        Log($"[호스트 이벤트] {what}: [{ctx.Events.Describe()}] / 활성 {ctx.Host.ActiveAttendeeCount}, 매핑 {ctx.Host.PendingInvitationCount}, 상태 {ctx.Host.CurrentState}");
        Assert.True(expectedApprovals == ctx.Events.Count(ReverseAttendeeEventKind.Approved),
            $"{what}: 승인 이벤트 수가 기대({expectedApprovals})와 다릅니다. 실제={ctx.Events.Count(ReverseAttendeeEventKind.Approved)}");
        Assert.Equal(expectedActive, ctx.Host.ActiveAttendeeCount);
    }

    /// <summary>잘못된 비밀번호 / 변조된 연결 문자열은 승인되지 않으며, 유효한 초대는 소모되지 않는다.</summary>
    [WdsFact]
    public Task Reverse_InvalidInvitationAttempts_AreNeverApproved_AndDoNotConsumeTheValidInvitation()
        => RunOnStaAsync(async () =>
        {
            await using var ctx = new ReverseE2EContext();
            await ctx.Step1_StartStudentSharingAsync();
            await ctx.Step2_CreateAndDeliverInvitationAsync();
            var validString = ctx.Invitation!.ConnectionString;

            // (a) 올바른 연결 문자열 + 틀린 비밀번호 — 거부 후 정리까지 확인
            {
                var wrongPassword = NewAttemptViewer(ctx);
                var error = TryConnect(wrongPassword, validString, ReverseE2EContext.ProfessorId, "wrong-" + ctx.Password);
                await AssertAttemptIsRefusedAsync(wrongPassword, error, "틀린 비밀번호");
                await DisposeRefusedViewerAsync(wrongPassword);
            }
            AssertNoExtraApproval(ctx, 0, 0, "틀린 비밀번호");

            // (b) 변조된 연결 문자열 (올바른 비밀번호)
            Assert.Contains("ID=\"", validString);
            var tampered = validString.Replace("ID=\"", "ID=\"00", StringComparison.Ordinal);
            Assert.NotEqual(validString, tampered);
            {
                var tamperedViewer = NewAttemptViewer(ctx);
                var error = TryConnect(tamperedViewer, tampered, ReverseE2EContext.ProfessorId, ctx.Password);
                await AssertAttemptIsRefusedAsync(tamperedViewer, error, "변조된 연결 문자열");
                await DisposeRefusedViewerAsync(tamperedViewer);
            }
            AssertNoExtraApproval(ctx, 0, 0, "변조된 연결 문자열");

            Assert.Equal(ReverseSessionState.Hosting, ctx.Host.CurrentState);
            Assert.Equal(1, ctx.Host.PendingInvitationCount); // 실패한 시도가 유효한 초대를 소모하지 않았다

            // (c) 그 뒤에도 정상 교수자는 같은 초대로 승인된다.
            ctx.Step3_ProfessorRequestsConnection();
            await ctx.Step4_AwaitNativeApprovalAsync();
            AssertNoExtraApproval(ctx, 1, 1, "정상 접속");
        });

    /// <summary>만료된 초대는 교수자 쪽 사전 검증에서 거부되고, 호스트는 매핑을 정리하며, 접속해도 승인되지 않는다.</summary>
    [WdsFact]
    public Task Reverse_ExpiredInvitation_IsRejectedBeforeConnect_AndNeverApproved()
        => RunOnStaAsync(async () =>
        {
            await using var ctx = new ReverseE2EContext();
            await ctx.Step1_StartStudentSharingAsync();
            await ctx.Step2_CreateAndDeliverInvitationAsync(TimeSpan.FromSeconds(4));
            var received = ctx.Received!;

            // 만료 직전까지는 유효, 만료 후에는 접속 이전 검증에서 거부된다.
            received.Validate(ctx.SessionId, ReverseE2EContext.ProfessorId, ctx.ConnectionId, DateTimeOffset.UtcNow, ReverseE2EContext.StudentId);
            await WaitUntilAsync(() => DateTimeOffset.UtcNow > received.ExpiresAt, TimeSpan.FromSeconds(10), () => "만료 시각이 지나지 않았습니다.");
            Assert.Throws<ArgumentException>(() =>
                received.Validate(ctx.SessionId, ReverseE2EContext.ProfessorId, ctx.ConnectionId, DateTimeOffset.UtcNow, ReverseE2EContext.StudentId));

            // 호스트는 만료된 초대의 매핑을 정리하고 WDS 초대를 폐기한다.
            await WaitUntilAsync(() => ctx.Host.PendingInvitationCount == 0, TimeSpan.FromSeconds(20),
                () => $"만료된 초대의 매핑이 정리되지 않았습니다. (남은 매핑 {ctx.Host.PendingInvitationCount})");

            // 사전 검증을 우회해 그대로 접속해도 승인되지 않는다.
            {
                var late = NewAttemptViewer(ctx);
                var error = TryConnect(late, received.ConnectionString, ReverseE2EContext.ProfessorId, ctx.Password);
                await AssertAttemptIsRefusedAsync(late, error, "만료된 초대");
                await DisposeRefusedViewerAsync(late);
            }
            AssertNoExtraApproval(ctx, 0, 0, "만료된 초대");
            Assert.Equal(ReverseSessionState.Hosting, ctx.Host.CurrentState);
        });

    /// <summary>이미 승인된 교수자가 있을 때 두 번째 접속자/다른 교수자는 승인되지 않고, 정상 접속자의 권한은 유지된다.</summary>
    [WdsFact]
    public Task Reverse_SecondViewerOrOtherProfessor_IsRejected_AndApprovedProfessorKeepsControl()
        => RunOnStaAsync(async () =>
        {
            await using var ctx = new ReverseE2EContext();
            await ctx.Step1_StartStudentSharingAsync();
            await ctx.Step2_CreateAndDeliverInvitationAsync();
            ctx.Step3_ProfessorRequestsConnection();
            await ctx.Step4_AwaitNativeApprovalAsync();

            await ctx.Host.GrantControlAsync(ReverseE2EContext.ProfessorId);
            await ctx.Events.WaitAsync(ReverseE2EContext.NativeEventTimeout, ReverseAttendeeEventKind.ControlGranted);

            // 다른 교수자 앞으로 초대를 발급하는 것 자체가 거부된다.
            await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Host.CreateProfessorInvitationAsync(
                ctx.SessionId, ctx.SharingId, "other-professor", Guid.NewGuid(), "other-" + ctx.Password, DateTimeOffset.UtcNow.AddMinutes(5)));

            // 같은 초대 정보를 가로챈 두 번째 접속자는 승인되지 않는다.
            {
                var intruder = NewAttemptViewer(ctx);
                var error = TryConnect(intruder, ctx.Invitation!.ConnectionString, ReverseE2EContext.ProfessorId, ctx.Password);
                await AssertAttemptIsRefusedAsync(intruder, error, "두 번째 접속자");
                await DisposeRefusedViewerAsync(intruder);
            }

            // 정상 접속자는 그대로 연결되어 있고 권한(ControlLevel=3)도 유지된다.
            AssertNoExtraApproval(ctx, 1, 1, "두 번째 접속자");
            Assert.False(ctx.Viewer!.Terminated.Task.IsCompleted, "침입 시도 때문에 정상 접속자의 연결이 끊겼습니다.");
            Assert.Equal(ReverseSessionManager.ControlLevelInteractive,
                await ctx.Host.GetAttendeeControlLevelAsync(ReverseE2EContext.ProfessorId));
            Assert.Equal(ReverseSessionState.ControlGranted, ctx.Host.CurrentState);
            Assert.Equal(1, ctx.Host.PendingInvitationCount); // 정상 접속자의 매핑이 침입 거부 때문에 사라지지 않았다
        });

    /// <summary>접속이 끝난 초대는 다시 쓸 수 없다(소진).</summary>
    [WdsFact]
    public Task Reverse_ConsumedInvitation_CannotBeReusedAfterDisconnect()
        => RunOnStaAsync(async () =>
        {
            await using var ctx = new ReverseE2EContext();
            await ctx.Step1_StartStudentSharingAsync();
            await ctx.Step2_CreateAndDeliverInvitationAsync();
            ctx.Step3_ProfessorRequestsConnection();
            await ctx.Step4_AwaitNativeApprovalAsync();

            ctx.Viewer!.Viewer.Disconnect();
            await ctx.Events.WaitAsync(ReverseE2EContext.NativeEventTimeout, ReverseAttendeeEventKind.Disconnected);
            Assert.Equal(0, ctx.Host.PendingInvitationCount);

            {
                var reuse = NewAttemptViewer(ctx);
                var error = TryConnect(reuse, ctx.Invitation!.ConnectionString, ReverseE2EContext.ProfessorId, ctx.Password);
                await AssertAttemptIsRefusedAsync(reuse, error, "소진된 초대 재사용");
                await DisposeRefusedViewerAsync(reuse);
            }

            AssertNoExtraApproval(ctx, 1, 0, "소진된 초대 재사용");
            Assert.Equal(ReverseSessionState.Disconnected, ctx.Host.CurrentState);
        });

    /// <summary>종료 후 재접속: 새 공유 세션 + 새 초대로만 접속되고, 이전 세션의 초대는 거부된다. 화면도 다시 표시된다.</summary>
    [WdsFact]
    public Task Reverse_ReconnectAfterTermination_NeedsFreshInvitation_AndShowsScreenAgain()
        => RunOnStaAsync(async () =>
        {
            await using var ctx = new ReverseE2EContext();

            // 1차 접속 → 호스트가 종료
            await ctx.Step1_StartStudentSharingAsync();
            await ctx.Step2_CreateAndDeliverInvitationAsync();
            var firstSharingId = ctx.SharingId;
            var firstInvitation = ctx.Invitation!;
            ctx.Step3_ProfessorRequestsConnection();
            await ctx.Step4_AwaitNativeApprovalAsync();

            await ctx.Host.StopReverseSharingAsync();
            await ctx.Viewer!.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ReverseSessionState.Inactive, ctx.Host.CurrentState);
            Assert.Equal(0, ctx.Host.PendingInvitationCount);
            Assert.Equal(0, ctx.Host.ActiveAttendeeCount);

            // 2차: 새 공유 세션(새 SharingId) + 새 초대
            ctx.Events.Reset();
            await ctx.Step1_StartStudentSharingAsync();
            Assert.NotEqual(firstSharingId, ctx.SharingId);
            await ctx.Step2_CreateAndDeliverInvitationAsync();
            Assert.NotEqual(firstInvitation.InvitationId, ctx.Invitation!.InvitationId);
            Assert.NotEqual(firstInvitation.ConnectionString, ctx.Invitation.ConnectionString);

            // 이전 세션에서 받았던 초대는 새 세션에서 통하지 않는다.
            {
                var stale = NewAttemptViewer(ctx);
                var error = TryConnect(stale, firstInvitation.ConnectionString, ReverseE2EContext.ProfessorId, ctx.Password);
                await AssertAttemptIsRefusedAsync(stale, error, "이전 세션의 초대");
                await DisposeRefusedViewerAsync(stale);
            }
            AssertNoExtraApproval(ctx, 0, 0, "이전 세션의 초대");

            // 새 초대로는 다시 승인되고 실제 화면이 다시 표시된다.
            ctx.Step3_ProfessorRequestsConnection();
            await ctx.Step4_AwaitNativeApprovalAsync();
            await ctx.Step5_AwaitRealDisplayAsync();
            AssertNoExtraApproval(ctx, 1, 1, "재접속");
        });
    #endregion

    // ════════════════════════════════════════════════════════════════════
    // 실제 WDS: 배율(SmartSizing)과 판서의 실제 반영 (피드백 4)
    // ════════════════════════════════════════════════════════════════════
    #region 배율·판서 실제 반영

    [WdsFact]
    public Task Reverse_SmartSizingResizesRealViewer_AndAnnotationReachesSharedScreen()
        => RunOnStaAsync(RunScalingAndAnnotationAsync);

    private static async Task RunScalingAndAnnotationAsync()
    {
        await using var ctx = new ReverseE2EContext();
        await ctx.Step1_StartStudentSharingAsync();
        await ctx.Step2_CreateAndDeliverInvitationAsync();
        ctx.Step3_ProfessorRequestsConnection();
        await ctx.Step4_AwaitNativeApprovalAsync();
        await ctx.Step5_AwaitRealDisplayAsync();

        var viewer = ctx.Viewer!;
        var area = viewer.ScreenArea();
        var surfaceSize = viewer.Surface.ClientSize;
        var sourceSize = Forms.Screen.PrimaryScreen!.Bounds.Size;

        // ── 배율: 맞춤 → 휠 축소 → 맞춤 복귀 (중복 배율 적용 없이) ──
        var viewportAdapter = new WdsViewportAdapter(new WheelScrollAdapter(), new ViewportFitAdapter());
        viewportAdapter.SetAxViewer(viewer.Viewer);
        viewportAdapter.SetSourceSize(sourceSize);
        viewportAdapter.SetViewportSize(surfaceSize);
        var appliedEvents = new List<ViewerAppliedEventArgs>();
        viewportAdapter.ViewerApplied += (_, e) => appliedEvents.Add(e);

        // (1) 맞춤: ApplyFitMode 후 CalculateRenderBounds 가 맞춤 스케일을 한 번만 적용한다.
        viewportAdapter.ApplyFitMode(FitMode.Fit);
        var fit = viewportAdapter.CalculateRenderBounds(surfaceSize);
        double expectedFit = Math.Min((double)surfaceSize.Width / sourceSize.Width, (double)surfaceSize.Height / sourceSize.Height);
        Assert.InRange(fit.Width, (int)(sourceSize.Width * expectedFit) - 1, (int)(sourceSize.Width * expectedFit) + 1);
        Assert.InRange(fit.Height, (int)(sourceSize.Height * expectedFit) - 1, (int)(sourceSize.Height * expectedFit) + 1);
        viewportAdapter.ApplyViewportSettings(fit);
        Assert.Equal(fit, viewer.Viewer.Bounds);
        Assert.True((bool)((dynamic)viewer.Viewer).SmartSizing, "SmartSizing 이 실제 컨트롤에서 true 로 읽혀야 합니다.");
        Assert.Single(appliedEvents);
        Assert.Equal(fit.Size, appliedEvents[0].ViewportInfo.ViewportSize);

        int fullWidthRun = 0;
        await WaitUntilAsync(() =>
        {
            var scan = ScanScreen(area, IsMagenta);
            fullWidthRun = RightmostRunWidth(scan.ColumnCounts);
            return scan.Count >= 30 && fullWidthRun >= 8;
        }, TimeSpan.FromSeconds(20), () => $"100% 배율에서 원격 화면의 마커가 보이지 않습니다. (run={fullWidthRun})");

        // (2) 휠 5칸 축소 → 줌 0.5 → 실제 뷰어가 절반 크기로 줄고, 원격 화면도 같은 비율로 다시 그려진다.
        var zoom = viewportAdapter.ApplyWheelZoom(-600);
        Assert.Equal(0.5, zoom, 3);
        var half = viewportAdapter.CalculateRenderBounds(surfaceSize);
        Assert.InRange(half.Width, fit.Width / 2 - 1, fit.Width / 2 + 1);
        Assert.InRange(half.Height, fit.Height / 2 - 1, fit.Height / 2 + 1);

        viewportAdapter.ApplyViewportSettings(half);
        Assert.Equal(half, viewer.Viewer.Bounds);
        Assert.Equal(2, appliedEvents.Count);

        int halfWidthRun = 0;
        await WaitUntilAsync(() =>
        {
            var scan = ScanScreen(area, IsMagenta);
            halfWidthRun = RightmostRunWidth(scan.ColumnCounts);
            return scan.Count >= 8 && halfWidthRun > 0 && Math.Abs((double)halfWidthRun / fullWidthRun - 0.5) <= 0.15;
        }, TimeSpan.FromSeconds(20),
            () => $"SmartSizing 으로 원격 화면이 절반 크기로 다시 그려지지 않았습니다. (100%={fullWidthRun}px, 현재={halfWidthRun}px)");

        // 축소된 뷰어 바깥은 실제로 비어(컨테이너 배경) 있어야 한다.
        AssertPixelNear(viewer.Surface.PointToScreen(new Point(Math.Max(1, half.X / 2), surfaceSize.Height / 2)), viewer.Surface.BackColor);
        AssertPixelNear(viewer.Surface.PointToScreen(new Point(half.Right + Math.Max(1, (surfaceSize.Width - half.Right) / 2), surfaceSize.Height / 2)), viewer.Surface.BackColor);

        // (3) 맞춤 복귀: 사용자 배율이 1.0 으로 돌아가고 뷰어가 다시 맞춤 크기가 된다.
        viewportAdapter.ApplyFitMode(FitMode.Fit);
        var restored = viewportAdapter.CalculateRenderBounds(surfaceSize);
        Assert.Equal(fit, restored);
        viewportAdapter.ApplyViewportSettings(restored);
        Assert.Equal(restored, viewer.Viewer.Bounds);
        Assert.Equal(3, appliedEvents.Count);

        // ── 판서: 제품 오버레이 엔진 (테스트 전용 캔버스가 아님) ──
        var annotation = ctx.Layout.Annotation;
        var annotationManager = new AnnotationManager();
        var engine = new AnnotationEngineAdapter(annotationManager);
        var localLayer = new AnnotationOverlayLayer(annotation.Width, annotation.Height);
        using var studentOverlay = AnnotationOverlayLayer.CreateDesktopOverlay(annotation, "[EduStream E2E] 학생 판서 오버레이");
        var transmittedPayloads = new List<string>();

        int rendered = 0, dispatched = 0;
        engine.OnStrokeRendered += (_, _) => Interlocked.Increment(ref rendered);
        engine.OnStrokeDispatched += (_, _) => Interlocked.Increment(ref dispatched);
        localLayer.BindLocalRenderer(engine);
        studentOverlay.BindTransmission(engine, (json, _) => transmittedPayloads.Add(json));
        var syncEvents = new List<AnnotationLayerSyncedEventArgs>();
        engine.OnLayerSynced += (_, e) => { lock (syncEvents) syncEvents.Add(e); };
        await engine.ActivateEngineAsync();

        var beforeCyan = ScanScreen(area, IsCyan).Count;
        var beforeYellow = ScanScreen(area, IsYellow).Count;
        var beforeRed = ScanScreen(area, IsRed).Count;

        var width = (int)annotation.Width;
        await engine.ReceiveStrokeAsync(new AnnotationStroke
        {
            ParticipantId = ReverseE2EContext.ProfessorId,
            Tool = AnnotationTool.Line,
            Color = new AnnotationColor(0, 255, 255),
            StrokeWidth = 10,
            Points = new[] { new Point(20, 40), new Point(width - 20, 40) }
        });
        await engine.ReceiveStrokeAsync(new AnnotationStroke
        {
            ParticipantId = ReverseE2EContext.ProfessorId,
            Tool = AnnotationTool.Rectangle,
            Color = AnnotationColor.Yellow,
            StrokeWidth = 8,
            Points = new[] { new Point(30, 80), new Point(width - 30, 190) }
        });

        // 엔진 완료 이벤트는 핸들러가 실제로 성공했을 때만 발생한다.
        await WaitUntilAsync(() => Volatile.Read(ref rendered) == 2 && Volatile.Read(ref dispatched) == 2,
            TimeSpan.FromSeconds(5), () => $"판서 완료 이벤트 부족 (rendered={rendered}, dispatched={dispatched})");
        Assert.Equal(2, (await annotationManager.GetAllStrokesAsync()).Count);

        // (a) 로컬 레이어에 선/도형이 실제로 그려졌다 (렌더링한 비트맵 픽셀 확인)
        Assert.True(localLayer.CountPixels(IsCyan) > 1000, "교수자 로컬 레이어에 선이 그려지지 않았습니다.");
        Assert.True(localLayer.CountPixels(IsYellow) > 1000, "교수자 로컬 레이어에 도형이 그려지지 않았습니다.");
        Assert.Equal(2, localLayer.ShapeCount);

        // (b) 학생 화면 오버레이에도 그려졌다
        Assert.True(studentOverlay.CountPixels(IsCyan) > 1000, "학생 화면 오버레이에 선이 그려지지 않았습니다.");
        Assert.True(studentOverlay.CountPixels(IsYellow) > 1000, "학생 화면 오버레이에 도형이 그려지지 않았습니다.");
        Assert.Equal(2, transmittedPayloads.Count);
        Assert.Equal(ReverseE2EContext.ProfessorId, AnnotationStrokeWire.FromJson(transmittedPayloads[0]).ParticipantId);

        // (c) 공유 대상 화면에 반영됐다: 오버레이는 뷰어 창 밖(학생 데스크톱)에만 있으므로, 뷰어 영역에서 새로 생긴
        //     시안/노랑 픽셀은 WDS 로 전달된 원격 화면 이미지에서만 나올 수 있다.
        int afterCyan = 0, afterYellow = 0;
        await WaitUntilAsync(() =>
        {
            afterCyan = ScanScreen(area, IsCyan).Count;
            afterYellow = ScanScreen(area, IsYellow).Count;
            return afterCyan - beforeCyan >= 40 && afterYellow - beforeYellow >= 40;
        }, TimeSpan.FromSeconds(20),
            () => $"판서가 공유 화면(뷰어)에 반영되지 않았습니다. (cyan {beforeCyan}→{afterCyan}, yellow {beforeYellow}→{afterYellow})");

        // ── 숨김: 로컬 레이어 / 학생 오버레이 / 공유 화면(뷰어) 모두에서 실제로 사라진다 ──
        await engine.SetLayerVisibilityAsync(false);
        Assert.False(annotationManager.IsLayerVisible);
        Assert.Equal(0, localLayer.ShapeCount);
        Assert.Equal(0, studentOverlay.ShapeCount);
        Assert.Equal(0, localLayer.CountPixels(IsCyan) + localLayer.CountPixels(IsYellow));
        Assert.Equal(0, studentOverlay.CountPixels(IsCyan) + studentOverlay.CountPixels(IsYellow));
        await WaitUntilAsync(() =>
        {
            afterCyan = ScanScreen(area, IsCyan).Count;
            afterYellow = ScanScreen(area, IsYellow).Count;
            return afterCyan - beforeCyan < 10 && afterYellow - beforeYellow < 10;
        }, TimeSpan.FromSeconds(20),
            () => $"숨김이 공유 화면(뷰어)에 반영되지 않았습니다. (cyan {beforeCyan}→{afterCyan}, yellow {beforeYellow}→{afterYellow})");
        Assert.Equal(2, (await annotationManager.GetAllStrokesAsync()).Count); // 숨겨도 스트로크는 보존

        // ── 숨긴 동안 그린 스트로크는 어디에도 나타나지 않는다 ──
        int renderedBeforeHiddenStroke = Volatile.Read(ref rendered);
        await engine.ReceiveStrokeAsync(new AnnotationStroke
        {
            ParticipantId = ReverseE2EContext.ProfessorId,
            Tool = AnnotationTool.Line,
            Color = new AnnotationColor(255, 0, 0),
            StrokeWidth = 10,
            Points = new[] { new Point(20, 150), new Point(width - 20, 150) }
        });
        Assert.Equal(renderedBeforeHiddenStroke, Volatile.Read(ref rendered)); // 그리지 않았으니 완료 이벤트도 없다
        Assert.Equal(3, (await annotationManager.GetAllStrokesAsync()).Count);
        Assert.Equal(0, localLayer.ShapeCount);
        Assert.Equal(0, studentOverlay.ShapeCount);
        await Task.Delay(1500); // 화면 전달 지연보다 길게 기다린 뒤에도 뷰어에 나타나면 안 된다
        Assert.True(ScanScreen(area, IsRed).Count - beforeRed < 10, "숨긴 상태에서 그린 스트로크가 공유 화면에 나타났습니다.");

        // ── 다시 표시: 보존된 3개가 모두 복원되어 공유 화면에 다시 나타난다 ──
        await engine.SetLayerVisibilityAsync(true);
        Assert.True(annotationManager.IsLayerVisible);
        Assert.Equal(3, localLayer.ShapeCount);
        Assert.Equal(3, studentOverlay.ShapeCount);
        Assert.True(localLayer.CountPixels(IsCyan) > 1000 && localLayer.CountPixels(IsYellow) > 1000 && localLayer.CountPixels(IsRed) > 1000,
            "재표시 후 로컬 레이어에 3개 스트로크가 모두 복원되지 않았습니다.");
        int redNow = 0;
        await WaitUntilAsync(() =>
        {
            afterCyan = ScanScreen(area, IsCyan).Count;
            afterYellow = ScanScreen(area, IsYellow).Count;
            redNow = ScanScreen(area, IsRed).Count;
            return afterCyan - beforeCyan >= 40 && afterYellow - beforeYellow >= 40 && redNow - beforeRed >= 40;
        }, TimeSpan.FromSeconds(20),
            () => $"재표시가 공유 화면(뷰어)에 반영되지 않았습니다. (cyan {beforeCyan}→{afterCyan}, yellow {beforeYellow}→{afterYellow}, red {beforeRed}→{redNow})");

        // ── 전체 삭제: 모든 출력에서 사라지고, 다시 표시해도 되살아나지 않는다 ──
        await engine.ClearAllStrokesAsync();
        Assert.Empty(await annotationManager.GetAllStrokesAsync());
        Assert.Equal(0, localLayer.ShapeCount);
        Assert.Equal(0, studentOverlay.ShapeCount);
        await engine.SetLayerVisibilityAsync(true);
        Assert.Equal(0, localLayer.ShapeCount);
        Assert.Equal(0, studentOverlay.ShapeCount);
        await WaitUntilAsync(() =>
        {
            afterCyan = ScanScreen(area, IsCyan).Count;
            afterYellow = ScanScreen(area, IsYellow).Count;
            redNow = ScanScreen(area, IsRed).Count;
            return afterCyan - beforeCyan < 10 && afterYellow - beforeYellow < 10 && redNow - beforeRed < 10;
        }, TimeSpan.FromSeconds(20),
            () => $"전체 삭제가 공유 화면(뷰어)에 반영되지 않았습니다. (cyan {beforeCyan}→{afterCyan}, yellow {beforeYellow}→{afterYellow}, red {beforeRed}→{redNow})");

        // 엔진이 각 변경(숨김, 재표시, 삭제, 재표시, 숨긴 뒤 재표시...)을 싱크에 실제로 적용했을 때만 이벤트가 발생했다.
        lock (syncEvents)
        {
            Assert.Equal(
                new[] { AnnotationLayerChange.VisibilityChanged, AnnotationLayerChange.VisibilityChanged, AnnotationLayerChange.Cleared, AnnotationLayerChange.VisibilityChanged },
                syncEvents.Select(e => e.Snapshot.Change).ToArray());
            Assert.All(syncEvents, e => Assert.Equal(2, e.AppliedHandlerCount));
        }

        await engine.DeactivateEngineAsync();
    }
    #endregion

    // ════════════════════════════════════════════════════════════════════
    // 단위 수준: 어댑터 로직 (Stub/Mock 사용 — E2E 아님)
    // ════════════════════════════════════════════════════════════════════
    #region 단위 테스트

    [Fact]
    public async Task ViewportAdapter_FitBounds_AreAppliedAndReadBackFromRealControl_UnitLevel()
    {
        var sessionMgr = new MockReverseSessionManager();
        var reverseAdapter = new ReverseScreenShareAdapter(sessionMgr);
        var viewportAdapter = new WdsViewportAdapter(new StubWheelScrollAdapter(), new StubViewportFitAdapter());
        using var viewer = new SmartSizingSurface(); // 실제 WinForms Control (속성 읽기/쓰기 모두 실제 값)
        viewportAdapter.SetAxViewer(viewer);

        ViewerAppliedEventArgs? applied = null;
        viewportAdapter.ViewerApplied += (_, e) => applied = e;

        await reverseAdapter.ActivateAdapterAsync();
        viewportAdapter.SetViewportSize(new Size(960, 540));

        reverseAdapter.AddDisplayHandler((_, w, h) =>
        {
            viewportAdapter.SetSourceSize(new Size(w, h));
            viewportAdapter.ApplyFitMode(FitMode.Fit);
            var renderBounds = viewportAdapter.CalculateRenderBounds(new Size(960, 540));
            viewportAdapter.ApplyViewportSettings(renderBounds); // 가짜 뷰어가 아니라 어댑터의 실제 적용 경로
            return Task.CompletedTask;
        });

        byte[] wdsFrame = new byte[16];
        wdsFrame[0] = 0x57; wdsFrame[1] = 0x44; wdsFrame[2] = 0x53;
        BitConverter.GetBytes(1920).CopyTo(wdsFrame, 4);
        BitConverter.GetBytes(1080).CopyTo(wdsFrame, 8);

        sessionMgr.ReceiveFrame(wdsFrame);

        Assert.NotNull(applied); // 적용 실패는 ReverseScreenShareAdapter 가 삼키므로 완료 이벤트로 성공을 확인한다.
        Assert.True(viewer.SmartSizing);
        Assert.Equal(new Rectangle(0, 0, 960, 540), viewer.Bounds);
        Assert.Equal(new Size(960, 540), applied!.ViewportInfo.ViewportSize);
        Assert.Equal(viewer.Bounds, viewportAdapter.LastAppliedViewerBounds);
    }

    [Fact]
    public void ViewportAdapter_WhenControlLacksSmartSizing_ThrowsAndDoesNotRaiseViewerApplied()
    {
        var viewportAdapter = new WdsViewportAdapter(new StubWheelScrollAdapter(), new StubViewportFitAdapter());
        using var notAViewer = new Forms.Panel();
        viewportAdapter.SetAxViewer(notAViewer);
        bool raised = false;
        viewportAdapter.ViewerApplied += (_, _) => raised = true;

        Assert.Throws<NotSupportedException>(() => viewportAdapter.ApplyViewportSettings(new Rectangle(0, 0, 100, 100)));
        Assert.False(raised);
        Assert.Equal(Rectangle.Empty, viewportAdapter.LastAppliedViewerBounds);

        var noViewer = new WdsViewportAdapter(new StubWheelScrollAdapter(), new StubViewportFitAdapter());
        Assert.Throws<InvalidOperationException>(() => noViewer.ApplyViewportSettings(new Rectangle(0, 0, 100, 100)));
    }

    [Fact]
    public async Task ReverseScreenShareAdapter_WhenDisplayFails_StrictlyBlocksDisplayedEvent()
    {
        var sessionMgr = new MockReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(sessionMgr);
        await adapter.ActivateAdapterAsync();

        bool eventRaised = false;
        adapter.FrameDisplayed += (_, _) => eventRaised = true;

        adapter.AddDisplayHandler((_, _, _) => throw new InvalidOperationException("UI 렌더링 붕괴"));
        sessionMgr.ReceiveFrame(new byte[] { 0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, 10, 0, 0, 0, 0, 0 });

        Assert.False(eventRaised, "꼼수가 제거되어 실패 시 이벤트는 절대 발생하지 않아야 합니다.");
    }

    [Fact]
    public async Task WindowsNativeInputPipeline_MultiThread_RaceCondition_VerifiedBlockedCount()
    {
        var pipeline = new WindowsNativeInputPipeline();
        await pipeline.ConnectAsync();
        await pipeline.InjectInputAsync("student_1");

        int successCount = 0;
        int blockedCount = 0;

        var blockTask = Task.Run(async () => await pipeline.BlockInputAsync("student_1"));

        var inputTasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            inputTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await pipeline.InjectMouseMoveAsync("student_1", 100, 100);
                    Interlocked.Increment(ref successCount);
                }
                catch (InputPipelineException)
                {
                    Interlocked.Increment(ref blockedCount);
                }
            }));
        }

        await Task.WhenAll(inputTasks);
        await blockTask;

        Assert.Equal(50, successCount + blockedCount);
        await Assert.ThrowsAsync<InputPipelineException>(() => pipeline.InjectMouseMoveAsync("student_1", 100, 100));
    }

    [Fact]
    public async Task OnFrameReceived_ConcurrentExecution_ShouldSafelyProcessAllFrames()
    {
        var sessionMgr = new MockReverseSessionManager();
        var adapter = new ReverseScreenShareAdapter(sessionMgr);
        await adapter.ActivateAdapterAsync();

        int displayInvokeCount = 0;
        adapter.AddDisplayHandler((frame, w, h) =>
        {
            Interlocked.Increment(ref displayInvokeCount);
            return Task.CompletedTask;
        });

        byte[] dummyFrame = new byte[16];
        dummyFrame[0] = 0x57; dummyFrame[1] = 0x44; dummyFrame[2] = 0x53;
        BitConverter.GetBytes(1920).CopyTo(dummyFrame, 4);
        BitConverter.GetBytes(1080).CopyTo(dummyFrame, 8);

        int concurrentTasks = 100;
        var tasks = new Task[concurrentTasks];

        for (int i = 0; i < concurrentTasks; i++)
        {
            tasks[i] = Task.Run(() => sessionMgr.ReceiveFrame(dummyFrame));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(concurrentTasks, adapter.TotalFramesProcessed);
        Assert.Equal(concurrentTasks, displayInvokeCount);
    }

    [Fact]
    public async Task WdsViewportAdapter_ApplyWheelZoom_StrictlyPushesToViewerHandler()
    {
        var adapter = new WdsViewportAdapter(new StubWheelScrollAdapter(), new StubViewportFitAdapter());
        adapter.SetSourceSize(new Size(1000, 1000));
        adapter.SetViewportSize(new Size(1000, 1000));

        bool viewerAppliedCalled = false;

        adapter.AddViewerHandler(info =>
        {
            viewerAppliedCalled = true;
            return Task.CompletedTask;
        });

        adapter.ApplyWheelZoom(120);
        await adapter.ApplyToViewerAsync();

        Assert.True(viewerAppliedCalled, "계산만 하고 끝내면 안 됩니다. 반드시 등록된 뷰어 핸들러로 값을 쏴주어야 합니다.");
    }
    #endregion

    // ════════════════════════════════════════════════════════════════════
    // E2E 지원 코드
    // ════════════════════════════════════════════════════════════════════
    #region E2E helpers

    /// <summary>STA + Dispatcher 메시지 루프에서 본문을 실행한다. (WPF 창/ActiveX/COM 이벤트가 같은 스레드에서 동작)</summary>
    private static Task RunOnStaAsync(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            Task task;
            try { task = body(); }
            catch (Exception ex) { task = Task.FromException(ex); }

            task.ContinueWith(t =>
            {
                if (t.IsFaulted) completion.TrySetException(t.Exception!.InnerExceptions);
                else if (t.IsCanceled) completion.TrySetCanceled();
                else completion.TrySetResult();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }, TaskScheduler.Default);

            if (!dispatcher.HasShutdownStarted)
            {
                try { Dispatcher.Run(); } catch (InvalidOperationException) { /* 본문이 동기 완료된 경우 */ }
            }
        })
        {
            IsBackground = true,
            Name = "EduStream-ReverseE2E-STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string> describeFailure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (condition()) return;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"{timeout.TotalSeconds:0}초 안에 조건이 충족되지 않았습니다: {describeFailure()}");
            await Task.Delay(300);
        }
    }

    private static bool IsMagenta(byte r, byte g, byte b) => r > 200 && b > 200 && g < 60;
    private static bool IsCyan(byte r, byte g, byte b) => r < 60 && g > 200 && b > 200;
    private static bool IsYellow(byte r, byte g, byte b) => r > 200 && g > 200 && b < 60;
    private static bool IsRed(byte r, byte g, byte b) => r > 200 && g < 60 && b < 60;

    private readonly record struct ColorScan(int Count, int[] ColumnCounts);

    /// <summary>실제 화면(스크린) 영역을 캡처해 조건에 맞는 픽셀 수와 열별 분포를 센다.</summary>
    private static ColorScan ScanScreen(Rectangle screenRect, Func<byte, byte, byte, bool> match)
    {
        int w = screenRect.Width, h = screenRect.Height;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(screenRect.Location, Point.Empty, screenRect.Size);

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var buffer = new byte[Math.Abs(stride) * h];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            var columns = new int[w];
            int count = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    if (match(buffer[i + 2], buffer[i + 1], buffer[i]))
                    {
                        count++;
                        columns[x]++;
                    }
                }
            }
            return new ColorScan(count, columns);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// 가장 오른쪽에 있는 연속 픽셀 구간의 너비.
    /// 마커는 학생 데스크톱 맨 오른쪽에 있으므로 이 구간이 "직접 미러링된 마커"이고,
    /// 뷰어 창 자체가 다시 비치는 재귀 복사본은 왼쪽(더 작은 크기)에 떨어져 있어 제외된다.
    /// </summary>
    private static int RightmostRunWidth(int[] columnCounts)
    {
        int x = columnCounts.Length - 1;
        while (x >= 0 && columnCounts[x] == 0) x--;
        if (x < 0) return 0;

        int right = x, left = x, gap = 0;
        for (; x >= 0; x--)
        {
            if (columnCounts[x] > 0) { left = x; gap = 0; }
            else if (++gap > 1) break;
        }
        return right - left + 1;
    }

    private static void AssertPixelNear(Point screenPoint, Color expected, int tolerance = 8)
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(screenPoint, Point.Empty, new Size(1, 1));
        var actual = bmp.GetPixel(0, 0);
        Assert.True(
            Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance && Math.Abs(actual.B - expected.B) <= tolerance,
            $"축소된 뷰어 바깥 영역이 비어 있어야 합니다. 위치={screenPoint}, 기대={expected}, 실제={actual}");
    }

    /// <summary>2번 IRemoteInputGate 가 넘기는 RemoteControlState 최소 유효 값. 교수자 ID 매핑은 게이트 생성자가 담당한다.</summary>
    private static EduStream.Core.Collaboration.RemoteControlState CreateGateState(Guid sessionId)
    {
        var professor = new EduStream.Core.Collaboration.ParticipantConnection(
            sessionId, Guid.NewGuid(), Guid.NewGuid(), EduStream.Core.Collaboration.ParticipantRole.Professor);
        var student = new EduStream.Core.Collaboration.ParticipantConnection(
            sessionId, Guid.NewGuid(), Guid.NewGuid(), EduStream.Core.Collaboration.ParticipantRole.Student);
        var snapshot = new EduStream.Core.Collaboration.ParticipantSnapshot(student, ReverseE2EContext.StudentId, true, true, true, 1);
        return EduStream.Core.Collaboration.RemoteControlState.Request(professor, snapshot, Guid.NewGuid());
    }

    private static Point PickOffsetPoint(Point origin)
    {
        var bounds = Forms.Screen.PrimaryScreen!.Bounds;
        int dx = origin.X + 23 < bounds.Right - 2 ? 23 : -23;
        int dy = origin.Y + 17 < bounds.Bottom - 2 ? 17 : -17;
        return new Point(origin.X + dx, origin.Y + dy);
    }

    private static void AssertCursorNear(Point expected, int tolerance = 3)
    {
        var actual = Forms.Cursor.Position;
        Assert.True(Math.Abs(actual.X - expected.X) <= tolerance && Math.Abs(actual.Y - expected.Y) <= tolerance,
            $"실제 OS 커서 위치가 기대와 다릅니다. 기대={expected}, 실제={actual}");
    }

    /// <summary>네이티브 이벤트를 순서·시점과 무관하게 안전하게 기다릴 수 있도록 기록한다.</summary>
    private sealed class LifecycleRecorder
    {
        private readonly object _gate = new();
        private readonly List<ReverseAttendeeEventArgs> _events = new();
        private readonly List<(ReverseAttendeeEventKind[] Kinds, TaskCompletionSource<ReverseAttendeeEventArgs> Tcs)> _waiters = new();

        public int Count(ReverseAttendeeEventKind kind)
        {
            lock (_gate) return _events.Count(e => e.Kind == kind);
        }

        /// <summary>새 접속 사이클을 시작하기 전에 이전 사이클의 기록을 비운다 (대기자는 건드리지 않는다).</summary>
        public void Reset()
        {
            lock (_gate) _events.Clear();
        }

        public void Record(ReverseAttendeeEventArgs e)
        {
            var completed = new List<TaskCompletionSource<ReverseAttendeeEventArgs>>();
            lock (_gate)
            {
                _events.Add(e);
                for (int i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Kinds.Contains(e.Kind))
                    {
                        completed.Add(_waiters[i].Tcs);
                        _waiters.RemoveAt(i);
                    }
                }
            }
            foreach (var tcs in completed) tcs.TrySetResult(e);
        }

        public async Task<ReverseAttendeeEventArgs> WaitAsync(TimeSpan timeout, params ReverseAttendeeEventKind[] kinds)
        {
            TaskCompletionSource<ReverseAttendeeEventArgs> tcs;
            lock (_gate)
            {
                var existing = _events.FirstOrDefault(x => kinds.Contains(x.Kind));
                if (existing != null) return existing;
                tcs = new TaskCompletionSource<ReverseAttendeeEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((kinds, tcs));
            }

            try
            {
                return await tcs.Task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"네이티브 이벤트 [{string.Join(", ", kinds)}] 가 {timeout.TotalSeconds:0}초 안에 도착하지 않았습니다. 수신된 이벤트: [{Describe()}]");
            }
        }

        public string Describe()
        {
            lock (_gate) return string.Join(", ", _events.Select(e => $"{e.Kind}({e.Reason})"));
        }
    }

    /// <summary>화면에 겹치지 않게 배치할 창 3개의 DIP 영역</summary>
    private readonly record struct E2ELayout(Wpf.Rect Viewer, Wpf.Rect Marker, Wpf.Rect Annotation)
    {
        public static E2ELayout Compute()
        {
            var work = Wpf.SystemParameters.WorkArea;
            double viewerWidth = Math.Clamp(work.Width * 0.45, 480, 800);
            var viewer = new Wpf.Rect(work.Left + 10, work.Top + 10, viewerWidth, viewerWidth * 3 / 4);
            var marker = new Wpf.Rect(work.Right - 190, work.Top + 20, 160, 160);

            double annotationLeft = viewer.Right + 30;
            double annotationWidth = Math.Min(460, work.Right - 30 - annotationLeft);
            var annotation = new Wpf.Rect(annotationLeft, marker.Bottom + 40, annotationWidth, 220);

            if (annotationWidth < 260 || annotation.Bottom > work.Bottom - 10 || viewer.Bottom > work.Bottom - 10)
                throw new InvalidOperationException(
                    $"E2E 검증용 창 3개(뷰어/마커/판서 영역)를 겹치지 않게 배치하려면 더 큰 화면이 필요합니다. (작업 영역 {work})");

            return new E2ELayout(viewer, marker, annotation);
        }
    }

    /// <summary>교수자 측 실제 AxRDPViewer 를 담은 창. 뷰어는 Dock 이 없는 Panel 안에 둬서 어댑터가 크기를 실제로 바꿀 수 있다.</summary>
    private sealed class ProfessorViewerHarness : IDisposable
    {
        private bool _disposed;
        private readonly EventHandler _onEstablished;
        private readonly EventHandler _onFailed;
        private readonly AxRDPCOMAPILib._IRDPSessionEvents_OnConnectionTerminatedEventHandler _onTerminated;
        private readonly AxRDPCOMAPILib._IRDPSessionEvents_OnErrorEventHandler _onError;

        public Wpf.Window Window { get; }
        public Forms.Panel Surface { get; }
        public AxRDPCOMAPILib.AxRDPViewer Viewer { get; }
        public TaskCompletionSource<bool> Established { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Terminated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProfessorViewerHarness(Wpf.Rect bounds)
        {
            Surface = new Forms.Panel { BackColor = Color.FromArgb(24, 24, 24), Dock = Forms.DockStyle.Fill };
            Viewer = new AxRDPCOMAPILib.AxRDPViewer();

            _onEstablished = (_, _) => Established.TrySetResult(true);
            _onFailed = (_, _) => Failed.TrySetResult("OnConnectionFailed");
            _onTerminated = (_, _) => Terminated.TrySetResult(true);
            _onError = (_, _) => Failed.TrySetResult("OnError");
            Viewer.OnConnectionEstablished += _onEstablished;
            Viewer.OnConnectionFailed += _onFailed;
            Viewer.OnConnectionTerminated += _onTerminated;
            Viewer.OnError += _onError;

            ((ISupportInitialize)Viewer).BeginInit();
            Surface.Controls.Add(Viewer);
            ((ISupportInitialize)Viewer).EndInit();

            Window = new Wpf.Window
            {
                Title = "[EduStream E2E] 교수자 뷰어 (자동 종료)",
                WindowStyle = Wpf.WindowStyle.None,
                ResizeMode = Wpf.ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true, // 화면 캡처 검증 중 다른 창(IDE 등)이 뷰어를 가리지 않도록
                WindowStartupLocation = Wpf.WindowStartupLocation.Manual,
                Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
                Content = new Forms.Integration.WindowsFormsHost { Child = Surface }
            };
        }

        public void Show()
        {
            Window.Show();
            Window.UpdateLayout();
            Viewer.Bounds = Surface.ClientRectangle;
            Viewer.CreateControl();
            // 원격 데스크톱(예: 2560x1600)이 뷰어(예: 1000x750)보다 크므로 SmartSizing 없이는 좌상단 일부만 보인다.
            Viewer.SmartSizing = true;
        }

        public Rectangle ScreenArea() => Surface.RectangleToScreen(Surface.ClientRectangle);

        public void Connect(string connectionString, string name, string password)
            => Viewer.Connect(connectionString, name, password);

        public void RequestInteractiveControl()
            => Viewer.RequestControl(RDPCOMAPILib.CTRL_LEVEL.CTRL_LEVEL_INTERACTIVE);

        public async Task WaitEstablishedAsync(TimeSpan timeout)
        {
            var finished = await Task.WhenAny(Established.Task, Failed.Task, Task.Delay(timeout));
            if (finished == Failed.Task)
                throw new InvalidOperationException($"교수자 뷰어 연결 실패 이벤트: {Failed.Task.Result}");
            if (finished != Established.Task)
                throw new TimeoutException($"교수자 뷰어의 OnConnectionEstablished 가 {timeout.TotalSeconds:0}초 안에 오지 않았습니다.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Viewer.OnConnectionEstablished -= _onEstablished;
            Viewer.OnConnectionFailed -= _onFailed;
            Viewer.OnConnectionTerminated -= _onTerminated;
            Viewer.OnError -= _onError;

            // IRDPSRAPIViewer 는 이미 Failed/Terminated 인데 Disconnect 를 다시 치면 COM 이 멈춘다.
            // 연결이 살아 있을 때만 끊고, 컨트롤 해제는 항상 수행한다.
            if (IsConnectionLive())
            {
                try { Viewer.Disconnect(); } catch { /* 이미 끊김 */ }
            }

            try
            {
                if (!Viewer.IsDisposed)
                {
                    Surface.Controls.Remove(Viewer);
                    Viewer.Dispose();
                }
            }
            catch { /* 표시 컨트롤 이미 해제 */ }
            try { Window.Close(); } catch { }
        }

        private bool IsConnectionLive()
        {
            try
            {
                return Viewer.IsHandleCreated
                    && !Viewer.IsDisposed
                    && Established.Task.IsCompletedSuccessfully
                    && !Terminated.Task.IsCompleted
                    && !Failed.Task.IsCompleted;
            }
            catch
            {
                return false;
            }
        }
    }
    /// <summary>
    /// 7단계 중 1~5단계를 "실제 WDS 세션 + 실제 AxRDPViewer + 실제 화면 픽셀"로 수행하는 컨텍스트.
    /// 각 Step 안의 Assert 가 해당 단계의 통과 조건이며, 상태 강제 주입이나 OnConnectedAsync 직접 호출은 없다.
    /// </summary>
    private sealed class ReverseE2EContext : IAsyncDisposable
    {
        public const string StudentId = "e2e-student-01";
        public const string ProfessorId = "e2e-professor-01";
        public static readonly TimeSpan NativeEventTimeout = TimeSpan.FromSeconds(30);

        public ReverseSessionManager Host { get; } = new();
        public LifecycleRecorder Events { get; } = new();
        public E2ELayout Layout { get; } = E2ELayout.Compute();
        public Guid SessionId { get; } = Guid.NewGuid();
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public string Password { get; } = Guid.NewGuid().ToString("N");

        public Guid SharingId { get; private set; }
        public ReverseInvitationPacket? Invitation { get; private set; }
        public ReverseInvitationWire? Received { get; private set; }
        public ProfessorViewerHarness? Viewer { get; private set; }
        private readonly List<ProfessorViewerHarness> _attempts = new();
        private Wpf.Window? _marker;

        /// <summary>이벤트 기록보다 먼저 실행되는 구독 지점 (입력 게이트처럼 이벤트 직후 즉시 반응해야 하는 코드용)</summary>
        public event Action<ReverseAttendeeEventArgs>? BeforeRecord;

        public ReverseE2EContext()
        {
            Host.AttendeeLifecycleChanged += (_, e) =>
            {
                BeforeRecord?.Invoke(e);
                Events.Record(e);
            };
        }

        // 1) 학생 측 실제 WDS 공유 세션 생성
        public async Task Step1_StartStudentSharingAsync()
        {
            SharingId = await Host.StartReverseSharingAsync(SessionId, StudentId);

            Assert.NotEqual(Guid.Empty, SharingId);
            Assert.True(Host.IsReverseSharingActive);
            Assert.Equal(ReverseSessionState.Hosting, Host.CurrentState);
            Assert.Equal(0, Host.PendingInvitationCount);
        }

        // 2) 유효한 초대 정보 생성 → JSON 으로 교수자에게 전달 (비밀번호는 별도 경로이므로 JSON 에 없다)
        public async Task Step2_CreateAndDeliverInvitationAsync(TimeSpan? lifetime = null)
        {
            Invitation = await Host.CreateProfessorInvitationAsync(
                SessionId, SharingId, ProfessorId, ConnectionId, Password, DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(5)));

            Assert.False(string.IsNullOrWhiteSpace(Invitation.ConnectionString));
            Assert.NotEqual(Guid.Empty, Invitation.InvitationId);
            Assert.Equal(ProfessorId, Invitation.ProfessorId);
            Assert.Equal(StudentId, Invitation.HostStudentId);
            Assert.Equal(1, Host.PendingInvitationCount); // ConnectionString 매핑 등록
            Assert.Equal(ReverseSessionState.Hosting, Host.CurrentState);

            var json = ReverseInvitationWire.From(Invitation).ToJson();
            Assert.DoesNotContain(Password, json);

            Received = ReverseInvitationWire.FromJson(json); // 교수자 쪽 수신
            Received.Validate(SessionId, ProfessorId, ConnectionId, DateTimeOffset.UtcNow, StudentId);
            Assert.Equal(Invitation.ConnectionString, Received.ConnectionString);
            Assert.Equal(StudentId, Received.StudentId);
            Assert.False(Received.ViewOnly);
            Assert.Equal(ReverseControlMode.HostGrantedInteractive, Received.ControlMode);
        }

        // 3) 교수자 측 실제 뷰어가 전달받은 초대로 접속 요청
        public void Step3_ProfessorRequestsConnection()
        {
            // 재접속 사이클: 이전 사이클의 뷰어/마커는 먼저 정리한다.
            try { Viewer?.Dispose(); } catch { }
            try { _marker?.Close(); } catch { }

            _marker = new Wpf.Window
            {
                Title = "[EduStream E2E] 학생 화면 마커 (자동 종료)",
                WindowStyle = Wpf.WindowStyle.None,
                ResizeMode = Wpf.ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = WpfMedia.Brushes.Magenta,
                WindowStartupLocation = Wpf.WindowStartupLocation.Manual,
                Left = Layout.Marker.Left, Top = Layout.Marker.Top, Width = Layout.Marker.Width, Height = Layout.Marker.Height
            };
            _marker.Show();

            Viewer = new ProfessorViewerHarness(Layout.Viewer);
            Viewer.Show();

            // 접속 전에는 뷰어 영역에 학생 화면의 마커가 있을 수 없다.
            Assert.True(ScanScreen(Viewer.ScreenArea(), IsMagenta).Count < 5, "접속 전인데 뷰어 영역에 마커 색이 이미 있습니다.");
            Assert.Equal(ReverseSessionState.Hosting, Host.CurrentState);

            Viewer.Connect(Received!.ConnectionString, Received.ParticipantId, Password);
        }

        // 4) 호스트가 실제 OnAttendeeConnected 로 접속을 받고 매핑 테이블 기준으로 승인
        public async Task Step4_AwaitNativeApprovalAsync()
        {
            var approvalTask = Events.WaitAsync(NativeEventTimeout,
                ReverseAttendeeEventKind.Approved, ReverseAttendeeEventKind.Rejected);

            var first = await Task.WhenAny(approvalTask, Viewer!.Failed.Task);
            if (first == Viewer.Failed.Task)
                throw new InvalidOperationException($"교수자 뷰어 연결 실패 이벤트({Viewer.Failed.Task.Result}) — 호스트 승인 이벤트가 오기 전에 실패했습니다.");

            var approved = await approvalTask;
            Assert.True(approved.Kind == ReverseAttendeeEventKind.Approved, $"호스트가 접속을 거부했습니다: {approved.Reason}");
            Assert.Equal(ProfessorId, approved.ProfessorId);
            Assert.Equal(StudentId, approved.StudentId);
            Assert.Equal(Invitation!.InvitationId, approved.InvitationId);
            Assert.Equal(ConnectionId, approved.ConnectionId);
            Assert.Equal(ReverseSessionManager.ControlLevelView, approved.ControlLevel);

            Assert.Equal(ReverseSessionState.Connected, Host.CurrentState);
            Assert.Equal(1, Host.ActiveAttendeeCount);
            Assert.Equal(ReverseSessionManager.ControlLevelView, await Host.GetAttendeeControlLevelAsync(ProfessorId)); // 실제 COM 값

            await Viewer.WaitEstablishedAsync(NativeEventTimeout); // 뷰어 쪽에서도 연결 확정
        }

        // 5) 학생 데스크톱이 교수자 뷰어에 실제 픽셀로 표시됨 (학생 쪽 마커 창의 색이 뷰어 안에 나타난다)
        public async Task Step5_AwaitRealDisplayAsync()
        {
            var area = Viewer!.ScreenArea();
            int count = 0;
            await WaitUntilAsync(() => (count = ScanScreen(area, IsMagenta).Count) >= 30,
                TimeSpan.FromSeconds(25), () => $"뷰어에 학생 화면이 표시되지 않았습니다. (마커 픽셀 {count}개)");
            Assert.True(count >= 30);
        }

        public void RetainAttempt(ProfessorViewerHarness viewer) => _attempts.Add(viewer);

        public async ValueTask DisposeAsync()
        {
            // 거절된 뷰어를 공유 세션이 살아있는 동안 OleClose 하면 WDS COM 이 멈춘다.
            // 세션을 먼저 종료한 뒤에 뷰어를 해제한다.
            try { await Host.StopReverseSharingAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"[E2E] 정리 중 종료 실패: {ex}"); }
            await Dispatcher.Yield();
            foreach (var attempt in _attempts)
                try { attempt.Dispose(); } catch { }
            try { Viewer?.Dispose(); } catch { }
            try { _marker?.Close(); } catch { }
            Host.Dispose();
        }
    }
    #endregion
}
