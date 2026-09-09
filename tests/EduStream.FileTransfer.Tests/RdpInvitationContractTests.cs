using System.Text;
using EduStream.Core.Models;
using EduStream.Core.Utils;
using EduStream.Core.Serialization;

namespace EduStream.FileTransfer.Tests;

public sealed class RdpInvitationContractTests
{
    private readonly Guid _session = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    private RdpInvitationPacket Invitation(DateTimeOffset? expires = null, bool viewOnly = true,
        int? length = null, string ticket = "opaque-초대") => new()
    {
        SessionId = _session, SenderId = "Server", ParticipantId = "Student01",
        SharingId = Guid.NewGuid(), InvitationId = Guid.NewGuid(), ConnectionId = _connection,
        ConnectionString = ticket, ExpiresAt = expires ?? _now.AddMinutes(5),
        DataLength = length ?? Encoding.UTF8.GetByteCount(ticket), ViewOnly = viewOnly
    };

    [Fact]
    public void RoundTrip_PreservesOpaqueTicketAndBindsToCurrentAttempt()
    {
        var serializer = new PacketSerializer();
        var packet = serializer.Deserialize<RdpInvitationPacket>(serializer.Serialize(Invitation()))!;
        RdpInvitationContract.Validate(packet, _session, "Student01", _connection, _now);
        Assert.Equal("opaque-초대", packet.ConnectionString);
        Assert.DoesNotContain(packet.ConnectionString, packet.ToString());
        Assert.DoesNotContain("Password", Encoding.UTF8.GetString(serializer.Serialize(packet)));
        Assert.Throws<ArgumentException>(() => RdpInvitationContract.Validate(packet, _session, "Student02", _connection, _now));
        Assert.Throws<ArgumentException>(() => RdpInvitationContract.Validate(packet, Guid.NewGuid(), "Student01", _connection, _now));
        Assert.Throws<ArgumentException>(() => RdpInvitationContract.Validate(packet, _session, "Student01", Guid.NewGuid(), _now));
    }

    [Fact]
    public void ExpiredInteractiveAndMalformedInvitationsAreRejected()
    {
        foreach (var packet in new[] {
            Invitation(expires: _now), Invitation(viewOnly: false), Invitation(length: 1),
            Invitation(ticket: ""), Invitation(ticket: new string('x', 65537)) })
            Assert.Throws<ArgumentException>(() => RdpInvitationContract.Validate(packet, _session, "Student01", _connection, _now));
    }

    [Fact]
    public void RequestMustMatchAuthenticatedParticipantAndSession()
    {
        var request = new RdpInvitationRequestPacket {
            SessionId = _session, SenderId = "Student01", ParticipantId = "Student01", ConnectionId = _connection };
        RdpInvitationContract.ValidateRequest(request, _session, "Student01");
        Assert.Throws<ArgumentException>(() => RdpInvitationContract.ValidateRequest(request, _session, "Student02"));
        Assert.Throws<ArgumentException>(() => RdpInvitationContract.ValidateRequest(request, Guid.NewGuid(), "Student01"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RevocationOnlyAppliesToCurrentInvitation(bool current)
    {
        var active = Invitation();
        var revoked = new RdpInvitationRevokedPacket {
            SessionId = _session, ParticipantId = "Student01", ConnectionId = _connection,
            InvitationId = current ? active.InvitationId : Guid.NewGuid() };
        Assert.Equal(current, RdpInvitationContract.AppliesTo(revoked, active));
    }

    [Theory]
    [InlineData(PacketType.RdpInvitation)]
    [InlineData(PacketType.RdpInvitationRequest)]
    [InlineData(PacketType.RdpInvitationRevoked)]
    public void NewControlTypesAreKnownWithoutChangingExistingValues(PacketType type)
    {
        Assert.True(PacketContractUtility.IsKnownPacketType(type));
        Assert.Equal(5, (int)PacketType.Screen);
        Assert.Equal(8, (int)PacketType.Heartbeat);
    }
}
