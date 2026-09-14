using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;

namespace EduStream.Server.Services;

/// <summary>
/// Windows Desktop Sharing API를 사용한 교수자 화면 공유 서비스.
/// 내부에서 STA/UI 스레드 호출과 COM 수명을 관리합니다.
/// </summary>
public sealed class RdpSharingService : IRdpSharingService
{
    /// <summary>
    /// 초대 정보를 추적하기 위한 내부 클래스
    /// </summary>
    private sealed class InvitationInfo
    {
        public Guid InvitationId { get; init; }
        public string ParticipantId { get; init; } = string.Empty;
        public Guid ConnectionId { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public bool IsRevoked { get; set; }
    }

    private readonly ILogSink _logSink;
    private readonly object _lock = new();
    private object? _rdpSession;
    private Guid _sharingId;
    private bool _disposed;
    private readonly Dictionary<Guid, InvitationInfo> _invitations = new(); // InvitationId -> InvitationInfo
    private const int MaxAttendees = 2;
    private readonly Func<object?> _rdpSessionFactory; // 테스트 대역을 위한 팩토리

    public RdpSharingService(ILogSink logSink) : this(logSink, CreateRdpSession) { }

    // 테스트 대역을 위한 생성자
    public RdpSharingService(ILogSink logSink, Func<object?> rdpSessionFactory)
    {
        _logSink = logSink;
        _rdpSessionFactory = rdpSessionFactory;
    }

    // 실제 RDPSession COM 객체 생성
    private static object? CreateRdpSession()
    {
        var rdpSessionType = Type.GetTypeFromProgID("RDPSession");
        if (rdpSessionType is null)
        {
            throw new PlatformNotSupportedException("RDPSession COM 객체를 찾을 수 없습니다. Windows Desktop Sharing API가 설치되지 않았거나 지원되지 않는 플랫폼입니다.");
        }

        var rdpSession = Activator.CreateInstance(rdpSessionType);
        if (rdpSession is null)
        {
            throw new InvalidOperationException("RDPSession 생성 실패");
        }

        return rdpSession;
    }

    public async Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<Guid>();

        var staThread = new Thread(() =>
        {
            try
            {
                lock (_lock)
                {
                    if (_rdpSession is not null)
                    {
                        tcs.SetException(new InvalidOperationException("공유가 이미 시작되었습니다. 먼저 StopAsync를 호출하여 종료해야 합니다."));
                        return;
                    }

                    try
                    {
                        // WDS RDPSession 생성 (STA 스레드에서 실행)
                        _rdpSession = _rdpSessionFactory();
                        if (_rdpSession is null)
                        {
                            throw new InvalidOperationException("RDPSession 생성 실패");
                        }

                        // RDPSession 초기화 (실제 WDS 메서드 호출)
                        // Open 메서드를 호출하여 공유 세션 시작
                        var rdpSessionType = _rdpSession.GetType();
                        var openMethod = rdpSessionType.GetMethod("Open");
                        if (openMethod is not null)
                        {
                            openMethod.Invoke(_rdpSession, null);
                            _logSink.Write("[RDP] RDPSession.Open() 호출 성공");
                        }

                        _sharingId = Guid.NewGuid();
                        _logSink.Write($"[RDP] 공유 시작: SessionId={sessionId}, SharingId={_sharingId}");

                        tcs.SetResult(_sharingId);
                    }
                    catch (Exception ex)
                    {
                        _logSink.Write($"[RDP] 공유 시작 실패: {ex.Message}");
                        tcs.SetException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        return await tcs.Task;
    }

    public async Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId,
        string participantId, Guid connectionId, string invitationPassword,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            lock (_lock)
            {
                if (_rdpSession is null)
                {
                    throw new InvalidOperationException("공유가 시작되지 않았습니다.");
                }

                if (_sharingId != sharingId)
                {
                    throw new InvalidOperationException("SharingId가 일치하지 않습니다.");
                }

                try
                {
                    // 활성 초대 수 확인 (다중 학생 공유 지원)
                    var activeInvitations = _invitations.Values.Count(i => !i.IsRevoked && i.ExpiresAt > DateTimeOffset.UtcNow);
                    if (activeInvitations >= MaxAttendees)
                    {
                        throw new InvalidOperationException($"최대 참가자 수({MaxAttendees})를 초과했습니다.");
                    }

                    var invitationId = Guid.NewGuid();

                    // WDS 초대 생성 (실제 WDS 메서드 호출)
                    var rdpSessionType = _rdpSession?.GetType();
                    if (rdpSessionType is not null)
                    {
                        // CreateAttendee 메서드를 호출하여 초대 생성
                        var createAttendeeMethod = rdpSessionType.GetMethod("CreateAttendee");
                        if (createAttendeeMethod is not null)
                        {
                            var attendee = createAttendeeMethod.Invoke(_rdpSession, null);
                            _logSink.Write("[RDP] RDPSession.CreateAttendee() 호출 성공");
                        }
                    }

                    // 비밀번호는 별도 경로로 전송
                    var connectionString = $"rdp://invitation:{invitationId}";

                    // 초대 정보 추적
                    _invitations[invitationId] = new InvitationInfo
                    {
                        InvitationId = invitationId,
                        ParticipantId = participantId,
                        ConnectionId = connectionId,
                        ExpiresAt = expiresAt,
                        IsRevoked = false
                    };

                    _logSink.Write($"[RDP] 초대 생성: ParticipantId={participantId}, InvitationId={invitationId}, 활성 초대={activeInvitations + 1}/{MaxAttendees}");

                    var invitationPacket = new RdpInvitationPacket
                    {
                        SessionId = sessionId,
                        SharingId = sharingId,
                        InvitationId = invitationId,
                        ConnectionId = connectionId,
                        ParticipantId = participantId,
                        ConnectionString = connectionString,
                        ExpiresAt = expiresAt,
                        ContractVersion = 1,
                        Provider = "windows-desktop-sharing",
                        ViewOnly = true,
                        DataLength = System.Text.Encoding.UTF8.GetByteCount(connectionString)
                    };

                    return invitationPacket;
                }
                catch (Exception ex)
                {
                    _logSink.Write($"[RDP] 초대 생성 실패: {ex.Message}");
                    throw;
                }
            }
        }, cancellationToken);
    }

    public async Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            lock (_lock)
            {
                if (_rdpSession is null)
                {
                    return;
                }

                try
                {
                    // 초대 정보 추적 업데이트
                    if (_invitations.TryGetValue(invitationId, out var invitation))
                    {
                        invitation.IsRevoked = true;
                        _logSink.Write($"[RDP] 초대 폐기: InvitationId={invitationId}, ParticipantId={invitation.ParticipantId}");
                    }
                    else
                    {
                        _logSink.Write($"[RDP] 초대 폐기: InvitationId={invitationId} (존재하지 않음)");
                    }

                    // WDS 초대 폐기 (실제 구현에서는 COM 메서드 호출 필요)
                }
                catch (Exception ex)
                {
                    _logSink.Write($"[RDP] 초대 폐기 실패: {ex.Message}");
                }
            }
        }, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>();

        var staThread = new Thread(() =>
        {
            try
            {
                lock (_lock)
                {
                    if (_rdpSession is null)
                    {
                        tcs.SetResult(true);
                        return;
                    }

                    try
                    {
                        // 모든 초대 정리
                        _invitations.Clear();

                        // WDS 공유 종료 (실제 WDS 메서드 호출)
                        if (_rdpSession is not null)
                        {
                            var rdpSessionType = _rdpSession.GetType();
                            var closeMethod = rdpSessionType.GetMethod("Close");
                            if (closeMethod is not null)
                            {
                                closeMethod.Invoke(_rdpSession, null);
                                _logSink.Write("[RDP] RDPSession.Close() 호출 성공");
                            }

                            // 실제 COM 객체인 경우에만 ReleaseComObject 호출
                            if (System.Runtime.InteropServices.Marshal.IsComObject(_rdpSession))
                            {
                                System.Runtime.InteropServices.Marshal.ReleaseComObject(_rdpSession);
                            }
                        }
                        _rdpSession = null;
                        _sharingId = Guid.Empty;

                        _logSink.Write("[RDP] 공유 종료");
                        tcs.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        _logSink.Write($"[RDP] 공유 종료 실패: {ex.Message}");
                        tcs.SetException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        await tcs.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync();
        _disposed = true;
    }
}
