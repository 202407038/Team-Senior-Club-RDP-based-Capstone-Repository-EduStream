using System.Collections.Concurrent;
using EduStream.Core.Models;

namespace EduStream.Server.Services;

public sealed partial class SessionManager
{
    private readonly ConcurrentDictionary<string, Guid> _directChatClients = new();
    public event Action<DirectChatPacket>? DirectChatReceived;
    public IReadOnlyList<DirectChatRecipient> DirectChatRecipients => _directChatClients
        .Where(x => _clientDisplayNames.ContainsKey(x.Key))
        .Select(x => new DirectChatRecipient(x.Value, _clientDisplayNames.GetValueOrDefault(x.Key, "")))
        .Where(x => x.Name.Length != 0).OrderBy(x => x.Name).ToArray();

    public async Task SendDirectChatAsync(Guid connectionId, string message)
    {
        if (CurrentSession is null || string.IsNullOrWhiteSpace(message) || message.Length > MaxChatMessageLength)
            throw new InvalidOperationException("세션과 메시지 길이를 확인해 주세요.");
        var client = _directChatClients.FirstOrDefault(x => x.Value == connectionId);
        if (client.Key is null || !_clientDisplayNames.ContainsKey(client.Key))
            throw new InvalidOperationException("학생 연결이 바뀌었거나 개인 채팅을 지원하지 않습니다. 대상을 다시 선택해 주세요.");
        var packet = new DirectChatPacket
        {
            SessionId = CurrentSession.SessionId, SenderId = "Server", Sender = ProfessorDisplayName,
            StudentConnectionId = connectionId, FromProfessor = true, Message = message
        };
        // 사라진 연결은 다른 학생이나 같은 이름의 새 연결로 대체하지 않는다.
        await _tcpServer.SendRequiredToClientAsync(client.Key, packet);
    }

    private async Task HandleDirectChatAsync(string clientId, DirectChatPacket request)
    {
        if (CurrentSession is null || request.SessionId != CurrentSession.SessionId ||
            !_directChatClients.TryGetValue(clientId, out var connectionId) ||
            !_clientDisplayNames.TryGetValue(clientId, out var name) ||
            (request.StudentConnectionId != Guid.Empty && request.StudentConnectionId != connectionId) ||
            string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > MaxChatMessageLength)
        {
            await _tcpServer.SendToClientAsync(clientId, CreateError("DIRECT_CHAT_REJECTED",
                "개인 채팅의 세션·참가자·메시지를 확인해 주세요.", true, request));
            return;
        }
        // Sender/FromProfessor는 학생 주장을 무시하고 승인된 연결로 확정한다.
        var accepted = new DirectChatPacket
        {
            SessionId = CurrentSession.SessionId, CorrelationId = request.CorrelationId,
            SenderId = clientId, Sender = name, StudentConnectionId = connectionId,
            FromProfessor = false, Message = request.Message
        };
        DirectChatReceived?.Invoke(accepted);
        // 교수자 처리 후 보낸 학생에게만 되돌린다. 다른 학생에게는 전송하지 않는다.
        await _tcpServer.SendToClientAsync(clientId, accepted);
    }
}

public sealed record DirectChatRecipient(Guid ConnectionId, string Name)
{
    // 승인된 표시 이름은 학번을 포함하며 동시 중복 참가가 거부된다.
    // 기록 키와 송신 연결 ID를 분리한다. 이 키는 인증 수단이 아니다.
    public string HistoryKey => "student:" + Name;
}
