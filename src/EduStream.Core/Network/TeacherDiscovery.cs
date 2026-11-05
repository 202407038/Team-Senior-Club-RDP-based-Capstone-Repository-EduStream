using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace EduStream.Core.Network;

/// <summary>접속 후보 안내 전용. 인증/비밀번호/자동 참가를 대체하지 않는다.</summary>
public static class TeacherDiscoveryProtocol
{
    public const int Port = 45050;
    public const string Protocol = "EduStream.Discovery/1";
    public static byte[] Query(Guid nonce) => JsonSerializer.SerializeToUtf8Bytes(new DiscoveryQuery(Protocol, nonce));
    public static Guid? ReadQuery(byte[] bytes)
    {
        if (bytes.Length > 256) return null;
        try
        {
            var query = JsonSerializer.Deserialize<DiscoveryQuery>(bytes);
            return query is { Nonce: var nonce } && query.Protocol == Protocol && nonce != Guid.Empty ? nonce : null;
        }
        catch (JsonException) { return null; }
    }
    public static byte[] Reply(Guid nonce, Guid sessionId, string computerName, int port, bool passwordRequired) =>
        JsonSerializer.SerializeToUtf8Bytes(new DiscoveryReply(Protocol, nonce, sessionId, computerName, port, passwordRequired));
    public static DiscoveredTeacher? ReadReply(byte[] bytes, IPEndPoint sender, Guid expectedNonce)
    {
        if (bytes.Length > 1024 || sender.Address.AddressFamily != AddressFamily.InterNetwork) return null;
        try
        {
            var reply = JsonSerializer.Deserialize<DiscoveryReply>(bytes);
            if (reply is null || reply.Protocol != Protocol || reply.Nonce != expectedNonce ||
                reply.SessionId == Guid.Empty || reply.Port is < 1 or > 65534 ||
                string.IsNullOrWhiteSpace(reply.ComputerName) || reply.ComputerName.Length > 80 || reply.ComputerName.Any(char.IsControl)) return null;
            return new(reply.SessionId, reply.ComputerName, sender.Address.ToString(), reply.Port, reply.PasswordRequired);
        }
        catch (JsonException) { return null; }
    }
    private sealed record DiscoveryQuery(string Protocol, Guid Nonce);
    private sealed record DiscoveryReply(string Protocol, Guid Nonce, Guid SessionId, string ComputerName, int Port, bool PasswordRequired);
}

public sealed record DiscoveredTeacher(Guid SessionId, string ComputerName, string Address, int Port, bool PasswordRequired)
{
    public string Label => $"{ComputerName} · {Address}:{Port}" + (PasswordRequired ? " · 비밀번호 있음" : " · 비밀번호 없음");
}
