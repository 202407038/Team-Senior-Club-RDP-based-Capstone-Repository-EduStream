using EduStream.Core.Models;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public class AnnouncementJournalTests
{
    [Fact]
    public void PublishingIsNotReceivingAndConfirmationsAreMonotonicAndBound()
    {
        var journal = new AnnouncementJournal(); var session = Guid.NewGuid(); journal.Reset(session);
        var packet = new ChatPacket { SessionId = session, IsAnnouncement = true, Message = "안내", IsImportantAnnouncement = true };
        journal.Publish(packet, [new("학생A", "학생A"), new("학생B", "학생B")]);
        Assert.Equal(0, journal.Snapshot()[0].Received);
        Assert.False(journal.Accept(Guid.NewGuid(), packet.AnnouncementId, "학생A", AnnouncementReceipt.Confirmed));
        Assert.False(journal.Accept(session, Guid.NewGuid(), "학생A", AnnouncementReceipt.Confirmed));
        Assert.False(journal.Accept(session, packet.AnnouncementId, "외부", AnnouncementReceipt.Confirmed));
        Assert.False(journal.Accept(session, packet.AnnouncementId, "학생A", (AnnouncementReceipt)99));
        Assert.True(journal.Accept(session, packet.AnnouncementId, "학생A", AnnouncementReceipt.Confirmed));
        journal.Accept(session, packet.AnnouncementId, "학생A", AnnouncementReceipt.Received);
        Assert.Equal(1, journal.Snapshot()[0].Received); Assert.Equal(1, journal.Snapshot()[0].Confirmed);
        var history = Assert.Single(journal.Replay(session, new("늦은학생", "늦은학생")));
        Assert.True(history.IsAnnouncementHistory); Assert.Equal(packet.AnnouncementId, history.AnnouncementId);
        history.Message = "변조";
        Assert.Equal("안내", journal.Snapshot()[0].Text); Assert.Equal(3, journal.Snapshot()[0].Targets);
        journal.Reset(Guid.NewGuid());
        Assert.Empty(journal.Snapshot()); Assert.Empty(journal.Replay(session, new("학생A", "학생A")));
    }

    [Fact]
    public void SameNameDoesNotInheritConfirmationUnlessReconnectIsVerified()
    {
        var journal = new AnnouncementJournal(); var session = Guid.NewGuid(); journal.Reset(session);
        var packet = new ChatPacket { SessionId = session, Message = "공지" };
        journal.Publish(packet, [new("old", "학생 A")]);
        journal.Accept(session, packet.AnnouncementId, "old", AnnouncementReceipt.Confirmed);
        journal.Replay(session, new("unrelated", "학생 A"));
        Assert.Equal(2, journal.Snapshot()[0].Targets);
        Assert.Equal(1, journal.Snapshot()[0].Confirmed);
        Assert.False(journal.Accept(session, packet.AnnouncementId, "학생 A", AnnouncementReceipt.Confirmed));
        journal.RebindVerifiedReconnect("old", "reconnected");
        journal.Replay(session, new("reconnected", "학생 A"));
        Assert.Equal(2, journal.Snapshot()[0].Targets);
        Assert.Equal(1, journal.Snapshot()[0].Confirmed);
        Assert.False(journal.Accept(session, packet.AnnouncementId, "old", AnnouncementReceipt.Confirmed));
    }
}
