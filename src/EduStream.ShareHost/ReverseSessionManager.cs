using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace EduStream.ShareHost;

/// <summary>역방향 세션에서 네이티브 WDS 이벤트가 만든 참석자 생명주기 종류</summary>
public enum ReverseAttendeeEventKind
{
    /// <summary>OnAttendeeConnected 에서 매핑 테이블 검증을 통과해 승인됨 (보기 전용, ControlLevel=2)</summary>
    Approved,
    /// <summary>OnAttendeeConnected 에서 검증 실패로 거부(연결 종료)됨</summary>
    Rejected,
    /// <summary>호스트(학생)가 제어를 허용해 ControlLevel=3 이 실제로 적용됨</summary>
    ControlGranted,
    /// <summary>제어가 회수되어 ControlLevel=2 로 복귀함</summary>
    ControlRevoked,
    /// <summary>뷰어의 제어 요청이 호스트 허용 없이 들어와 거부됨</summary>
    ControlRequestDenied,
    /// <summary>OnAttendeeDisconnected 로 승인된 참석자가 이탈함</summary>
    Disconnected
}

/// <summary>네이티브 WDS 이벤트 기반 참석자 생명주기 알림 인자</summary>
public sealed class ReverseAttendeeEventArgs : EventArgs
{
    public ReverseAttendeeEventKind Kind { get; init; }
    public string ProfessorId { get; init; } = string.Empty;
    public string StudentId { get; init; } = string.Empty;
    public Guid InvitationId { get; init; }
    public Guid ConnectionId { get; init; }
    public int AttendeeId { get; init; } = -1;

    /// <summary>이벤트 시점에 호스트 COM 객체에 적용된 ControlLevel (알 수 없으면 -1)</summary>
    public int ControlLevel { get; init; } = -1;

    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 학생(호스트) → 교수자(뷰어) 역방향 WDS 세션 관리자.
///
/// 설계 요점
///  1) 초대 생성 시 WDS가 돌려준 <c>ConnectionString</c> 을 Key 로 하는 매핑 테이블(ConcurrentDictionary)에
///     ProfessorId/StudentId/세션·공유 세대/만료 정보를 보관합니다.
///  2) 승인 판단은 가짜 호출이 아니라 실제 네이티브 이벤트 <c>OnAttendeeConnected</c>(dispid 301) 안에서
///     참석자가 사용한 초대의 ConnectionString 으로 매핑 테이블을 조회해 수행합니다.
///     (WDS 초대 COM 객체에 존재하지 않는 ProfessorId 속성을 읽는 방식은 사용하지 않습니다.)
///  3) 승인 직후는 보기 전용(ControlLevel=2). 제어(ControlLevel=3)는 호스트가
///     <see cref="GrantControlAsync"/> 로 명시 허용했을 때만 올라가며 <see cref="RevokeControlAsync"/> 로 회수됩니다.
///  4) 실패/이탈/종료 시 매핑 테이블·초대 COM·참석자 정보를 모두 정리(Cleanup)합니다.
///  5) COM 이벤트가 실제로 도착하도록 모든 WDS 호출은 이 인스턴스 전용 STA 스레드(Dispatcher)에서 수행합니다.
/// </summary>
public sealed class ReverseSessionManager : IReverseSessionManager, IDisposable
{
    /// <summary>CTRL_LEVEL_VIEW</summary>
    public const int ControlLevelView = 2;
    /// <summary>CTRL_LEVEL_INTERACTIVE</summary>
    public const int ControlLevelInteractive = 3;
    private const int ControlLevelRequestInteractive = 5; // CTRL_LEVEL_REQCTRL_INTERACTIVE

    // _IRDPSessionEvents (IID 98a97042-6698-40e9-8efd-b3200990004b) dispid
    private static readonly Guid SessionEventsIid = new("98a97042-6698-40e9-8efd-b3200990004b");
    private const int DispidOnAttendeeConnected = 301;
    private const int DispidOnAttendeeDisconnected = 302;
    private const int DispidOnControlLevelChangeRequest = 309;

    private static readonly Guid RdpSessionClsid = new("9B78F0E6-3E05-4A5B-B2E8-E743A8956B65");
    private const int RegdbEClassNotReg = unchecked((int)0x80040154);

    /// <summary>ConnectionString 매핑 테이블의 값. 초대 1건과 그 신원 정보를 묶습니다.</summary>
    private sealed class PendingInvitation
    {
        public required Guid InvitationId { get; init; }
        public required Guid SessionId { get; init; }
        public required Guid SharingId { get; init; }
        public required Guid ConnectionId { get; init; }
        public required string ProfessorId { get; init; }
        public required string StudentId { get; init; }
        public required string ConnectionString { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required object Com { get; init; }
        public int? AttendeeId { get; set; }
        public bool Released { get; set; }
    }

    private sealed class ApprovedAttendee
    {
        public required int AttendeeId { get; init; }
        public required object Com { get; init; }
        public required PendingInvitation Invitation { get; init; }
        public string ProfessorId => Invitation.ProfessorId;
    }

    private readonly object _stateLock = new();

    // 🎯 ConnectionString → 초대/신원 매핑 테이블
    private readonly ConcurrentDictionary<string, PendingInvitation> _invitationsByConnectionString =
        new(StringComparer.Ordinal);

    // attendee.Id → 승인된 참석자 (_stateLock 으로 보호)
    private readonly Dictionary<int, ApprovedAttendee> _approvedAttendees = new();
    private readonly List<(int DispId, Delegate Handler)> _subscriptions = new();

    private Thread? _staThread;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _expiryTimer;
    private object? _rdpSession;

    private Guid _sessionId = Guid.Empty;
    private Guid _reverseSharingId = Guid.Empty;
    private string _hostStudentId = string.Empty;
    private MonitorInfo? _sharedMonitor;
    private string _approvedTargetProfessorId = string.Empty;
    private bool _controlPermitted;
    private bool _tearingDown;
    private ReverseSessionState _state = ReverseSessionState.Inactive;
    private bool _isDisposed;

    public bool IsReverseSharingActive
    {
        get { lock (_stateLock) return _state != ReverseSessionState.Inactive; }
    }

    public ReverseSessionState CurrentState
    {
        get { lock (_stateLock) return _state; }
    }

    /// <summary>매핑 테이블에 남아 있는(아직 정리되지 않은) 초대 수</summary>
    public int PendingInvitationCount => _invitationsByConnectionString.Count;

    /// <summary>OnAttendeeConnected 로 승인되어 현재 접속 중인 참석자 수</summary>
    public int ActiveAttendeeCount
    {
        get { lock (_stateLock) return _approvedAttendees.Count; }
    }

    /// <summary>이번 공유에 적용한 모니터. 지정하지 않았으면 null 이며 WDS 기본 데스크톱 전체를 공유합니다.</summary>
    public MonitorInfo? SharedMonitor
    {
        get { lock (_stateLock) return _sharedMonitor; }
    }

    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    /// <summary>실제 네이티브 이벤트(연결/이탈/제어 요청)와 호스트 제어 허용·회수 결과 알림</summary>
    public event EventHandler<ReverseAttendeeEventArgs>? AttendeeLifecycleChanged;

    // ───────────────────────── 세션 시작 ─────────────────────────

    public Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, CancellationToken cancellationToken = default)
        => StartReverseSharingCoreAsync(sessionId, studentId, null, cancellationToken);

    /// <summary>
    /// 선택한 모니터 사각형만 공유합니다. 열거 결과의 물리 픽셀 경계를 WDS SetDesktopSharedRect 에 넣습니다.
    /// </summary>
    public Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, MonitorInfo shareMonitor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shareMonitor);
        if (shareMonitor.Width <= 0 || shareMonitor.Height <= 0)
            throw new ArgumentException("공유할 모니터의 크기가 없습니다.", nameof(shareMonitor));
        return StartReverseSharingCoreAsync(sessionId, studentId, shareMonitor, cancellationToken);
    }

    private async Task<Guid> StartReverseSharingCoreAsync(Guid sessionId, string studentId, MonitorInfo? shareMonitor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (string.IsNullOrWhiteSpace(studentId))
            throw new ArgumentException("학생 ID가 필요합니다.", nameof(studentId));

        lock (_stateLock)
        {
            if (_state != ReverseSessionState.Inactive)
                throw new InvalidOperationException("역방향 공유가 이미 활성화되어 있습니다.");

            _state = ReverseSessionState.Hosting; // 동시 Start 차단을 위해 먼저 선점
            _tearingDown = false;
            _sharedMonitor = null;
        }

        try
        {
            var dispatcher = StartStaThread();
            return await dispatcher
                .InvokeAsync(() => OpenSessionOnSta(sessionId, studentId, shareMonitor), DispatcherPriority.Normal, cancellationToken)
                .Task.ConfigureAwait(false);
        }
        catch
        {
            try { await StopReverseSharingAsync().ConfigureAwait(false); } catch { /* 원래 예외를 우선 전달 */ }
            throw;
        }
    }

    private Guid OpenSessionOnSta(Guid sessionId, string studentId, MonitorInfo? shareMonitor)
    {
        // 레지스트리(CLSID) 미등록과 실제 Open 실패를 구분해서 보고한다.
        Type? rdpType = Type.GetTypeFromProgID("RDPCOMAPILib.RDPSession")
                        ?? Type.GetTypeFromCLSID(RdpSessionClsid, throwOnError: false);
        if (rdpType == null)
            throw new NotSupportedException("WDS 엔진(RDPCOMAPILib.RDPSession)이 레지스트리에 등록되지 않았습니다. 현재 OS(Windows Home 등)에서 지원하지 않습니다.");

        object session;
        try
        {
            session = Activator.CreateInstance(rdpType)
                      ?? throw new InvalidOperationException("WDS 세션 인스턴스가 null 입니다.");
        }
        catch (COMException ex) when (ex.HResult == RegdbEClassNotReg)
        {
            throw new NotSupportedException("WDS 엔진(RDPSession)이 레지스트리에 등록되지 않았습니다. 현재 OS(Windows Home 등)에서 지원하지 않습니다.", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("WDS 세션 인스턴스를 생성할 수 없습니다. (COM 활성화 실패)", ex);
        }

        lock (_stateLock) _rdpSession = session;

        try
        {
            // 🎯 실제 네이티브 이벤트 연결 (Open 이전에 구독)
            if (Marshal.IsComObject(session))
            {
                Subscribe(session, DispidOnAttendeeConnected, new Action<object>(OnAttendeeConnected));
                Subscribe(session, DispidOnAttendeeDisconnected, new Action<object>(OnAttendeeDisconnected));
                Subscribe(session, DispidOnControlLevelChangeRequest, new Action<object, int>(OnControlLevelChangeRequest));
            }

            dynamic dyn = session;
            dyn.ColorDepth = 24;
            if (shareMonitor != null)
            {
                int right = shareMonitor.Left + shareMonitor.Width;
                int bottom = shareMonitor.Top + shareMonitor.Height;
                try
                {
                    dyn.SetDesktopSharedRect(shareMonitor.Left, shareMonitor.Top, right, bottom);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"선택한 모니터({shareMonitor.Left},{shareMonitor.Top},{shareMonitor.Width}x{shareMonitor.Height})를 WDS 공유 영역으로 적용하지 못했습니다.", ex);
                }
            }
            dyn.Open();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"실제 WDS 세션(Open)을 여는 데 실패했습니다: {ex.Message}", ex);
        }

        Guid sharingId;
        lock (_stateLock)
        {
            _sessionId = sessionId;
            _reverseSharingId = sharingId = Guid.NewGuid();
            _hostStudentId = studentId;
            _sharedMonitor = shareMonitor;
            _approvedTargetProfessorId = string.Empty;
            _controlPermitted = false;
        }

        _expiryTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => SweepExpiredInvitations(), Dispatcher.CurrentDispatcher);

        return sharingId;
    }

    // ───────────────────────── 초대 생성 ─────────────────────────

    public async Task<ReverseInvitationPacket> CreateProfessorInvitationAsync(
        Guid sessionId, Guid sharingId, string professorId, Guid connectionId,
        string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Dispatcher dispatcher;
        lock (_stateLock)
        {
            if (_state != ReverseSessionState.Hosting)
                throw new InvalidOperationException("역방향 공유가 호스팅 상태가 아닙니다.");

            if (_rdpSession == null || _dispatcher == null)
                throw new InvalidOperationException("WDS 세션이 만들어지지 않았습니다.");

            if (_reverseSharingId != sharingId)
                throw new InvalidOperationException("현재 진행 중인 공유 ID와 요청한 공유 ID가 일치하지 않습니다.");

            if (_sessionId != sessionId)
                throw new InvalidOperationException("현재 진행 중인 세션 ID와 요청한 세션 ID가 일치하지 않습니다.");

            dispatcher = _dispatcher;
        }

        if (string.IsNullOrWhiteSpace(professorId))
            throw new ArgumentException("교수자 ID가 필요합니다.", nameof(professorId));
        if (connectionId == Guid.Empty)
            throw new ArgumentException("연결 ID가 필요합니다.", nameof(connectionId));
        if (string.IsNullOrEmpty(invitationPassword))
            throw new ArgumentException("초대 비밀번호가 필요합니다.", nameof(invitationPassword));
        if (expiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentException("만료 시각이 이미 지났습니다.", nameof(expiresAt));

        return await dispatcher
            .InvokeAsync(() => CreateInvitationOnSta(sessionId, sharingId, professorId, connectionId, invitationPassword, expiresAt),
                DispatcherPriority.Normal, cancellationToken)
            .Task.ConfigureAwait(false);
    }

    private ReverseInvitationPacket CreateInvitationOnSta(Guid sessionId, Guid sharingId, string professorId,
        Guid connectionId, string invitationPassword, DateTimeOffset expiresAt)
    {
        string hostStudentId;
        object session;
        lock (_stateLock)
        {
            if (_state != ReverseSessionState.Hosting || _tearingDown || _rdpSession == null)
                throw new InvalidOperationException("역방향 공유가 호스팅 상태가 아닙니다.");

            if (_approvedAttendees.Count > 0)
                throw new InvalidOperationException("이미 교수자가 접속 중입니다.");

            if (_approvedTargetProfessorId.Length > 0 &&
                !string.Equals(_approvedTargetProfessorId, professorId, StringComparison.Ordinal))
                throw new InvalidOperationException("이미 다른 교수자가 승인 대상으로 지정되어 있습니다.");

            hostStudentId = _hostStudentId;
            session = _rdpSession;
        }

        // 같은 교수자에게 재발급하면 이전 초대는 폐기한다.
        foreach (var stale in _invitationsByConnectionString.Values
                     .Where(p => string.Equals(p.ProfessorId, professorId, StringComparison.Ordinal)).ToArray())
            RevokePending(stale);

        var invitationId = Guid.NewGuid();
        var groupName = "EduStream_Reverse_" + invitationId.ToString("N");
        object? invitationCom = null;
        object? invitationsCom = null;

        try
        {
            dynamic dyn = session;
            invitationsCom = dyn.Invitations;

            // 같은 교수자에게 초대를 갱신해도 AuthString은 WDS 세션 내에서 중복되지 않아야 한다.
            dynamic created = ((dynamic)invitationsCom).CreateInvitation(invitationId.ToString("N"), groupName, invitationPassword, 1);
            invitationCom = created;

            string connectionString = created.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("WDS가 빈 연결 문자열을 반환했습니다.");

            var pending = new PendingInvitation
            {
                InvitationId = invitationId,
                SessionId = sessionId,
                SharingId = sharingId,
                ConnectionId = connectionId,
                ProfessorId = professorId,
                StudentId = hostStudentId,
                ConnectionString = connectionString,
                ExpiresAt = expiresAt,
                Com = (object)created
            };

            if (!_invitationsByConnectionString.TryAdd(connectionString, pending))
                throw new InvalidOperationException("동일한 ConnectionString 의 초대가 이미 매핑되어 있습니다.");

            lock (_stateLock) _approvedTargetProfessorId = professorId;

            return new ReverseInvitationPacket
            {
                SessionId = sessionId,
                SharingId = sharingId,
                InvitationId = invitationId,
                ProfessorId = professorId,
                ConnectionId = connectionId,
                ConnectionString = connectionString,
                ExpiresAt = expiresAt,
                HostStudentId = hostStudentId,
                ControlMode = ReverseControlMode.HostGrantedInteractive
            };
        }
        catch
        {
            if (invitationCom != null)
            {
                try { ((dynamic)invitationCom).Revoked = true; } catch { /* 폐기 실패는 원래 예외를 가리지 않음 */ }
                ReleaseCom(invitationCom);
            }
            throw;
        }
        finally
        {
            ReleaseCom(invitationsCom);
        }
    }

    // ───────────────────────── 네이티브 이벤트 ─────────────────────────

    /// <summary>
    /// 🎯 실제 네이티브 OnAttendeeConnected 이벤트 핸들러.
    /// 참석자가 사용한 초대의 ConnectionString 으로 매핑 테이블을 조회해 ProfessorId/StudentId/세대/만료를 검증하고
    /// 승인(보기 전용 ControlLevel=2) 또는 거부(TerminateConnection)합니다.
    /// </summary>
    private void OnAttendeeConnected(object attendee)
    {
        if (attendee is null) return;

        try
        {
            HandleAttendeeConnected(attendee);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"[ReverseSession] OnAttendeeConnected 처리 실패: {ex}");
            SafeTerminate(attendee);
            Publish(new ReverseAttendeeEventArgs
            {
                Kind = ReverseAttendeeEventKind.Rejected,
                Reason = $"승인 처리 중 예외: {ex.GetType().Name}"
            });
        }
    }

    private void HandleAttendeeConnected(object attendeeObj)
    {
        dynamic attendee = attendeeObj;
        int attendeeId = Convert.ToInt32(attendee.Id);

        string? connectionString = null;
        object? invitationObj = attendee.Invitation;
        if (invitationObj != null)
            connectionString = ((dynamic)invitationObj).ConnectionString as string;

        PendingInvitation? matched = null;
        string? rejectReason;
        // 이미 승인된 참석자가 쓰고 있는 초대로 들어온 두 번째 접속자(침입)를 거부할 때는
        // 정상 접속자의 매핑·초대 상태를 건드리지 않고 침입 연결만 끊는다.
        bool matchedAlreadyInUse = false;

        lock (_stateLock)
        {
            if (_tearingDown || (_state != ReverseSessionState.Hosting && _state != ReverseSessionState.Connecting))
            {
                rejectReason = $"현재 상태({_state})에서는 접속을 받을 수 없습니다.";
            }
            else if (string.IsNullOrEmpty(connectionString) ||
                     !_invitationsByConnectionString.TryGetValue(connectionString, out matched))
            {
                rejectReason = "발급된 초대(ConnectionString)와 일치하지 않는 접속입니다.";
            }
            else
            {
                matchedAlreadyInUse = matched.AttendeeId.HasValue;
                rejectReason = ValidateApprovalLocked(matched, DateTimeOffset.UtcNow);
            }

            if (rejectReason is null)
            {
                // 승인: 항상 보기 전용으로 시작. 제어는 GrantControlAsync 로만 올라간다.
                attendee.ControlLevel = ControlLevelView;

                matched!.AttendeeId = attendeeId;
                _approvedAttendees[attendeeId] = new ApprovedAttendee
                {
                    AttendeeId = attendeeId,
                    Com = attendeeObj,
                    Invitation = matched
                };
                _state = ReverseSessionState.Connected;
            }
            else if (matched != null && !matchedAlreadyInUse)
            {
                // 매핑은 있었지만 검증에 실패한 초대는 소진 처리(1회용)하고, 연결 대기 중이었다면 실패로 전이한다.
                _invitationsByConnectionString.TryRemove(matched.ConnectionString, out _);
                if (_state == ReverseSessionState.Connecting) _state = ReverseSessionState.Failed;
            }
        }

        if (rejectReason is not null)
        {
            SafeTerminate(attendeeObj);
            if (matched != null && !matchedAlreadyInUse) RevokeInvitationCom(matched);

            Publish(new ReverseAttendeeEventArgs
            {
                Kind = ReverseAttendeeEventKind.Rejected,
                ProfessorId = matched?.ProfessorId ?? string.Empty,
                StudentId = matched?.StudentId ?? string.Empty,
                InvitationId = matched?.InvitationId ?? Guid.Empty,
                ConnectionId = matched?.ConnectionId ?? Guid.Empty,
                AttendeeId = attendeeId,
                Reason = rejectReason
            });
            return;
        }

        Publish(new ReverseAttendeeEventArgs
        {
            Kind = ReverseAttendeeEventKind.Approved,
            ProfessorId = matched!.ProfessorId,
            StudentId = matched.StudentId,
            InvitationId = matched.InvitationId,
            ConnectionId = matched.ConnectionId,
            AttendeeId = attendeeId,
            ControlLevel = ControlLevelView,
            Reason = "OnAttendeeConnected 에서 ConnectionString 매핑 검증 통과"
        });
    }

    /// <summary>ProfessorId/StudentId/세대/만료/동시 접속 제한 검증. 통과하면 null</summary>
    private string? ValidateApprovalLocked(PendingInvitation pending, DateTimeOffset now)
    {
        if (pending.AttendeeId.HasValue) return "이미 사용된 초대입니다.";
        if (pending.ExpiresAt <= now) return "만료된 초대입니다.";
        if (pending.SessionId != _sessionId || pending.SharingId != _reverseSharingId)
            return "현재 공유 세대와 일치하지 않는 초대입니다.";
        if (!string.Equals(pending.StudentId, _hostStudentId, StringComparison.Ordinal))
            return "호스트 학생(StudentId)이 일치하지 않습니다.";
        if (_approvedTargetProfessorId.Length == 0 ||
            !string.Equals(pending.ProfessorId, _approvedTargetProfessorId, StringComparison.Ordinal))
            return "승인된 교수자(ProfessorId) 대상이 아닙니다.";
        if (_approvedAttendees.Count > 0) return "이미 다른 교수자가 접속 중입니다.";
        return null;
    }

    /// <summary>🎯 실제 네이티브 OnAttendeeDisconnected 이벤트: 승인된 참석자 이탈 시 매핑·권한 정리</summary>
    private void OnAttendeeDisconnected(object info)
    {
        try
        {
            int attendeeId;
            try
            {
                dynamic dyn = info;
                object? attendee = dyn.Attendee;
                if (attendee == null) return;
                attendeeId = Convert.ToInt32(((dynamic)attendee).Id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"[ReverseSession] 이탈 이벤트 해석 실패: {ex.GetType().Name}");
                return;
            }

            ApprovedAttendee? approved;
            lock (_stateLock)
            {
                if (_tearingDown || !_approvedAttendees.Remove(attendeeId, out approved)) return;

                _invitationsByConnectionString.TryRemove(approved.Invitation.ConnectionString, out _);
                _controlPermitted = false;

                if (_approvedAttendees.Count == 0 &&
                    (_state == ReverseSessionState.Connected || _state == ReverseSessionState.ControlGranted ||
                     _state == ReverseSessionState.Connecting))
                {
                    _state = ReverseSessionState.Disconnected;
                }
            }

            RevokeInvitationCom(approved.Invitation);

            Publish(new ReverseAttendeeEventArgs
            {
                Kind = ReverseAttendeeEventKind.Disconnected,
                ProfessorId = approved.ProfessorId,
                StudentId = approved.Invitation.StudentId,
                InvitationId = approved.Invitation.InvitationId,
                ConnectionId = approved.Invitation.ConnectionId,
                AttendeeId = attendeeId,
                Reason = "OnAttendeeDisconnected"
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"[ReverseSession] OnAttendeeDisconnected 처리 실패: {ex}");
        }
    }

    /// <summary>🎯 실제 네이티브 OnControlLevelChangeRequest: 호스트 허용 없는 제어 요청은 거부</summary>
    private void OnControlLevelChangeRequest(object attendeeObj, int requestedLevel)
    {
        try
        {
            dynamic attendee = attendeeObj;
            int attendeeId = Convert.ToInt32(attendee.Id);
            ApprovedAttendee? approved;
            bool allowed;

            lock (_stateLock)
            {
                _approvedAttendees.TryGetValue(attendeeId, out approved);
                bool wantsInteractive = requestedLevel == ControlLevelInteractive || requestedLevel == ControlLevelRequestInteractive;
                allowed = approved != null && _controlPermitted && wantsInteractive &&
                          string.Equals(approved.ProfessorId, _approvedTargetProfessorId, StringComparison.Ordinal);

                if (approved != null)
                    attendee.ControlLevel = allowed ? ControlLevelInteractive : ControlLevelView;
            }

            if (approved == null)
            {
                SafeTerminate(attendeeObj); // 승인되지 않은 참석자의 제어 요청은 연결 자체를 끊는다.
                return;
            }

            if (!allowed)
            {
                Publish(new ReverseAttendeeEventArgs
                {
                    Kind = ReverseAttendeeEventKind.ControlRequestDenied,
                    ProfessorId = approved.ProfessorId,
                    StudentId = approved.Invitation.StudentId,
                    InvitationId = approved.Invitation.InvitationId,
                    ConnectionId = approved.Invitation.ConnectionId,
                    AttendeeId = attendeeId,
                    ControlLevel = ControlLevelView,
                    Reason = $"호스트가 제어를 허용하지 않아 요청(level={requestedLevel})을 거부했습니다."
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"[ReverseSession] OnControlLevelChangeRequest 처리 실패: {ex}");
            SafeTerminate(attendeeObj);
        }
    }

    // ───────────────────────── 제어 허용 / 회수 ─────────────────────────

    /// <summary>호스트(학생)가 승인된 교수자에게 제어(ControlLevel=3)를 허용. 실제 COM 값을 읽어 검증합니다.</summary>
    public async Task GrantControlAsync(string professorId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(professorId);
        var dispatcher = RequireDispatcher();
        await dispatcher.InvokeAsync(() => GrantControlOnSta(professorId), DispatcherPriority.Normal, cancellationToken)
            .Task.ConfigureAwait(false);
    }

    private void GrantControlOnSta(string professorId)
    {
        ReverseAttendeeEventArgs evt;
        lock (_stateLock)
        {
            if (_tearingDown || (_state != ReverseSessionState.Connected && _state != ReverseSessionState.ControlGranted))
                throw new InvalidOperationException("접속한 참석자가 없어 원격 제어 권한을 부여할 수 없습니다.");

            var approved = _approvedAttendees.Values.FirstOrDefault(a =>
                string.Equals(a.ProfessorId, professorId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("승인된 교수자 접속이 아니므로 제어 권한을 부여할 수 없습니다.");

            if (!string.Equals(approved.ProfessorId, _approvedTargetProfessorId, StringComparison.Ordinal))
                throw new InvalidOperationException("승인 대상 교수자가 아니므로 제어 권한을 부여할 수 없습니다.");

            dynamic attendee = approved.Com;
            try
            {
                attendee.ControlLevel = ControlLevelInteractive;
                int actual = Convert.ToInt32(attendee.ControlLevel);
                if (actual != ControlLevelInteractive)
                    throw new InvalidOperationException($"ControlLevel 적용 확인 실패 (기대 {ControlLevelInteractive}, 실제 {actual}).");
            }
            catch (Exception ex)
            {
                // 적용 확인 실패 시 안전하게 보기 전용으로 되돌린다.
                try { attendee.ControlLevel = ControlLevelView; } catch { SafeTerminate(approved.Com); }
                _controlPermitted = false;
                throw new InvalidOperationException($"원격 제어 권한(ControlLevel=3) 부여 중 예외 발생: {ex.Message}", ex);
            }

            _controlPermitted = true;
            _state = ReverseSessionState.ControlGranted;
            evt = BuildEvent(ReverseAttendeeEventKind.ControlGranted, approved, ControlLevelInteractive, "호스트가 제어를 허용했습니다.");
        }
        Publish(evt);
    }

    /// <summary>제어 회수(ControlLevel=2 복귀). 회수 확인에 실패하면 안전을 위해 연결을 끊고 예외를 던집니다.</summary>
    public async Task RevokeControlAsync(string professorId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(professorId);
        var dispatcher = RequireDispatcher();
        await dispatcher.InvokeAsync(() => RevokeControlOnSta(professorId), DispatcherPriority.Normal, cancellationToken)
            .Task.ConfigureAwait(false);
    }

    private void RevokeControlOnSta(string professorId)
    {
        ReverseAttendeeEventArgs? evt = null;
        lock (_stateLock)
        {
            _controlPermitted = false; // 이후 들어오는 제어 요청은 즉시 거부

            var approved = _approvedAttendees.Values.FirstOrDefault(a =>
                string.Equals(a.ProfessorId, professorId, StringComparison.Ordinal));
            if (approved == null) return; // 회수할 접속이 없으면 허용 플래그만 해제

            dynamic attendee = approved.Com;
            try
            {
                attendee.ControlLevel = ControlLevelView;
                int actual = Convert.ToInt32(attendee.ControlLevel);
                if (actual != ControlLevelView)
                    throw new InvalidOperationException($"ControlLevel 회수 확인 실패 (기대 {ControlLevelView}, 실제 {actual}).");
            }
            catch (Exception ex)
            {
                SafeTerminate(approved.Com); // 회수 실패 시 제어가 남지 않도록 연결 자체를 차단
                throw new InvalidOperationException($"원격 제어 권한 회수 중 예외 발생: {ex.Message}", ex);
            }

            if (_state == ReverseSessionState.ControlGranted) _state = ReverseSessionState.Connected;
            evt = BuildEvent(ReverseAttendeeEventKind.ControlRevoked, approved, ControlLevelView, "호스트가 제어를 회수했습니다.");
        }
        Publish(evt);
    }

    /// <summary>승인된 교수자 참석자의 "실제" 호스트 COM ControlLevel 을 읽습니다. 접속이 없으면 null</summary>
    public async Task<int?> GetAttendeeControlLevelAsync(string professorId, CancellationToken cancellationToken = default)
    {
        var dispatcher = RequireDispatcher();
        return await dispatcher.InvokeAsync<int?>(() =>
        {
            lock (_stateLock)
            {
                var approved = _approvedAttendees.Values.FirstOrDefault(a =>
                    string.Equals(a.ProfessorId, professorId, StringComparison.Ordinal));
                if (approved == null) return null;
                dynamic attendee = approved.Com;
                return Convert.ToInt32(attendee.ControlLevel);
            }
        }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
    }

    // ───────────────────────── 기존 인터페이스 상태 전이 ─────────────────────────

    /// <summary>교수자 연결 대기(Connecting)로 전이. 실제 연결 성사는 OnAttendeeConnected 이벤트로만 일어납니다.</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock) { if (_state == ReverseSessionState.Hosting) _state = ReverseSessionState.Connecting; }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 연결 완료 확인. 이 메서드는 더 이상 상태를 "만들어내지" 않습니다.
    /// 네이티브 OnAttendeeConnected 로 승인된 참석자가 없으면 실패로 전이하고 매핑 정보를 정리합니다.
    /// </summary>
    public async Task OnConnectedAsync(CancellationToken cancellationToken = default)
    {
        bool hasApproved;
        lock (_stateLock)
        {
            hasApproved = _approvedAttendees.Count > 0;
            if (hasApproved || _state != ReverseSessionState.Connecting) return;
            _state = ReverseSessionState.Failed;
        }

        await CleanupAsync().ConfigureAwait(false);
        throw new InvalidOperationException(
            "접속한 참석자가 없어 연결 완료를 확정할 수 없습니다. (OnAttendeeConnected 승인 이벤트 미수신)");
    }

    public async Task OnConnectionFailedAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock) { _state = ReverseSessionState.Failed; }
        await CleanupAsync().ConfigureAwait(false);
    }

    public async Task OnDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock) { _state = ReverseSessionState.Disconnected; }
        await CleanupAsync().ConfigureAwait(false);
    }

    public void ReceiveFrame(byte[] frameData)
    {
        lock (_stateLock)
        {
            if (_state != ReverseSessionState.Connected && _state != ReverseSessionState.ControlGranted) return;
        }
        FrameReceived?.Invoke(this, new FrameReceivedEventArgs { FrameData = frameData, Timestamp = DateTimeOffset.UtcNow });
    }

    // ───────────────────────── 정리(Cleanup) / 종료 ─────────────────────────

    /// <summary>실패/이탈 시: 참석자 제어 회수·연결 종료, 초대 폐기, 매핑 테이블 비우기 (세션 자체는 유지)</summary>
    private async Task CleanupAsync()
    {
        Dispatcher? dispatcher;
        lock (_stateLock) dispatcher = _tearingDown ? null : _dispatcher;

        if (dispatcher == null)
        {
            lock (_stateLock) { _approvedAttendees.Clear(); _controlPermitted = false; }
            _invitationsByConnectionString.Clear();
            return;
        }

        await dispatcher.InvokeAsync(CleanupOnSta).Task.ConfigureAwait(false);
    }

    private void CleanupOnSta()
    {
        ApprovedAttendee[] approved;
        lock (_stateLock)
        {
            approved = _approvedAttendees.Values.ToArray();
            _approvedAttendees.Clear();
            _controlPermitted = false;
        }

        foreach (var a in approved)
        {
            try { ((dynamic)a.Com).ControlLevel = ControlLevelView; } catch { /* 이미 끊긴 참석자 */ }
            SafeTerminate(a.Com);
        }

        foreach (var pending in _invitationsByConnectionString.Values.ToArray())
            RevokePending(pending);
        _invitationsByConnectionString.Clear();
    }

    public async Task StopReverseSharingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Dispatcher? dispatcher;
        Thread? thread;
        lock (_stateLock)
        {
            _tearingDown = true; // 이후 도착하는 COM 이벤트는 모두 무시
            dispatcher = _dispatcher;
            thread = _staThread;
        }

        Exception? closeError = null;
        try
        {
            if (dispatcher != null)
            {
                try
                {
                    closeError = await dispatcher.InvokeAsync(TeardownOnSta).Task.ConfigureAwait(false);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }

                if (thread != null)
                    await Task.Run(() => thread.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
        }
        finally
        {
            _invitationsByConnectionString.Clear();
            lock (_stateLock)
            {
                _approvedAttendees.Clear();
                _rdpSession = null;
                _dispatcher = null;
                _staThread = null;
                _sessionId = Guid.Empty;
                _reverseSharingId = Guid.Empty;
                _hostStudentId = string.Empty;
                _sharedMonitor = null;
                _approvedTargetProfessorId = string.Empty;
                _controlPermitted = false;
                _tearingDown = false;
                _state = ReverseSessionState.Inactive;
            }
        }

        // 🎯 종료 실패를 숨기지 않고 상위로 전파 (모든 정리를 끝낸 뒤)
        if (closeError != null)
            throw new InvalidOperationException("역방향 세션 종료 실패", closeError);
    }

    /// <summary>STA 에서 수행하는 전체 해제. Close 실패만 반환하고 나머지 정리는 항상 끝까지 수행합니다.</summary>
    private Exception? TeardownOnSta()
    {
        try { _expiryTimer?.Stop(); } catch { /* 종료 중 */ }
        _expiryTimer = null;

        ApprovedAttendee[] approved;
        lock (_stateLock)
        {
            approved = _approvedAttendees.Values.ToArray();
            _approvedAttendees.Clear();
            _controlPermitted = false;
        }

        foreach (var a in approved)
        {
            try { ((dynamic)a.Com).ControlLevel = ControlLevelView; } catch { /* 이미 끊긴 참석자 */ }
            SafeTerminate(a.Com);
        }

        foreach (var pending in _invitationsByConnectionString.Values.ToArray())
            RevokePending(pending);
        _invitationsByConnectionString.Clear();

        object? session;
        lock (_stateLock) session = _rdpSession;

        UnsubscribeAll(session);

        Exception? closeError = null;
        if (session != null)
        {
            try { ((dynamic)session).Close(); }
            catch (Exception ex) { closeError = ex; }
            finally
            {
                ReleaseCom(session);
                lock (_stateLock) _rdpSession = null;
            }
        }

        return closeError;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            // 호출 스레드의 SynchronizationContext 와 무관하게 대기하도록 풀 스레드에서 실행
            Task.Run(() => StopReverseSharingAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"[ReverseSession] Dispose 중 종료 실패: {ex}");
        }
    }

    // ───────────────────────── 내부 헬퍼 ─────────────────────────

    private void SweepExpiredInvitations()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pending in _invitationsByConnectionString.Values
                     .Where(p => p.ExpiresAt <= now && !p.AttendeeId.HasValue).ToArray())
        {
            RevokePending(pending);
        }
    }

    /// <summary>매핑 테이블에서 제거하고 WDS 초대를 폐기(Revoked=true)한 뒤 COM 해제</summary>
    private void RevokePending(PendingInvitation pending)
    {
        _invitationsByConnectionString.TryRemove(pending.ConnectionString, out _);
        RevokeInvitationCom(pending);
    }

    private static void RevokeInvitationCom(PendingInvitation pending)
    {
        lock (pending)
        {
            if (pending.Released) return;
            pending.Released = true;
        }

        try { ((dynamic)pending.Com).Revoked = true; } catch { /* 이미 해제된 초대 */ }
        ReleaseCom(pending.Com);
    }

    private ReverseAttendeeEventArgs BuildEvent(ReverseAttendeeEventKind kind, ApprovedAttendee approved, int controlLevel, string reason) => new()
    {
        Kind = kind,
        ProfessorId = approved.ProfessorId,
        StudentId = approved.Invitation.StudentId,
        InvitationId = approved.Invitation.InvitationId,
        ConnectionId = approved.Invitation.ConnectionId,
        AttendeeId = approved.AttendeeId,
        ControlLevel = controlLevel,
        Reason = reason
    };

    private Dispatcher RequireDispatcher()
    {
        lock (_stateLock)
        {
            if (_dispatcher == null || _rdpSession == null || _tearingDown)
                throw new InvalidOperationException("역방향 공유 세션이 활성화되어 있지 않습니다.");
            return _dispatcher;
        }
    }

    private Dispatcher StartStaThread()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run(); // COM 이벤트(OnAttendeeConnected 등)를 처리하는 메시지 루프
        })
        {
            IsBackground = true,
            Name = "EduStream-ReverseWDS-STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var dispatcher = ready.Task.GetAwaiter().GetResult();
        lock (_stateLock)
        {
            _staThread = thread;
            _dispatcher = dispatcher;
        }
        return dispatcher;
    }

    private void Subscribe(object session, int dispId, Delegate handler)
    {
        ComEventsHelper.Combine(session, SessionEventsIid, dispId, handler);
        _subscriptions.Add((dispId, handler));
    }

    private void UnsubscribeAll(object? session)
    {
        if (session != null && Marshal.IsComObject(session))
        {
            foreach (var (dispId, handler) in _subscriptions)
            {
                try { ComEventsHelper.Remove(session, SessionEventsIid, dispId, handler); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[ReverseSession] 이벤트 해제 실패({dispId}): {ex.GetType().Name}"); }
            }
        }
        _subscriptions.Clear();
    }

    private static void SafeTerminate(object attendee)
    {
        try { ((dynamic)attendee).TerminateConnection(); }
        catch { /* 이미 끊어진 연결 */ }
    }

    private static void ReleaseCom(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); } catch { /* 이미 해제됨 */ }
        }
    }

    /// <summary>구독자 예외가 COM 콜백 경계를 넘어가지 않도록 개별 호출을 격리</summary>
    private void Publish(ReverseAttendeeEventArgs args)
    {
        var handlers = AttendeeLifecycleChanged;
        if (handlers == null) return;

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<ReverseAttendeeEventArgs>>())
        {
            try { handler(this, args); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"[ReverseSession] 구독자 예외({args.Kind}): {ex}");
            }
        }
    }
}
