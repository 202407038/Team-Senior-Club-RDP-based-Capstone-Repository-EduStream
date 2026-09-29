using System.Text;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;

namespace EduStream.FileTransfer.Tests;

public sealed class FinalReadinessWireTests
{
    [Fact]
    public void FileMessages_RoundTripWithoutLeakingLocalPaths()
    {
        var session = Guid.NewGuid();
        var file = new SessionFileDescriptor(session, Guid.NewGuid(), 1, "강의.txt", 0, new string('a', 64), 65536);
        var catalog = RoundTrip(new SessionFileCatalogSnapshot(session, 1, new[] { file }));
        Assert.Equal(file, catalog.Files.Single());
        var request = new SessionFileRequest(Guid.NewGuid(), session, file.FileId, 1);
        Assert.Equal(request, RoundTrip(request));
        var chunk = RoundTrip(new SessionFileChunk(request.RequestId, session, file.FileId, 1, 0, new byte[0]));
        Assert.Empty(chunk.Content);
        Assert.Equal(request.RequestId, chunk.RequestId);
        var cancel = new FileCancelRequest(request.RequestId, session);
        Assert.Equal(cancel, RoundTrip(cancel));
        var stored = new FileStoredNotice(request.RequestId, file.FileId, 0, new string('a', 64));
        Assert.Equal(stored, RoundTrip(stored));
        var failure = new CollaborationFailureNotice(request.RequestId, CollaborationError.NotAuthorized);
        Assert.Equal(failure, RoundTrip(failure));
    }

    [Fact]
    public void ParticipantSnapshot_RoundTrips()
    {
        var connection = new ParticipantConnection(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
        var room = new RoomJoined(connection, 1,
            new[] { new ParticipantSnapshot(connection, "학생", true, true, true, 1) });
        var result = RoundTrip(room);
        Assert.Equal(room.Connection, result.Connection);
        Assert.Equal(room.Participants.Single(), result.Participants.Single());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("id")]
    [InlineData("json")]
    [InlineData("duplicate")]
    [InlineData("missingPayload")]
    [InlineData("nullPayload")]
    public void BadEnvelopeRejected(string fault)
    {
        var request = new FileCancelRequest(Guid.NewGuid(), Guid.NewGuid());
        var json = Encoding.UTF8.GetString(CollaborationMessageCodec.Encode(Guid.NewGuid(), request));
        json = fault switch
        {
            "version" => json.Replace("\"Version\":1", "\"Version\":999"),
            "kind" => json.Replace("\"Kind\":5", "\"Kind\":999"),
            "id" => json.Replace(request.SessionId.ToString(), Guid.Empty.ToString()),
            "json" => "{",
            "missingPayload" => json.Replace("\"Payload\":", "\"Ignored\":"),
            "nullPayload" => System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 1, MessageId = Guid.NewGuid(), Kind = 5, Payload = (object?)null
            }),
            _ => json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1")
        };
        Assert.Throws<CollaborationException>(() =>
            CollaborationMessageCodec.Decode<FileCancelRequest>(Encoding.UTF8.GetBytes(json), out _));
    }

    [Fact]
    public void OversizedFrameAndUnsupportedDtoAreRejected()
    {
        Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Decode<FileCancelRequest>(
            new byte[CollaborationMessageCodec.MaxFrameBytes + 1], out _));
        Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Encode(Guid.NewGuid(), new { Password = "never-send" }));
        var encoded = CollaborationMessageCodec.Encode(Guid.NewGuid(), new FileCancelRequest(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Decode<SessionFileRequest>(encoded, out _));
    }

    [Fact]
    public void NullParticipantIdentityIsRejectedAsContractError()
    {
        Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Encode(Guid.NewGuid(),
            new RoomJoined(null!, 1, Array.Empty<ParticipantSnapshot>())));
        var connection = new ParticipantConnection(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
        Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Encode(Guid.NewGuid(),
            new RoomJoined(connection, 1, new ParticipantSnapshot[] { null! })));
    }

    private static T RoundTrip<T>(T payload) where T : notnull
    {
        var id = Guid.NewGuid();
        var result = CollaborationMessageCodec.Decode<T>(CollaborationMessageCodec.Encode(id, payload), out var decodedId);
        Assert.Equal(id, decodedId);
        return result;
    }
}
