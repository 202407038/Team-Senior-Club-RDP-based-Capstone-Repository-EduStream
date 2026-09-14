using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;

namespace EduStream.Server.Services;

/// <summary>
/// Windows Desktop Sharing API를 사용한 교수자 화면 공유 서비스.
/// 단일 전용 STA 스레드 작업 큐를 통해 COM 생명주기 및 스레드 안전성을 보장합니다.
/// </summary>
public sealed class RdpSharingService : IRdpSharingService
{
    private sealed class InvitationInfo
    {
        public Guid InvitationId { get; init; }
        public string ParticipantId { get; init; } = string.Empty;
        public Guid ConnectionId { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public bool IsRevoked { get; set; }
        public object? ComInvitation { get; init; }
    }

    /// <summary>
    /// 모든 COM 작업을 격리 실행하는 단일 STA 백그라운드 워커
    /// </summary>
    private sealed class StaTaskRunner : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private bool _disposed;

        public StaTaskRunner()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "RdpSharingService-STA"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Run()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch
                {
                    // 예외는 TaskCompletionSource로 전달됨
                }
            }
        }

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(() =>
                {
                    try
                    {
                        tcs.SetResult(func());
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            return tcs.Task;
        }

        public Task InvokeAsync(Action action)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(() =>
                {
                    try
                    {
                        action();
                        tcs.SetResult();
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            return tcs.Task;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            _thread.Join(1500);
            _queue.Dispose();
        }
    }

    private readonly ILogSink _logSink;
    private readonly Func<object?> _rdpSessionFactory;
    private readonly StaTaskRunner _staRunner = new();
    private readonly Dictionary<Guid, InvitationInfo> _invitations = new();
    private const int MaxAttendees = 2;

    private object? _rdpSession;
    private Guid _sharingId;
    private bool _disposed;

    public RdpSharingService(ILogSink logSink) : this(logSink, CreateRdpSession) { }

    public RdpSharingService(ILogSink logSink, Func<object?> rdpSessionFactory)
    {
        _logSink = logSink;
        _rdpSessionFactory = rdpSessionFactory;
    }

    private static object? CreateRdpSession()
    {
        var rdpSessionType = Type.GetTypeFromCLSID(new Guid("9B78F0E6-3E05-4A5B-B2E8-E743A8956B65"), true);
        if (rdpSessionType is null)
        {
            throw new PlatformNotSupportedException("RDPSession COM 객체를 찾을 수 없습니다.");
        }

        var rdpSession = Activator.CreateInstance(rdpSessionType);
        if (rdpSession is null)
        {
            throw new InvalidOperationException("RDPSession 생성 실패");
        }

        return rdpSession;
    }

    public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return _staRunner.InvokeAsync(() =>
        {
            if (_rdpSession is not null)
            {
                throw new InvalidOperationException("공유가 이미 시작되었습니다. 먼저 StopAsync를 호출하여 종료해야 합니다.");
            }

            try
            {
                _rdpSession = _rdpSessionFactory();
                if (_rdpSession is null)
                {
                    throw new InvalidOperationException("RDPSession 생성 실패");
                }

                // 실제 WDS Open() 호출
                InvokeComMethod(_rdpSession, "Open");
                _logSink.Write("[RDP] RDPSession.Open() 호출 성공");

                _sharingId = Guid.NewGuid();
                _logSink.Write($"[RDP] 공유 시작: SessionId={sessionId}, SharingId={_sharingId}");
                return _sharingId;
            }
            catch (Exception ex)
            {
                _logSink.Write($"[RDP] 공유 시작 실패: {ex.Message}");
                if (_rdpSession is not null)
                {
                    TryReleaseCom(_rdpSession);
                    _rdpSession = null;
                }
                throw;
            }
        });
    }

    public Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId,
        string participantId, Guid connectionId, string invitationPassword,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        return _staRunner.InvokeAsync(() =>
        {
            if (_rdpSession is null)
            {
                throw new InvalidOperationException("공유가 시작되지 않았습니다.");
            }

            if (_sharingId != sharingId)
            {
                throw new InvalidOperationException("SharingId가 일치하지 않습니다.");
            }

            var activeInvitations = _invitations.Values.Count(i => !i.IsRevoked && i.ExpiresAt > DateTimeOffset.UtcNow);
            if (activeInvitations >= MaxAttendees)
            {
                throw new InvalidOperationException($"최대 참가자 수({MaxAttendees})를 초과했습니다.");
            }

            try
            {
                var invitations = GetComProperty(_rdpSession, "Invitations");
                if (invitations is null)
                {
                    throw new InvalidOperationException("WDS Invitations 관리자를 가져올 수 없습니다.");
                }

                // 전달받은 비밀번호를 그대로 사용
                var comInvitation = InvokeComMethod(invitations, "CreateInvitation", participantId, $"EduStream_{participantId}", invitationPassword ?? string.Empty, 1);
                if (comInvitation is null)
                {
                    throw new InvalidOperationException("WDS CreateInvitation 호출 결과가 null입니다.");
                }

                _logSink.Write("[RDP] Invitations.CreateInvitation() 호출 성공");

                // ConnectionString 속성 획득
                var connStrObj = GetComProperty(comInvitation, "ConnectionString");
                string connectionString = connStrObj?.ToString() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException("WDS 초대 문자열이 비어있습니다.");
                }

                _logSink.Write("[RDP] ConnectionString 획득 성공");

                var invitationId = Guid.NewGuid();
                _invitations[invitationId] = new InvitationInfo
                {
                    InvitationId = invitationId,
                    ParticipantId = participantId,
                    ConnectionId = connectionId,
                    ExpiresAt = expiresAt,
                    IsRevoked = false,
                    ComInvitation = comInvitation
                };

                // 단위 테스트 검증용 로그 포맷 유지 ("활성 초대=X/Y")
                _logSink.Write($"[RDP] 초대 생성: ParticipantId={participantId}, InvitationId={invitationId}, 활성 초대={activeInvitations + 1}/{MaxAttendees}");

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
                    ViewOnly = true,
                    DataLength = System.Text.Encoding.UTF8.GetByteCount(connectionString)
                };
            }
            catch (Exception ex)
            {
                _logSink.Write($"[RDP] 초대 생성 실패: {ex.Message}");
                throw;
            }
        });
    }

    public Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        return _staRunner.InvokeAsync(() =>
        {
            if (_rdpSession is null) return;

            if (_invitations.TryGetValue(invitationId, out var invitation))
            {
                invitation.IsRevoked = true;
                _logSink.Write($"[RDP] 초대 폐기: InvitationId={invitationId}, ParticipantId={invitation.ParticipantId}");

                DisconnectAttendee(invitation.ParticipantId);

                if (invitation.ComInvitation is not null)
                {
                    try
                    {
                        SetComProperty(invitation.ComInvitation, "Revoked", true);
                        _logSink.Write("[RDP] COM 초대 Revoked 설정 성공");
                    }
                    catch (Exception ex)
                    {
                        _logSink.Write($"[RDP] Revoked 설정 경고: {ex.Message}");
                    }

                    TryReleaseCom(invitation.ComInvitation);
                    _logSink.Write("[RDP] COM 초대 객체 해제 성공");
                }
            }
            else
            {
                _logSink.Write($"[RDP] 초대 폐기: InvitationId={invitationId} (존재하지 않음)");
            }
        });
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _staRunner.InvokeAsync(() =>
        {
            if (_rdpSession is null) return;

            try
            {
                DisconnectAttendee(null);

                foreach (var invitation in _invitations.Values)
                {
                    if (invitation.ComInvitation is not null)
                    {
                        try { SetComProperty(invitation.ComInvitation, "Revoked", true); } catch { }
                        TryReleaseCom(invitation.ComInvitation);
                    }
                }
                _invitations.Clear();

                if (_rdpSession is not null)
                {
                    try
                    {
                        InvokeComMethod(_rdpSession, "Close");
                        _logSink.Write("[RDP] RDPSession.Close() 호출 성공");
                    }
                    catch (Exception ex)
                    {
                        _logSink.Write($"[RDP] Close() 호출 중 오류: {ex.Message}");
                    }

                    TryReleaseCom(_rdpSession);
                }

                _rdpSession = null;
                _sharingId = Guid.Empty;

                // 단위 테스트 검증용 필수 로그 ("공유 종료")
                _logSink.Write("[RDP] 공유 종료");
            }
            catch (Exception ex)
            {
                _logSink.Write($"[RDP] 공유 종료 실패: {ex.Message}");
                throw;
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync();
        _staRunner.Dispose();
        _disposed = true;
    }

    private void DisconnectAttendee(string? participantId)
    {
        if (_rdpSession is null) return;

        try
        {
            var attendees = GetComProperty(_rdpSession, "Attendees");
            if (attendees is System.Collections.IEnumerable enumerable)
            {
                foreach (var attendee in enumerable)
                {
                    try
                    {
                        var remoteName = GetComProperty(attendee, "RemoteName")?.ToString();
                        if (string.IsNullOrEmpty(participantId) || remoteName == participantId)
                        {
                            InvokeComMethod(attendee, "TerminateConnection");
                            _logSink.Write($"[RDP] Attendee 연결 해제 완료: {remoteName}");
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logSink.Write($"[RDP] Attendee 정리 중 예외 (무시 가능): {ex.Message}");
        }
    }

    #region COM & Reflection 유틸리티

    private static object? InvokeComMethod(object target, string methodName, params object[] args)
    {
        var type = target.GetType();
        var method = type.GetMethod(methodName);
        if (method is not null)
        {
            return method.Invoke(target, args);
        }
        return type.InvokeMember(methodName, BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, target, args);
    }

    private static object? GetComProperty(object target, string propertyName)
    {
        var type = target.GetType();
        var prop = type.GetProperty(propertyName);
        if (prop is not null)
        {
            return prop.GetValue(target);
        }
        return type.InvokeMember(propertyName, BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, target, null);
    }

    private static void SetComProperty(object target, string propertyName, object value)
    {
        var type = target.GetType();
        var prop = type.GetProperty(propertyName);
        if (prop is not null)
        {
            prop.SetValue(target, value);
            return;
        }
        type.InvokeMember(propertyName, BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance, null, target, new[] { value });
    }

    private static void TryReleaseCom(object obj)
    {
        if (Marshal.IsComObject(obj))
        {
            Marshal.ReleaseComObject(obj);
        }
    }

    #endregion
}