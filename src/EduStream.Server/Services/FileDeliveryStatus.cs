namespace EduStream.Server.Services;

public sealed record FileDeliveryStatus(Guid ConnectionId, Guid RequestId, Guid FileId, string Student,
    string FileName, string Status, bool IsTerminal)
{
    public string Summary => $"{Student} · {FileName} · {Status}";
}
