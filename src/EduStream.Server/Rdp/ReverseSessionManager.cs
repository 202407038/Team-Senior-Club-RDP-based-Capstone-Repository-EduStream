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

            // 🎯 [피드백 2번 반영] 단순 뭉뚱그림 방지: 레지스트리(CLSID) 미존재와 실제 Open 실패를 엄격히 구분하여 예외 처리
            Type? rdpType = Type.GetTypeFromProgID("RDPCOMAPILib.RDPSession");
            if (rdpType == null)
                throw new NotSupportedException("WDS 엔진(RDPCOMAPILib.RDPSession)이 레지스트리에 등록되지 않았습니다. 현재 OS(Windows Home 등)에서 지원하지 않습니다.");

            try
            {
                _rdpSession = Activator.CreateInstance(rdpType);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("WDS 세션 인스턴스를 생성할 수 없습니다. (COM 활성화 실패)", ex);
            }

            try
            {
                // C# 컴파일러에게 "이거 절대 null 아니니까 안심하고 실행해"라고 알려주는 느낌표(!) 추가
                _rdpSession!.ColorDepth = 24;
                _rdpSession!.Open();
            }
            catch (Exception ex)
            {
                // 실제 Open 시 터지는 네트워크/OS 제한 에러를 명확히 상위로 던짐
                throw new InvalidOperationException($"실제 WDS 세션(Open)을 여는 데 실패했습니다: {ex.Message}", ex);
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

            // 🎯 [피드백 3번 반영] 기존에 있던 sharingId 일치 검증 로직 복구
            if (_reverseSharingId != sharingId)
                throw new InvalidOperationException("현재 진행 중인 공유 ID와 요청한 공유 ID가 일치하지 않습니다.");

            // 🎯 [피드백 3번 반영] MS 공식 명세에 맞춰 인자 4개(AuthString, GroupName, Password, AttendeeLimit)로 정확히 호출
            dynamic rdpInvitation = safeSession.Invitations.CreateInvitation(
                "", // AuthString (기본 빈 문자열 사용)
                "ProfessorGroup", // GroupName
                invitationPassword, // Password
                1 // AttendeeLimit
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

                if (_rdpSession != null)
                {
                    try
                    {
                        int grantedCount = 0;

                        // 🎯 [피드백 4번 반영] ControlLevel 2(보기 전용) -> 3(마우스 조작 가능)으로 수정
                        foreach (dynamic attendee in _rdpSession.Attendees)
                        {
                            attendee.ControlLevel = 3; // CTRL_LEVEL_INTERACTIVE
                            grantedCount++;
                        }

                        // 🎯 [피드백 4번 반영] 참석자가 0명인데 성공(ControlGranted)으로 넘어가는 꼼수 차단
                        if (grantedCount > 0)
                        {
                            _state = ReverseSessionState.ControlGranted;
                        }
                        else
                        {
                            throw new InvalidOperationException("접속한 참석자가 없어 원격 제어 권한을 부여할 수 없습니다.");
                        }
                    }
                    catch (Exception ex)
                    {
                        // 🎯 [피드백 4번 반영] 권한 처리 예외를 catch에서 무시하지 않고 실패 상태로 롤백 후 에러 던짐
                        _state = ReverseSessionState.Failed;
                        throw new InvalidOperationException($"원격 제어 권한(ControlLevel=3) 부여 중 예외 발생: {ex.Message}", ex);
                    }
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