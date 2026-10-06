using EduStream.Core.Collaboration;
using EduStream.Core.Logging;

namespace EduStream.Client.Services;

/// <summary>#88의 공유 수명을 재사용하고 #89 명령을 실제 학생 호스트에 연결하는 앱 어댑터.</summary>
public sealed class StudentSharingSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly StudentReverseShareService _sharing;
    private readonly ICollaborationChannel _channel;
    private ParticipantSnapshot? _self;
    private bool _localAllowViewing = true;
    private bool _localAllowControl = true;
    private bool _disposed;
    public event Action<string>? StatusChanged;

    public StudentSharingSession(ReverseCollaborationClient routing, ICollaborationChannel channel, ILogSink log)
    {
        _channel = channel;
        _sharing = new StudentReverseShareService(routing, log);
        _sharing.SharingChanged += (_, message) => StatusChanged?.Invoke(message);
    }

    public async Task ApplyRoomAsync(RoomJoined room)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            _self = room.Participants.FirstOrDefault(p => p.Connection == room.Connection);
            await _sharing.UpdateAsync(_self is { Connected: true, AllowViewing: true });
            if (!_localAllowControl || _self is not { AllowControl: true })
                await _sharing.RevokeCurrentInputAsync();
        }
        finally { _lifecycle.Release(); }
    }

    public async Task HandleInputAsync(RemoteInputCommandNotice command)
    {
        RemoteInputResultNotice result;
        await _lifecycle.WaitAsync();
        try
        {
            result = await _sharing.ApplyInputAsync(command, !_disposed && _localAllowViewing && _localAllowControl &&
                _self is { Connected: true, AllowViewing: true, AllowControl: true });
        }
        finally { _lifecycle.Release(); }
        await _channel.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), result));
    }

    public async Task ApplyLocalPermissionsAsync(bool allowViewing, bool allowControl)
    {
        await _lifecycle.WaitAsync();
        try
        {
            _localAllowViewing = allowViewing;
            _localAllowControl = allowViewing && allowControl;
            await _sharing.SetLocalViewingAllowedAsync(allowViewing);
            if (!_localAllowControl) await _sharing.RevokeCurrentInputAsync();
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try { _disposed = true; await _sharing.DisposeAsync(); }
        finally { _lifecycle.Release(); }
    }
}
