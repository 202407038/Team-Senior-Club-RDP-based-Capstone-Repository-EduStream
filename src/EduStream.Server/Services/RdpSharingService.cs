using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Utils;

namespace EduStream.Server.Services;

/// <summary>단일 STA 메시지 루프에서 WDS 공유·초대·참가자 수명을 관리합니다.</summary>
public sealed class RdpSharingService : IRdpSharingService
{
    private sealed record Invitation(Guid Id, string Participant, string Group, DateTimeOffset Expires, object Com);
    private sealed record Attendee(Guid InvitationId, object Com);
    private static readonly Guid EventsId = new("98a97042-6698-40e9-8efd-b3200990004b");
    private readonly ILogSink _log;
    private readonly Func<object?> _factory;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<Guid, Invitation> _invitations = new();
    private readonly Dictionary<int, Attendee> _attendees = new();
    private readonly List<(int Id, Delegate Handler)> _subscriptions = new();
    private object? _session;
    private Guid _sessionId, _sharingId;
    private DispatcherTimer? _expiryTimer;
    private bool _disposed;

    public RdpSharingService(ILogSink logSink) : this(logSink, () => Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("9B78F0E6-3E05-4A5B-B2E8-E743A8956B65"), true)!)) { }

    public RdpSharingService(ILogSink logSink, Func<object?> rdpSessionFactory)
    {
        _log = logSink;
        _factory = rdpSessionFactory;
        _thread = new Thread(() =>
        {
            _ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run(); // COM 이벤트를 처리하는 메시지 루프를 공유 수명 동안 유지한다.
        }) { IsBackground = true, Name = "EduStream-WDS-STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    private async Task<T> Run<T>(Func<T> action, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        return await dispatcher.InvokeAsync(action, DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
    }
    public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) => Run(() =>
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("세션 ID가 필요합니다.");
        if (_session is not null) throw new InvalidOperationException("공유가 이미 시작되었습니다.");
        try
        {
            _session = _factory() ?? throw new InvalidOperationException("WDS 생성 실패");
            if (Marshal.IsComObject(_session))
            {
                Subscribe(301, new Action<object>(OnConnected));
                Subscribe(302, new Action<object>(OnDisconnected));
                Subscribe(309, new Action<object, int>(OnControlRequested));
            }
            Call(_session, "Open");
            _sessionId = sessionId;
            _sharingId = Guid.NewGuid();
            _expiryTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                (_, _) => SweepExpired(), Dispatcher.CurrentDispatcher);
            _log.Write("[RDP] 공유 시작: 실제 RDPSession.Open 성공");
            return _sharingId;
        }
        catch
        {
            Unsubscribe();
            Release(_session);
            _session = null;
            throw;
        }
    }, cancellationToken);

    public Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId, string participantId,
        Guid connectionId, string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) => Run(() =>
    {
        if (_session is null || sharingId != _sharingId || sessionId != _sessionId)
            throw new InvalidOperationException("현재 공유와 일치하지 않습니다.");
        if (string.IsNullOrWhiteSpace(participantId) || connectionId == Guid.Empty || string.IsNullOrEmpty(invitationPassword) || expiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentException("초대 입력이 유효하지 않습니다.");
        SweepExpired();
        if (_invitations.Count >= 2) throw new InvalidOperationException("최대 참가자 수(2)를 초과했습니다.");
        var id = Guid.NewGuid();
        // 인증 문자열과 그룹은 재발급마다 고유하게 만든다. 표시 이름은 인증 키가 아니다.
        var group = "EduStream_" + id.ToString("N");
        object? manager = null, invitation = null;
        try
        {
            manager = Get(_session, "Invitations") ?? throw new InvalidOperationException("초대 관리자 없음");
            invitation = Call(manager, "CreateInvitation", id.ToString("N"), group, invitationPassword, 1)
                ?? throw new InvalidOperationException("WDS 초대 발급 실패");
            var connection = Get(invitation, "ConnectionString") as string ?? string.Empty;
            var packet = new RdpInvitationPacket
            {
                SessionId = sessionId, SenderId = "Server", SharingId = sharingId, InvitationId = id,
                ParticipantId = participantId, ConnectionId = connectionId, ConnectionString = connection,
                ExpiresAt = expiresAt, ViewOnly = true, Provider = "windows-desktop-sharing", ContractVersion = 1,
                DataLength = System.Text.Encoding.UTF8.GetByteCount(connection)
            };
            RdpInvitationContract.Validate(packet, sessionId, participantId, connectionId, DateTimeOffset.UtcNow);
            _invitations.Add(id, new(id, participantId, group, expiresAt, invitation));
            _log.Write($"[RDP] 초대 생성: participant={participantId}, 활성 초대={_invitations.Count}/2");
            return packet;
        }
        catch
        {
            if (invitation is not null) { try { Set(invitation, "Revoked", true); } finally { Release(invitation); } }
            throw;
        }
        finally { Release(manager); }
    }, cancellationToken);

    public async Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default) =>
        await Run(() => { Revoke(invitationId); return true; }, cancellationToken).ConfigureAwait(false);
    private void Revoke(Guid id)
    {
        if (!_invitations.TryGetValue(id, out var invitation)) return;
        Set(invitation.Com, "Revoked", true);
        foreach (var entry in _attendees.Where(x => x.Value.InvitationId == id).ToArray())
        {
            Call(entry.Value.Com, "TerminateConnection");
            _attendees.Remove(entry.Key);
        }
        _invitations.Remove(id);
        Release(invitation.Com);
        _log.Write($"[RDP] 초대 폐기: participant={invitation.Participant}");
    }
    private void OnConnected(object attendee)
    {
        try
        {
            var comInvitation = Get(attendee, "Invitation");
            var group = comInvitation is null ? null : Get(comInvitation, "GroupName") as string;
            var match = _invitations.Values.SingleOrDefault(x => x.Group == group);
            if (match is null || match.Expires <= DateTimeOffset.UtcNow || _attendees.Values.Any(x => x.InvitationId == match.Id))
            {
                Call(attendee, "TerminateConnection");
                return;
            }
            var id = Convert.ToInt32(Get(attendee, "Id"));
            _attendees[id] = new(match.Id, attendee);
            Set(attendee, "ControlLevel", 2);
            _log.Write($"[RDP] 보기 전용 참가 승인: participant={match.Participant}");
        }
        catch (Exception ex)
        {
            _log.Write($"[RDP] 참가 승인 실패: {ex.GetType().Name}");
            try { Call(attendee, "TerminateConnection"); } catch { }
        }
    }
    private void OnDisconnected(object info)
    {
        try
        {
            var attendee = Get(info, "Attendee");
            if (attendee is not null) _attendees.Remove(Convert.ToInt32(Get(attendee, "Id")));
        }
        catch (Exception ex) { _log.Write($"[RDP] 이탈 이벤트 확인 실패: {ex.GetType().Name}"); }
    }
    private void OnControlRequested(object attendee, int requested)
    {
        try
        {
            if (_attendees.ContainsKey(Convert.ToInt32(Get(attendee, "Id")))) Set(attendee, "ControlLevel", 2);
            else Call(attendee, "TerminateConnection");
        }
        catch (Exception ex) { _log.Write($"[RDP] 제어 권한 요청 거부 실패: {ex.GetType().Name}"); }
    }
    private void SweepExpired()
    {
        foreach (var entry in _invitations.Values.Where(x => x.Expires <= DateTimeOffset.UtcNow &&
            !_attendees.Values.Any(a => a.InvitationId == x.Id)).ToArray())
            try { Revoke(entry.Id); } catch (Exception ex) { _log.Write($"[RDP] 만료 초대 정리 실패: {ex.GetType().Name}"); }
    }
    public async Task StopAsync(CancellationToken cancellationToken = default) => await Run(() =>
    {
        _expiryTimer?.Stop();
        _expiryTimer = null;
        if (_session is null) return true;
        var errors = new List<Exception>();
        foreach (var id in _invitations.Keys.ToArray())
            try { Revoke(id); } catch (Exception ex) { errors.Add(ex); }
        try { Call(_session, "Close"); } catch (Exception ex) { errors.Add(ex); }
        Unsubscribe();
        foreach (var invitation in _invitations.Values) Release(invitation.Com);
        _invitations.Clear();
        _attendees.Clear();
        Release(_session);
        _session = null;
        _sharingId = _sessionId = Guid.Empty;
        if (errors.Count > 0) throw new AggregateException("WDS 종료 중 오류", errors);
        _log.Write("[RDP] 공유 종료");
        return true;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            _disposed = true;
            (await _ready.Task.ConfigureAwait(false)).BeginInvokeShutdown(DispatcherPriority.Send);
            await Task.Run(() => _thread.Join()).ConfigureAwait(false);
        }
    }
    private void Subscribe(int id, Delegate handler)
    {
        ComEventsHelper.Combine(_session!, EventsId, id, handler);
        _subscriptions.Add((id, handler));
    }
    private void Unsubscribe()
    {
        foreach (var item in _subscriptions) ComEventsHelper.Remove(_session!, EventsId, item.Id, item.Handler);
        _subscriptions.Clear();
    }
    private static object? Call(object target, string name, params object[] args) => target.GetType().InvokeMember(
        name, BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, target, args);
    private static object? Get(object target, string name) => target.GetType().InvokeMember(
        name, BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, target, null);
    private static void Set(object target, string name, object value) => target.GetType().InvokeMember(
        name, BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance, null, target, new[] { value });
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
