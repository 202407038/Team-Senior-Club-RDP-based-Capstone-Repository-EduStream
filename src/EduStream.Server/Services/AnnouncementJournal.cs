using EduStream.Core.Models;

namespace EduStream.Server.Services;

/// <summary>현재 수업의 공지와 실제 학생 응답. 송신 자체를 수신/확인으로 세지 않는다.</summary>
public sealed class AnnouncementJournal
{
    private readonly object _sync = new();
    private Guid _session;
    private readonly List<Notice> _notices = new();
    private sealed record RecipientState(string Name, AnnouncementReceipt Receipt);
    private sealed record Notice(ChatPacket Packet, Dictionary<string, RecipientState> Recipients);
    public event Action? Changed;
    public void Reset(Guid session)
    {
        lock (_sync) { _session = session; _notices.Clear(); }
        Changed?.Invoke();
    }
    public void Publish(ChatPacket packet, IEnumerable<AnnouncementRecipient> recipients)
    {
        lock (_sync)
        {
            if (_session == Guid.Empty || packet.SessionId != _session) throw new InvalidOperationException("열린 세션의 공지만 보낼 수 있습니다.");
            if (string.IsNullOrWhiteSpace(packet.Message) || packet.Message.Length > 500) throw new ArgumentException("공지는 1~500자여야 합니다.");
            packet.AnnouncementId = Guid.NewGuid();
            packet.AnnouncementReceipt = AnnouncementReceipt.None;
            packet.IsAnnouncementHistory = false;
            packet.IsSystemMessage = false;
            packet.SentAt = DateTimeOffset.UtcNow;
            _notices.Add(new Notice(Copy(packet, false), recipients.DistinctBy(x => x.Key)
                .ToDictionary(x => x.Key, x => new RecipientState(x.Name, AnnouncementReceipt.None))));
        }
        Changed?.Invoke();
    }
    public ChatPacket[] Replay(Guid session, AnnouncementRecipient recipient)
    {
        ChatPacket[] result;
        lock (_sync)
        {
            if (session != _session || session == Guid.Empty) return [];
            foreach (var notice in _notices) notice.Recipients.TryAdd(recipient.Key, new(recipient.Name, AnnouncementReceipt.None));
            result = _notices.Select(x => Copy(x.Packet, true)).ToArray();
        }
        Changed?.Invoke();
        return result;
    }
    public bool Accept(Guid? session, Guid? id, string verifiedConnection, AnnouncementReceipt receipt)
    {
        lock (_sync)
        {
            if (session != _session || receipt is not (AnnouncementReceipt.Received or AnnouncementReceipt.Confirmed)) return false;
            var notice = _notices.FirstOrDefault(x => x.Packet.AnnouncementId == id);
            if (notice is null || !notice.Recipients.TryGetValue(verifiedConnection, out var previous)) return false;
            if (receipt > previous.Receipt) notice.Recipients[verifiedConnection] = previous with { Receipt = receipt };
        }
        Changed?.Invoke();
        return true;
    }
    public IReadOnlyList<AnnouncementDeliverySummary> Snapshot()
    {
        lock (_sync) return _notices.AsEnumerable().Reverse().Select(n => new AnnouncementDeliverySummary(
            n.Packet.AnnouncementId!.Value, n.Packet.Message, n.Packet.IsImportantAnnouncement,
            n.Recipients.Count, n.Recipients.Count(x => x.Value.Receipt >= AnnouncementReceipt.Received),
            n.Recipients.Count(x => x.Value.Receipt == AnnouncementReceipt.Confirmed),
            string.Join(" · ", n.Recipients.Select(x => $"{x.Value.Name}: {Label(x.Value.Receipt)}")))).ToArray();
    }
    // 이름만 같은 새 참가는 이전 확인을 물려받지 않는다. 검증된 재접속 티켓일 때만 연결한다.
    public void RebindVerifiedReconnect(string previousConnection, string currentConnection)
    {
        lock (_sync)
            foreach (var notice in _notices)
                if (notice.Recipients.Remove(previousConnection, out var previous))
                    notice.Recipients[currentConnection] = previous;
        Changed?.Invoke();
    }
    private static string Label(AnnouncementReceipt receipt) => receipt switch
    { AnnouncementReceipt.Received => "수신", AnnouncementReceipt.Confirmed => "확인", _ => "수신 미확인" };
    private static ChatPacket Copy(ChatPacket p, bool history) => new()
    {
        SessionId = p.SessionId, Sender = p.Sender, SenderId = p.SenderId, Message = p.Message,
        DataLength = p.DataLength, IsAnnouncement = true, AnnouncementId = p.AnnouncementId,
        IsImportantAnnouncement = p.IsImportantAnnouncement, IsAnnouncementHistory = history, SentAt = p.SentAt
    };
}

public sealed record AnnouncementRecipient(string Key, string Name);

public sealed record AnnouncementDeliverySummary(Guid Id, string Text, bool IsImportant, int Targets,
    int Received, int Confirmed, string Details)
{
    public string Summary => $"참가별 대상 {Targets}건 · 수신 {Received}건 · 확인 {Confirmed}건";
}
