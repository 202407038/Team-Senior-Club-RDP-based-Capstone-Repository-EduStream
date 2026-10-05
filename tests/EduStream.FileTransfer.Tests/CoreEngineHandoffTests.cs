using System.Drawing;
using System.Text;
using System.Text.Json.Nodes;
using EduStream.Core.Collaboration;
using EduStream.Server.Rdp;

namespace EduStream.FileTransfer.Tests;

public sealed class CoreEngineHandoffTests
{
    private static ReverseRdpInvitationNotice Invitation() => ReverseInvitationWire.From(new ReverseInvitationPacket
    {
        SessionId = Guid.NewGuid(), SharingId = Guid.NewGuid(), InvitationId = Guid.NewGuid(),
        ConnectionId = Guid.NewGuid(), ProfessorId = "professor", HostStudentId = "student",
        ConnectionString = "연결문자열", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
    }).ToContract();

    [Theory]
    [InlineData(ReverseRdpControlMode.ViewOnly)]
    [InlineData(ReverseRdpControlMode.HostGrantedInteractive)]
    public void ReverseInvitation_EngineCodecEngineRoundTripPreservesEveryField(ReverseRdpControlMode mode)
    {
        var expected = Invitation() with { ControlMode = mode, ViewOnly = mode == ReverseRdpControlMode.ViewOnly };
        var id = Guid.NewGuid();
        var decoded = CollaborationMessageCodec.Decode<ReverseRdpInvitationNotice>(
            CollaborationMessageCodec.Encode(id, expected), out var actualId);
        Assert.Equal(id, actualId);
        Assert.Equal(expected, decoded);
        Assert.Equal(expected, ReverseInvitationWire.From(ReverseInvitationWire.FromContract(decoded).ToPacket()).ToContract());
        decoded.ValidateForConnection(expected.SessionId, expected.ProfessorId, expected.ConnectionId,
            expected.StudentId, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(expected.ConnectionString, expected.ToString());
    }

    [Theory]
    [InlineData("session")]
    [InlineData("student")]
    [InlineData("professor")]
    [InlineData("connection")]
    [InlineData("expiry")]
    public void ReverseInvitation_RejectsDifferentConnectionContext(string mismatch)
    {
        var n = Invitation();
        Assert.Throws<ArgumentException>(() => n.ValidateForConnection(
            mismatch == "session" ? Guid.NewGuid() : n.SessionId,
            mismatch == "professor" ? "other" : n.ProfessorId,
            mismatch == "connection" ? Guid.NewGuid() : n.ConnectionId,
            mismatch == "student" ? "other" : n.StudentId,
            mismatch == "expiry" ? n.ExpiresAt : DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("direction")]
    [InlineData("mode")]
    [InlineData("participant")]
    [InlineData("missing")]
    public void ReverseInvitation_RejectsMalformedWireBeforeEngine(string malformed)
    {
        var root = JsonNode.Parse(CollaborationMessageCodec.Encode(Guid.NewGuid(), Invitation()))!;
        var payload = root["Payload"]!;
        switch (malformed)
        {
            case "length": payload["DataLength"] = 1; break;
            case "direction": payload["Direction"] = "professor-to-student"; break;
            case "mode": payload["ViewOnly"] = true; break;
            case "participant": payload["ParticipantId"] = "student"; break;
            case "missing": payload.AsObject().Remove("SharingId"); break;
        }
        Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Decode<ReverseRdpInvitationNotice>(
            Encoding.UTF8.GetBytes(root.ToJsonString()), out _));
    }

    [Fact]
    public void ReverseSecret_IsBoundToOneInvitationAndRedacted()
    {
        var n = Invitation();
        var secret = new ReverseRdpInvitationSecretNotice(n.SessionId, n.SharingId, n.InvitationId,
            n.ConnectionId, n.StudentId, n.ProfessorId, "private-password", n.ExpiresAt);
        var decoded = CollaborationMessageCodec.Decode<ReverseRdpInvitationSecretNotice>(
            CollaborationMessageCodec.Encode(Guid.NewGuid(), secret), out _);
        Assert.Equal(secret, decoded);
        decoded.ValidateForInvitation(n, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => decoded.ValidateForInvitation(n with { InvitationId = Guid.NewGuid() }, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => decoded.ValidateForInvitation(n with { SharingId = Guid.NewGuid() }, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => decoded.ValidateForInvitation(n with { ConnectionId = Guid.NewGuid() }, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => decoded.ValidateForInvitation(n, n.ExpiresAt));
        Assert.DoesNotContain(secret.Password, secret.ToString());
    }

    [Fact]
    public void SharingRestart_SameConnectionRejectsPreviousSharingGeneration()
    {
        var old = Invitation();
        var current = old with { SharingId = Guid.NewGuid(), InvitationId = Guid.NewGuid() };
        current.ValidateForSharing(current.SessionId, current.SharingId, current.ProfessorId,
            current.ConnectionId, current.StudentId, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => old.ValidateForSharing(current.SessionId, current.SharingId,
            current.ProfessorId, current.ConnectionId, current.StudentId, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => current.ValidateForSharing(current.SessionId, Guid.Empty,
            current.ProfessorId, current.ConnectionId, current.StudentId, DateTimeOffset.UtcNow));
    }

    private static AnnotationStroke Stroke() => new()
    {
        ParticipantId = "professor", Tool = EduStream.Server.Rdp.AnnotationTool.Rectangle,
        Points = new[] { new Point(-1920, 0), new Point(100, 200) }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Annotation_ExistingEngineJsonSurvivesCommonCodec(bool layer)
    {
        var stroke = Stroke();
        var json = layer ? AnnotationLayerWire.ToJson(new AnnotationLayerSnapshot
        {
            Change = AnnotationLayerChange.Undone, IsVisible = true, ContentRevision = 2,
            VisibleStrokes = new[] { stroke }
        }) : AnnotationStrokeWire.ToJson(stroke);
        var sender = new ParticipantConnection(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var n = new AnnotationTransportNotice(sender.SessionId, Guid.NewGuid(), sender.ConnectionId, 3, json);
        var decoded = CollaborationMessageCodec.Decode<AnnotationTransportNotice>(
            CollaborationMessageCodec.Encode(Guid.NewGuid(), n), out _);
        Assert.Equal(n, decoded);
        decoded.ValidateForSender(sender, n.SharingId, 2);
        var restored = layer ? AnnotationLayerWire.FromJson(decoded.PayloadJson).VisibleStrokes.Single()
            : AnnotationStrokeWire.FromJson(decoded.PayloadJson);
        Assert.Equal(stroke.StrokeId, restored.StrokeId);
        Assert.Equal(stroke.Points, restored.Points);
        Assert.Throws<CollaborationException>(() => decoded.ValidateForSender(sender, n.SharingId, 3));
        Assert.Throws<CollaborationException>(() => decoded.ValidateForSender(sender with { Role = ParticipantRole.Student }, n.SharingId, 0));
        Assert.Throws<CollaborationException>(() => decoded.ValidateForSender(sender with { ConnectionId = Guid.NewGuid() }, n.SharingId, 0));
        Assert.Throws<CollaborationException>(() => decoded.ValidateForSender(sender, Guid.NewGuid(), 0));
    }

    [Theory]
    [InlineData("points")]
    [InlineData("tool")]
    [InlineData("width")]
    [InlineData("duplicate")]
    [InlineData("oversize")]
    [InlineData("one_point")]
    [InlineData("ellipse_alias")]
    [InlineData("highlighter")]
    public void Annotation_RejectsMalformedOrExcessivePayload(string kind)
    {
        var root = JsonNode.Parse(AnnotationStrokeWire.ToJson(Stroke()))!;
        if (kind == "points") root["Points"] = JsonNode.Parse("[[1]]");
        if (kind == "tool") root["Tool"] = "999";
        if (kind == "width") root["StrokeWidth"] = 0;
        if (kind == "one_point") root["Points"] = JsonNode.Parse("[[1,2]]");
        if (kind == "ellipse_alias") root["Tool"] = "Ellipse";
        if (kind == "highlighter") root["Tool"] = "Highlighter";
        var json = root.ToJsonString();
        if (kind == "duplicate") json = json.Insert(1, "\"Tool\":\"Pen\",");
        if (kind == "oversize") json = new string('x', AnnotationTransportNotice.MaxPayloadBytes + 1);
        var notice = new AnnotationTransportNotice(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, json);
        Assert.Throws<ArgumentException>(notice.Validate);
    }
}
