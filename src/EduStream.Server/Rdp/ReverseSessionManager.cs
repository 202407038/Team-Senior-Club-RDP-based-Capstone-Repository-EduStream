using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

public sealed class ReverseSessionManager : IReverseSessionManager, IDisposable
{
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<Guid, ReverseInvitationPacket> _invitations = new();
    
    // [프로젝트 규칙 적용] 깃배쉬 호환성을 위해 dynamic을 사용한 Late Binding으로 실제 엔진 호출
    private dynamic? _rdpSession; 
    
    private Guid _reverseSharingId = Guid.Empty;
    private string _hostStudentId = string.Empty;
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

    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    public Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            if (_state != ReverseSessionState.Inactive)
                throw new InvalidOperationException("역방향 공유가 이미 활성화되어 있습니다.");

            try
            {
                Type? rdpType = Type.GetTypeFromProgID("RDPCOMAPILib.RDPSession");
                if (rdpType == null) throw new InvalidOperationException("WDS 엔진을 찾을 수 없습니다.");
                
                _rdpSession = Activator.CreateInstance(rdpType);
                if (_rdpSession == null)
                {
                    throw new InvalidOperationException("WDS 세션이 만들어지지 않았습니다.");
                }
                
                // 🌟 [진짜 WDS 연동 추가] 화면 공유 품질 및 제어 속성 초기화
                _rdpSession.ColorDepth = 24; // 24비트 트루컬러 지원
                
                _rdpSession.Open(); 
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("실제 WDS 세션을 여는 데 실패했습니다.", ex);
            }

            _reverseSharingId = Guid.NewGuid();
            _hostStudentId = studentId;
            _state = ReverseSessionState.Hosting;

            return Task.FromResult(_reverseSharingId);
        }
    }

    public Task<ReverseInvitationPacket> CreateProfessorInvitationAsync(
        Guid sessionId, Guid sharingId, string professorId, Guid connectionId, 
        string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            var safeSession = _rdpSession;

            if (_state != ReverseSessionState.Hosting)
                throw new InvalidOperationException("역방향 공유가 호스팅 상태가 아닙니다.");

            if (safeSession == null)
                throw new InvalidOperationException("WDS 세션이 만들어지지 않았습니다.");

            dynamic rdpInvitation = safeSession.Invitations.CreateInvitation(
                "ProfessorGroup",
                invitationPassword,
                1
            );

            var invitationId = Guid.NewGuid();
            var invitation = new ReverseInvitationPacket
            {
                SessionId = sessionId,
                SharingId = sharingId,
                ProfessorId = professorId,
                ConnectionId = connectionId,
                ConnectionString = rdpInvitation.ConnectionString, 
                ExpiresAt = expiresAt,
                HostStudentId = _hostStudentId
            };

            _invitations[invitationId] = invitation;
            return Task.FromResult(invitation);
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock) { if (_state == ReverseSessionState.Hosting) _state = ReverseSessionState.Connecting; }
        return Task.CompletedTask;
    }

    public Task OnConnectedAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock) 
        { 
            if (_state == ReverseSessionState.Connecting) 
            {
                _state = ReverseSessionState.Connected; 
                
                // 🌟 [진짜 WDS 연동 추가] 교수가 접속 완료된 시점에 마우스/키보드 제어권(Interactive) 강제 부여!
                if (_rdpSession != null)
                {
                    try
                    {
                        // 엔진에 접속된 모든 참석자(교수)에게 제어 권한 2(CTRL_LEVEL_INTERACTIVE) 할당
                        foreach (dynamic attendee in _rdpSession.Attendees)
                        {
                            attendee.ControlLevel = 2; 
                        }
                        _state = ReverseSessionState.ControlGranted; // 권한 부여 완료 상태로 쐐기
                    }
                    catch { /* 테스트 환경 등 엔진이 껍데기일 때 터지는 것 방지 */ }
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task OnConnectionFailedAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock) { _state = ReverseSessionState.Failed; }
        return Task.CompletedTask;
    }

    public Task OnDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock) { _state = ReverseSessionState.Disconnected; }
        return Task.CompletedTask;
    }

    public void ReceiveFrame(byte[] frameData)
    {
        lock (_stateLock)
        {
            if (_state != ReverseSessionState.Connected && _state != ReverseSessionState.ControlGranted) return;
        }
        FrameReceived?.Invoke(this, new FrameReceivedEventArgs { FrameData = frameData, Timestamp = DateTimeOffset.UtcNow });
    }

    public Task StopReverseSharingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            if (_rdpSession != null)
            {
                try
                {
                    _rdpSession.Close();
                    Marshal.ReleaseComObject(_rdpSession);
                }
                catch { /* 무시 */ }
                finally
                {
                    _rdpSession = null;
                }
            }

            _invitations.Clear();
            _reverseSharingId = Guid.Empty;
            _hostStudentId = string.Empty;
            _state = ReverseSessionState.Inactive;
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            StopReverseSharingAsync().Wait();
            _isDisposed = true;
        }
    }
}