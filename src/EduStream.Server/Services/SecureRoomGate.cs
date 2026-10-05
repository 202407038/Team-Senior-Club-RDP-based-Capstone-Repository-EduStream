using System.Security.Cryptography;
using System.Text;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.Core.Network;

namespace EduStream.Server.Services;

/// <summary>
/// 2번 구현: 보호 채널로 들어온 학생의 참가 인증(방 비밀번호)을 처리하고, 기존 TCP 참가 요청에 한 번만 쓸 수 있는
/// 참가 티켓을 발급합니다. 티켓을 쓴 뒤에는 보호 채널이 그 참가자의 연결로 묶입니다.
/// </summary>
/// <remarks>
/// 비밀번호 없는 방도 TLS 채널에서 이 게이트를 거쳐 참가 티켓과 세션 권한을 발급받습니다.
/// LAN 모드에서는 수동 접속 코드로 서버 신원을 대조하지 않습니다.
/// 현재 단계에서는 티켓이 평문 v1 TCP 참가 요청에 실립니다. 일회용·30초·이름 고정이라 재사용은 막지만,
/// 채팅·화면 등 v1 트래픽 자체의 암호화는 이 PR 범위가 아닙니다.
/// </remarks>
/// <summary>티켓 소비 결과. 재연결로 발급된 티켓이면 ReconnectContext에 검증기가 돌려준 값이 담깁니다.</summary>
public sealed record SecureRedemption(SecureCollaborationConnection Connection, object? ReconnectContext);

public sealed class SecureRoomGate : IAsyncDisposable
{
    public static readonly TimeSpan DefaultAuthTimeout = TimeSpan.FromSeconds(15);
    private const int TicketBytes = 32;

    private sealed class State(SecureCollaborationConnection connection)
    {
        public SecureCollaborationConnection Connection { get; } = connection;
        public bool TicketIssued { get; set; }
        public bool Bound { get; set; }
    }

    private sealed record Ticket(string DisplayName, State State, DateTimeOffset ExpiresAt, object? ReconnectContext);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, State> _states = new(); // 보호 채널 연결 ID → 인증 상태
    private readonly Dictionary<string, Ticket> _tickets = new(StringComparer.Ordinal); // 티켓 SHA256 → 발급 정보
    private readonly Guid _sessionId;
    private readonly RoomPasswordVerifier? _password;
    private readonly ILogSink _logSink;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _authTimeout;
    private readonly TimeSpan _ticketLifetime;
    private bool _disposed;

    /// <summary>티켓을 쓴 뒤(참가 완료 후) 받은 프레임입니다. 파일·목록 라우팅이 구독합니다.</summary>
    public event Func<SecureCollaborationConnection, byte[], Task>? BoundFrameReceived;

    /// <summary>보호 채널이 닫혔을 때 발생합니다. 참가자와 묶여 있었다면 기존 TCP 연결도 정리해야 합니다.</summary>
    public event Action<SecureCollaborationConnection>? ConnectionClosed;

    /// <summary>
    /// 재연결 토큰 검증기(이름, 토큰 → 복원 정보 또는 null). 유효하면 비밀번호 확인을 건너뛰고 토큰을 소비해야 합니다.
    /// 지정하지 않으면 재연결 요청은 모두 거부합니다.
    /// </summary>
    public Func<string, string, Task<object?>>? ReconnectValidator { get; set; }

    public SecureRoomGate(SecureCollaborationListener listener, Guid sessionId, RoomPasswordVerifier? password,
        ILogSink logSink, TimeSpan? authTimeout = null, TimeSpan? ticketLifetime = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        CollaborationContract.RequireId(sessionId, nameof(sessionId));
        _sessionId = sessionId;
        _password = password;
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _authTimeout = authTimeout ?? DefaultAuthTimeout;
        _ticketLifetime = ticketLifetime ?? RoomAuthRules.TicketLifetime;
        _timeProvider = timeProvider ?? TimeProvider.System;
        listener.ConnectionAccepted += OnConnectionAcceptedAsync;
    }

    public int PendingTicketCount
    {
        get { lock (_gate) return _tickets.Count; }
    }

    /// <summary>
    /// 기존 TCP 참가 요청의 티켓을 확인하고 소비합니다. 이름이 다르거나 만료·재사용·연결 종료면 null이며,
    /// 어떤 경우든 한 번 제시된 티켓은 다시 쓸 수 없습니다.
    /// </summary>
    public SecureRedemption? Redeem(string? joinTicket, string displayName)
    {
        if (string.IsNullOrEmpty(joinTicket) || joinTicket.Length > RoomAuthRules.MaxTicketLength) return null;
        var key = HashTicket(joinTicket);
        lock (_gate)
        {
            if (_disposed || !_tickets.Remove(key, out var ticket)) return null;
            if (ticket.ExpiresAt <= _timeProvider.GetUtcNow() ||
                !string.Equals(ticket.DisplayName, displayName, StringComparison.Ordinal) ||
                ticket.State.Connection.IsClosed || ticket.State.Bound)
                return null;
            ticket.State.Bound = true;
            return new SecureRedemption(ticket.State.Connection, ticket.ReconnectContext);
        }
    }

    private Task OnConnectionAcceptedAsync(SecureCollaborationConnection connection)
    {
        var state = new State(connection);
        lock (_gate)
        {
            if (_disposed) return connection.DisposeAsync().AsTask();
            _states.Add(connection.Id, state);
        }
        _ = connection.Completion.ContinueWith(_ => OnClosed(state), TaskScheduler.Default);
        connection.Start(frame => OnFrameAsync(state, frame));
        _ = CloseIfNotBoundAsync(state);
        return Task.CompletedTask;
    }

    private async Task OnFrameAsync(State state, byte[] frame)
    {
        bool bound, ticketIssued;
        lock (_gate) (bound, ticketIssued) = (state.Bound, state.TicketIssued);

        if (bound)
        {
            var handler = BoundFrameReceived;
            if (handler is not null) await handler(state.Connection, frame);
            return;
        }
        if (ticketIssued) return; // 티켓 사용 전 프레임은 받지 않는다.

        RoomAuthRequest request;
        try
        {
            if (CollaborationFrameInspector.PeekKind(frame) != CollaborationMessageKind.RoomAuthRequest)
                throw new CollaborationException(CollaborationError.NotAuthorized);
            request = CollaborationMessageCodec.Decode<RoomAuthRequest>(frame, out _);
        }
        catch (CollaborationException ex)
        {
            // 인증 전에는 참가 인증 요청 외의 메시지를 받지 않는다.
            _logSink.Write($"[SecureJoin] 인증 전 잘못된 프레임, 연결 종료: connection={state.Connection.Id}, 사유={ex.Code}");
            await state.Connection.DisposeAsync();
            return;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
        }

        await AuthenticateAsync(state, request);
    }

    private async Task AuthenticateAsync(State state, RoomAuthRequest request)
    {
        RoomPasswordResult verdict;
        object? reconnectContext = null;
        var reconnectRejected = false;
        var chars = new char[Encoding.UTF8.GetCharCount(request.Password)];
        try
        {
            if (request.ReconnectToken is { } token)
            {
                // 재연결은 비밀번호 대신 끊긴 참가자에게 준 일회용 토큰으로 확인한다(U03 비밀번호 재입력 없음).
                var validator = ReconnectValidator;
                reconnectContext = validator is null ? null : await validator(request.DisplayName, token);
                reconnectRejected = reconnectContext is null;
                verdict = reconnectRejected ? RoomPasswordResult.Rejected : RoomPasswordResult.Accepted;
            }
            else
            {
                Encoding.UTF8.GetChars(request.Password, chars);
                // 무비밀번호 방 정책만 허용한다. 이 뒤의 참가 티켓 발급/사용과 권한 검사는 생략하지 않는다.
                verdict = _password?.Verify(state.Connection.RemoteAddress, chars) ?? RoomPasswordResult.Accepted;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(request.Password);
            Array.Clear(chars);
        }

        RoomAuthResult result;
        if (verdict == RoomPasswordResult.Accepted)
        {
            var ticket = Base64Url(RandomNumberGenerator.GetBytes(TicketBytes));
            lock (_gate)
            {
                if (_disposed || state.TicketIssued || state.Connection.IsClosed) return;
                state.TicketIssued = true;
                _tickets[HashTicket(ticket)] = new Ticket(request.DisplayName, state,
                    _timeProvider.GetUtcNow() + _ticketLifetime, reconnectContext);
            }
            result = new RoomAuthResult(request.AttemptId, true, null, ticket, _sessionId);
            _logSink.Write($"[SecureJoin] 참가 인증 성공, 티켓 발급: connection={state.Connection.Id}");
        }
        else
        {
            var error = reconnectRejected ? CollaborationError.StaleConnection
                : verdict == RoomPasswordResult.LockedOut ? CollaborationError.ResourceLimit
                : CollaborationError.NotAuthorized;
            result = new RoomAuthResult(request.AttemptId, false, error, null, null);
            _logSink.Write($"[SecureJoin] 참가 인증 거부: connection={state.Connection.Id}, 원격={state.Connection.RemoteAddress}, 사유={verdict}");
        }

        try
        {
            await state.Connection.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), result));
        }
        catch (Exception ex)
        {
            _logSink.Write($"[SecureJoin] 인증 결과 전달 실패: connection={state.Connection.Id}, {ex.GetType().Name}");
            await state.Connection.DisposeAsync();
            return;
        }

        // 잠긴 주소는 같은 연결로 계속 시도하지 못하게 끊는다. 단순 불일치는 같은 연결에서 다시 입력할 수 있다.
        if (verdict == RoomPasswordResult.LockedOut) await state.Connection.DisposeAsync();
    }

    /// <summary>인증을 끝내지 않았거나, 티켓을 받고도 TCP 참가를 끝내지 않은 연결을 닫습니다.</summary>
    private async Task CloseIfNotBoundAsync(State state)
    {
        try
        {
            await Task.Delay(_authTimeout + _ticketLifetime, _timeProvider);
        }
        catch (ObjectDisposedException) { return; }

        bool bound;
        lock (_gate) bound = state.Bound;
        if (!bound && !state.Connection.IsClosed)
        {
            _logSink.Write($"[SecureJoin] 참가 미완료로 연결 종료: connection={state.Connection.Id}");
            await state.Connection.DisposeAsync();
        }
    }

    private void OnClosed(State state)
    {
        lock (_gate)
        {
            _states.Remove(state.Connection.Id);
            foreach (var key in _tickets.Where(pair => ReferenceEquals(pair.Value.State, state)).Select(pair => pair.Key).ToArray())
                _tickets.Remove(key);
        }
        ConnectionClosed?.Invoke(state.Connection);
    }

    private static string HashTicket(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(ticket)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        SecureCollaborationConnection[] connections;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            connections = _states.Values.Select(state => state.Connection).ToArray();
            _tickets.Clear();
        }
        foreach (var connection in connections) await connection.DisposeAsync();
    }
}
