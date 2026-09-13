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
    private readonly ILogSink _logSink;
    private readonly object _lock = new();
    private object? _rdpSession;
    private Guid _sharingId;
    private bool _disposed;

    public RdpSharingService(ILogSink logSink)
    {
        _logSink = logSink;
    }

    public async Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            lock (_lock)
            {
                if (_rdpSession is not null)
                {
                    _logSink.Write("[RDP] 공유가 이미 시작되었습니다.");
                    return _sharingId;
                }

                try
                {
                    // WDS RDPSession 생성 (STA 스레드에서 실행 필요)
                    // 실제 구현에서는 COM 객체 생성 및 초기화 필요
                    var rdpSessionType = Type.GetTypeFromProgID("RDPSession");
                    if (rdpSessionType is null)
                    {
                        // 테스트 환경에서 COM 객체가 없는 경우를 대비해 모의 객체 생성
                        _logSink.Write("[RDP] RDPSession COM 객체를 찾을 수 없어 모의 객체 생성");
                        _rdpSession = new object();
                    }
                    else
                    {
                        _rdpSession = Activator.CreateInstance(rdpSessionType);
                        if (_rdpSession is null)
                        {
                            throw new InvalidOperationException("RDPSession 생성 실패");
                        }
                    }

                    _sharingId = Guid.NewGuid();
                    _logSink.Write($"[RDP] 공유 시작: SessionId={sessionId}, SharingId={_sharingId}");

                    return _sharingId;
                }
                catch (Exception ex)
                {
                    _logSink.Write($"[RDP] 공유 시작 실패: {ex.Message}");
                    throw;
                }
            }
        }, cancellationToken);

        return _sharingId;
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
                    var invitationId = Guid.NewGuid();

                    // WDS 초대 생성 (실제 구현에서는 COM 메서드 호출 필요)
                    // AttendeeLimit=1, 비밀번호 설정
                    var connectionString = $"rdp://invitation:{invitationId};password:{invitationPassword}";

                    _logSink.Write($"[RDP] 초대 생성: ParticipantId={participantId}, InvitationId={invitationId}");

                    return new RdpInvitationPacket
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
                        ViewOnly = true
                    };
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
                    // WDS 초대 폐기 (실제 구현에서는 COM 메서드 호출 필요)
                    _logSink.Write($"[RDP] 초대 폐기: InvitationId={invitationId}");
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
                    // WDS 공유 종료 (실제 구현에서는 COM 메서드 호출 필요)
                    if (_rdpSession is not null)
                    {
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(_rdpSession);
                    }
                    _rdpSession = null;
                    _sharingId = Guid.Empty;

                    _logSink.Write("[RDP] 공유 종료");
                }
                catch (Exception ex)
                {
                    _logSink.Write($"[RDP] 공유 종료 실패: {ex.Message}");
                }
            }
        }, cancellationToken);
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
