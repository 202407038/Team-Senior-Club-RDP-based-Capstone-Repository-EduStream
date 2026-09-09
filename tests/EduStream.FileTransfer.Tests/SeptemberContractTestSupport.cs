using EduStream.Core.Factories;
using EduStream.Core.Models;
using EduStream.Core.Serialization;
namespace EduStream.FileTransfer.Tests;

internal static class SeptemberContractTestSupport
{
    public static T RoundTrip<T>(T packet) where T : BasePacket =>
        new PacketSerializer().Deserialize<T>(new PacketSerializer().Serialize(packet))!;

    public static RdpConnectionStatus ConnectedStudent(Guid sessionId, string participantId)
    {
        var connection = Guid.NewGuid();
        return RdpConnectionStatus.Create(sessionId, participantId).BeginConnect(connection).Connected(connection);
    }

    public static ChatPacket StudentChat(Guid sessionId, string participantId, string message) =>
        PacketFactory.CreateChat(participantId, participantId, message, sessionId);
}
