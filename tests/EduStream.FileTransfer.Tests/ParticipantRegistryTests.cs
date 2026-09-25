using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class ParticipantRegistryTests
{
    [Fact]
    public void Join_IssuesFreshConnectionAndBumpsRevision()
    {
        var registry = new ParticipantRegistry();
        var sessionId = Guid.NewGuid();

        var revisionBefore = registry.Revision;
        var connection = registry.Join("client-1", sessionId, "학생 1", ParticipantRole.Student);

        Assert.Equal(sessionId, connection.SessionId);
        Assert.Equal(ParticipantRole.Student, connection.Role);
        Assert.True(registry.Revision > revisionBefore);

        var snapshot = registry.TryResolve(connection.ConnectionId);
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Connected);
        Assert.True(snapshot.AllowViewing);
        Assert.True(snapshot.AllowControl); // U07: 교수자 제어 허용 기본 ON
    }

    [Fact]
    public void SetPermissions_RaisesPermissionsChangedWithNewRevision()
    {
        var registry = new ParticipantRegistry();
        var connection = registry.Join("client-1", Guid.NewGuid(), "학생 1", ParticipantRole.Student);
        ParticipantSnapshot? changed = null;
        registry.PermissionsChanged += snapshot => changed = snapshot;

        registry.SetPermissions(connection.ConnectionId, allowViewing: true, allowControl: false);

        Assert.NotNull(changed);
        Assert.Equal(connection, changed!.Connection);
        Assert.False(changed.AllowControl);
        Assert.Equal(1, changed.PermissionRevision);
    }

    [Fact]
    public void ConnectionRemoved_RaisedForDisconnectRejoinAndClear()
    {
        var registry = new ParticipantRegistry();
        var sessionId = Guid.NewGuid();
        var removed = new List<ParticipantConnection>();
        registry.ConnectionRemoved += removed.Add;

        var first = registry.Join("client-1", sessionId, "학생 1", ParticipantRole.Student);
        var rejoined = registry.Join("client-1", sessionId, "학생 1", ParticipantRole.Student);
        var other = registry.Join("client-2", sessionId, "학생 2", ParticipantRole.Student);
        registry.Disconnect("client-2");
        registry.Clear();

        Assert.Equal(new[] { first, other, rejoined }, removed);
    }

    [Fact]
    public void Join_SameClientTwice_ReplacesPreviousConnection()
    {
        var registry = new ParticipantRegistry();
        var sessionId = Guid.NewGuid();

        var first = registry.Join("client-1", sessionId, "학생 1", ParticipantRole.Student);
        var second = registry.Join("client-1", sessionId, "학생 1", ParticipantRole.Student);

        Assert.NotEqual(first.ConnectionId, second.ConnectionId);
        Assert.Null(registry.TryResolve(first.ConnectionId));
        Assert.NotNull(registry.TryResolve(second.ConnectionId));
        Assert.Single(registry.Participants);
    }

    [Fact]
    public void Disconnect_RemovesFromRegistry()
    {
        var registry = new ParticipantRegistry();
        var sessionId = Guid.NewGuid();
        var connection = registry.Join("client-1", sessionId, "학생 1", ParticipantRole.Student);

        var removed = registry.Disconnect("client-1");

        Assert.Equal(connection, removed);
        Assert.Null(registry.TryResolve(connection.ConnectionId));
        Assert.Empty(registry.Participants);
    }

    [Fact]
    public void Disconnect_UnknownClient_ReturnsNullAndDoesNotBumpRevision()
    {
        var registry = new ParticipantRegistry();
        var before = registry.Revision;

        var removed = registry.Disconnect("no-such-client");

        Assert.Null(removed);
        Assert.Equal(before, registry.Revision);
    }

    [Fact]
    public void SetPermissions_ControlRequiresViewing()
    {
        var registry = new ParticipantRegistry();
        var sessionId = Guid.NewGuid();
        var connection = registry.Join("client-1", sessionId, "학생 1", ParticipantRole.Student);

        Assert.True(registry.SetPermissions(connection.ConnectionId, allowViewing: false, allowControl: true));

        var snapshot = registry.TryResolve(connection.ConnectionId)!;
        Assert.False(snapshot.AllowViewing);
        Assert.False(snapshot.AllowControl); // 보기 없는 제어는 허용하지 않음
    }

    [Fact]
    public void Snapshot_UnknownViewer_ReturnsNull()
    {
        var registry = new ParticipantRegistry();
        var stale = new ParticipantConnection(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);

        Assert.Null(registry.Snapshot(stale));
    }

    [Fact]
    public void FileRequestAuthorizer_RejectsUnknownOrStaleConnection()
    {
        var registry = new ParticipantRegistry();
        var sessionId = Guid.NewGuid();
        var authorizer = new SessionFileRequestAuthorizer(registry);

        var connection = registry.Join("client-1", sessionId, "학생 1", ParticipantRole.Student);
        var request = new SessionFileRequest(Guid.NewGuid(), sessionId, Guid.NewGuid(), 1);
        var context = new FileDownloadContext(connection);

        Assert.True(authorizer.CanDownload(context, request));

        registry.Disconnect("client-1");
        Assert.False(authorizer.CanDownload(context, request));

        // 재접속으로 새 ConnectionId가 발급된 뒤, 옛 연결 정보로는 더 이상 통과할 수 없다.
        var rejoined = registry.Join("client-1", sessionId, "학생 1", ParticipantRole.Student);
        Assert.NotEqual(connection.ConnectionId, rejoined.ConnectionId);
        Assert.False(authorizer.CanDownload(context, request));

        var freshContext = new FileDownloadContext(rejoined);
        Assert.True(authorizer.CanDownload(freshContext, request));
    }

    [Fact]
    public void FileRequestAuthorizer_RejectsNonStudentRole()
    {
        var registry = new ParticipantRegistry();
        var sessionId = Guid.NewGuid();
        var authorizer = new SessionFileRequestAuthorizer(registry);
        var professor = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var request = new SessionFileRequest(Guid.NewGuid(), sessionId, Guid.NewGuid(), 1);

        Assert.False(authorizer.CanDownload(new FileDownloadContext(professor), request));
    }
}
