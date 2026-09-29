using System.Collections.Concurrent;
using EduStream.Core.Collaboration;

namespace EduStream.Server.Services;

/// <summary>
/// 2번 구현: 실제 승인된 TCP 연결(clientId)마다 ParticipantConnection을 새로 발급하고
/// 보기/제어 권한과 목록 revision을 서버 기준으로 관리합니다.
/// 표시 이름이나 클라이언트가 보낸 role은 신뢰하지 않고, 이 레지스트리가 유일한 진실입니다.
/// </summary>
public sealed class ParticipantRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ParticipantConnection> _byClientId = new();
    private readonly Dictionary<Guid, ParticipantSnapshot> _byConnectionId = new();
    private long _revision;

    /// <summary>
    /// 참가자 목록이나 개별 권한이 바뀔 때마다 발생합니다. UI 폴링/브로드캐스트 트리거용입니다.
    /// </summary>
    public event Action? RosterChanged;

    /// <summary>
    /// 한 참가자의 보기/제어 허용이 바뀐 뒤 발생합니다. 인자는 변경 후 스냅샷입니다.
    /// 원격 제어 조정자는 이 이벤트로 진행 중인 승인을 회수합니다.
    /// </summary>
    public event Action<ParticipantSnapshot>? PermissionsChanged;

    /// <summary>
    /// 연결이 레지스트리에서 사라진 뒤 발생합니다(이탈·끊김·재접속 교체·전체 정리).
    /// </summary>
    public event Action<ParticipantConnection>? ConnectionRemoved;

    public long Revision
    {
        get { lock (_gate) return _revision; }
    }

    public IReadOnlyList<ParticipantSnapshot> Participants
    {
        get { lock (_gate) return _byConnectionId.Values.ToArray(); }
    }

    /// <summary>
    /// 참가 승인된 연결 하나에 새 ParticipantConnection을 발급합니다.
    /// 같은 clientId로 재호출하면 이전 연결을 정리하고 새로 발급합니다(재접속).
    /// U07 확정 요구에 따라 교수자 제어 허용은 기본 ON이며, 참가 시 안내·표시는 5번 UI가 담당합니다.
    /// </summary>
    public ParticipantConnection Join(string clientId, Guid sessionId, string displayName, ParticipantRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        CollaborationContract.RequireId(sessionId, nameof(sessionId));
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80)
            throw new CollaborationException(CollaborationError.InvalidRequest);

        var connection = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), role);
        var snapshot = new ParticipantSnapshot(connection, displayName, Connected: true,
            AllowViewing: true, AllowControl: true, PermissionRevision: 0);

        ParticipantConnection? replaced;
        lock (_gate)
        {
            if (_byClientId.TryGetValue(clientId, out replaced))
            {
                _byConnectionId.Remove(replaced.ConnectionId);
            }
            _byClientId[clientId] = connection;
            _byConnectionId[connection.ConnectionId] = snapshot;
            _revision++;
        }
        if (replaced is not null) ConnectionRemoved?.Invoke(replaced);
        RosterChanged?.Invoke();
        return connection;
    }

    /// <summary>
    /// 연결 끊김/이탈 시 호출합니다. 제어 승인은 ConnectionRemoved 구독자가 회수하며,
    /// RDP 초대는 호출자가 별도로 회수해야 합니다.
    /// </summary>
    public ParticipantConnection? Disconnect(string clientId)
    {
        ParticipantConnection? removed;
        lock (_gate)
        {
            if (!_byClientId.Remove(clientId, out removed)) return null;
            _byConnectionId.Remove(removed.ConnectionId);
            _revision++;
        }
        ConnectionRemoved?.Invoke(removed);
        RosterChanged?.Invoke();
        return removed;
    }

    /// <summary>
    /// 학생이 자신의 보기/제어 허용을 직접 켜고 끌 때 호출합니다.
    /// AllowControl은 AllowViewing이 꺼지면 함께 꺼집니다(보기 없는 제어는 없습니다).
    /// </summary>
    public bool SetPermissions(Guid connectionId, bool allowViewing, bool allowControl)
    {
        ParticipantSnapshot updated;
        lock (_gate)
        {
            if (!_byConnectionId.TryGetValue(connectionId, out var current)) return false;
            updated = current with
            {
                AllowViewing = allowViewing,
                AllowControl = allowViewing && allowControl,
                PermissionRevision = current.PermissionRevision + 1
            };
            _byConnectionId[connectionId] = updated;
            _revision++;
        }
        PermissionsChanged?.Invoke(updated);
        RosterChanged?.Invoke();
        return true;
    }

    public ParticipantConnection? TryGetConnection(string clientId)
    {
        lock (_gate) return _byClientId.TryGetValue(clientId, out var connection) ? connection : null;
    }

    /// <summary>
    /// 지금 이 순간의 저장된 상태를 반환합니다. 알 수 없는 연결이면 null이며,
    /// 호출자는 이를 "권한 없음/미인증"으로 처리해야 합니다(예외로 승격하지 않습니다).
    /// </summary>
    public ParticipantSnapshot? TryResolve(Guid connectionId)
    {
        lock (_gate) return _byConnectionId.TryGetValue(connectionId, out var snapshot) ? snapshot : null;
    }

    /// <summary>
    /// 지정한 시청자 기준 RoomJoined 스냅샷을 만듭니다. viewer 본인이 현재 레지스트리에 없으면
    /// (이미 이탈/교체된 연결이면) null입니다.
    /// </summary>
    public RoomJoined? Snapshot(ParticipantConnection viewer)
    {
        lock (_gate)
        {
            if (!_byConnectionId.ContainsKey(viewer.ConnectionId)) return null;
            return new RoomJoined(viewer, _revision, _byConnectionId.Values.ToArray());
        }
    }

    public void Clear()
    {
        ParticipantConnection[] removed;
        lock (_gate)
        {
            if (_byClientId.Count == 0 && _byConnectionId.Count == 0) return;
            removed = _byConnectionId.Values.Select(snapshot => snapshot.Connection).ToArray();
            _byClientId.Clear();
            _byConnectionId.Clear();
            _revision++;
        }
        foreach (var connection in removed) ConnectionRemoved?.Invoke(connection);
        RosterChanged?.Invoke();
    }
}
