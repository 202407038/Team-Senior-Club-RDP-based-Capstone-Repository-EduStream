using System.IO;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Core.Network;

namespace EduStream.Server.Services;

public sealed partial class SessionManager
{
    private readonly string _submissionDirectory;
    public SubmissionStore? Submissions { get; private set; }
    public event Action? SubmissionsChanged;
    private void InitializeSubmissions(Guid session)
    {
        Submissions = new(session, _submissionDirectory);
        Submissions.Changed += () => SubmissionsChanged?.Invoke();
    }
    public async Task SetSubmissionsOpenAsync(bool open)
    {
        var store = Submissions ?? throw new InvalidOperationException("세션을 먼저 열어 주세요.");
        store.SetOpen(open);
        var frame = CollaborationMessageCodec.Encode(Guid.NewGuid(), new SubmissionMessage(store.SessionId, Guid.Empty, SubmissionAction.State, IsOpen: open));
        foreach (var peer in _secureConnections.ToArray()) await SendSecureAsync(peer.Value, frame, peer.Key);
    }
    private async Task HandleSubmissionAsync(string clientId, SecureCollaborationConnection secure, byte[] frame)
    {
        var student = _participantRegistry.TryGetConnection(clientId);
        var store = Submissions;
        if (student is null || store is null || !_clientDisplayNames.TryGetValue(clientId, out var name)) return;
        var message = CollaborationMessageCodec.Decode<SubmissionMessage>(frame, out _);
        var response = store.Handle(student.ConnectionId, name, message);
        await SendSecureAsync(secure, CollaborationMessageCodec.Encode(Guid.NewGuid(), response), clientId);
    }
}
