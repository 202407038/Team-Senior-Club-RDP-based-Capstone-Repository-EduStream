using System.Text.Json;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;

namespace EduStream.FileTransfer.Tests;

public sealed class FinalReadinessContractTests
{
    private static (ParticipantConnection Professor, ParticipantSnapshot Student) Participants()
    {
        var session = Guid.NewGuid();
        return (new(session, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor),
            new(new(session, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student), "학생", true, true, true, 1));
    }

    [Fact]
    public void RemoteControl_RequiresNativeConfirmationAndRejectsOldPermission()
    {
        var (professor, student) = Participants();
        var state = RemoteControlState.Request(professor, student, Guid.NewGuid());
        Assert.Equal(ControlPhase.Requested, state.Phase);
        Assert.Throws<CollaborationException>(() => state.Activate(state.RequestId, student with { PermissionRevision = 2 }));
        Assert.Throws<CollaborationException>(() => state.Activate(Guid.NewGuid(), student));
        Assert.Throws<CollaborationException>(() => state.Activate(state.RequestId,
            student with { Connection = student.Connection with { ConnectionId = Guid.NewGuid() } }));
        Assert.Throws<CollaborationException>(() => state.Activate(state.RequestId, student with { AllowControl = false }));
        Assert.Equal(ControlPhase.Active, state.Activate(state.RequestId, student).Phase);
        Assert.Throws<CollaborationException>(() => state.Revoke().Activate(state.RequestId, student));
        Assert.Equal(ControlPhase.Revoked, state.Revoke().Fail().Phase);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void DeniedOrDisconnectedStudent_CannotBeControlled(bool connected, bool viewing, bool control)
    {
        var (professor, student) = Participants();
        Assert.Throws<CollaborationException>(() => RemoteControlState.Request(professor,
            student with { Connected = connected, AllowViewing = viewing, AllowControl = control }, Guid.NewGuid()));
    }

    [Fact]
    public void StudentsCannotControlProfessorOrOtherStudents_CrossSessionRejected()
    {
        var (professor, student) = Participants();
        Assert.Throws<CollaborationException>(() => RemoteControlState.Request(student.Connection, student, Guid.NewGuid()));
        Assert.Throws<CollaborationException>(() => RemoteControlState.Request(professor,
            student with { Connection = professor }, Guid.NewGuid()));
        Assert.Throws<CollaborationException>(() => RemoteControlState.Request(
            professor with { SessionId = Guid.NewGuid() }, student, Guid.NewGuid()));
    }

    [Fact]
    public void PauseResume_PreservesClassAndRejectsPreviousCallbacks()
    {
        var state = SharingLifecycleState.Create(Guid.NewGuid()).Begin(Guid.NewGuid());
        var oldAttempt = state.AttemptId;
        var oldGeneration = state.Generation;
        state = state.Started(state.AttemptId, state.Generation, Guid.NewGuid()).Pause().Begin(Guid.NewGuid());
        Assert.Equal(2, state.Generation);
        Assert.Equal(SharingPhase.Starting, state.Phase);
        Assert.Throws<CollaborationException>(() => state.Started(oldAttempt, oldGeneration, Guid.NewGuid()));
        // 같은 ID를 잘못 재사용하더라도 generation이 이전 콜백을 차단.
        Assert.Throws<CollaborationException>(() => state.Started(state.AttemptId, oldGeneration, Guid.NewGuid()));
        var closed = state.Close();
        Assert.Throws<CollaborationException>(() => closed.Begin(Guid.NewGuid()));
        Assert.Throws<CollaborationException>(() => closed.Started(state.AttemptId, state.Generation, Guid.NewGuid()));
    }

    [Fact]
    public void AnnotationOffHideClear_AreIndependent()
    {
        var drawn = AnnotationState.Empty.SetDrawing(true).ContentChanged();
        var off = drawn.SetDrawing(false);
        Assert.True(off.Visible);
        Assert.Equal(drawn.ContentRevision, off.ContentRevision);
        var hidden = off.ToggleVisibility();
        Assert.False(hidden.Visible);
        Assert.False(hidden.Drawing);
        Assert.True(hidden.ToggleVisibility().Visible);
        Assert.Equal(drawn.ContentRevision + 1, drawn.ClearAndStop().ContentRevision);
        Assert.False(drawn.ClearAndStop().Drawing);
    }

    [Fact]
    public void ParticipantRevision_DoesNotAcceptOldConnectionOrDuplicateIdentity()
    {
        var (professor, student) = Participants();
        var current = new RoomJoined(professor, 1, new[] { student });
        ParticipantSnapshotRules.Validate(current);
        Assert.False(ParticipantSnapshotRules.IsNewer(current, current));
        Assert.True(ParticipantSnapshotRules.IsNewer(current, current with { Revision = 2 }));
        Assert.False(ParticipantSnapshotRules.IsNewer(current,
            current with { Revision = 3, Connection = professor with { ConnectionId = Guid.NewGuid() } }));
        Assert.Throws<CollaborationException>(() =>
            ParticipantSnapshotRules.Validate(current with { Participants = new[] { student, student } }));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("a:stream")]
    [InlineData("CON.txt")]
    [InlineData("LPT1")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("")]
    public void SessionFileNames_AreSafeWindowsLeafNames(string name) =>
        Assert.Throws<CollaborationException>(() => SessionFileLimits.ValidateName(name));

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(65537)]
    public void FileDescriptors_RoundTripWithStableIdentity(int size)
    {
        var file = new SessionFileDescriptor(Guid.NewGuid(), Guid.NewGuid(), 1, "한글 강의.txt",
            size, new string('a', 64), 65536);
        file.Validate();
        var copy = JsonSerializer.Deserialize<SessionFileDescriptor>(JsonSerializer.Serialize(file));
        Assert.Equal(file, copy);
        Assert.Equal(size <= 65536 ? 1 : 2, file.TotalChunks);
    }

    [Fact]
    public void JoinValidation_DoesNotIncludePasswordInDto()
    {
        var request = new RoomJoinRequest("127.0.0.1", 5000, "학생", Guid.NewGuid());
        request.Validate();
        Assert.DoesNotContain("Password", JsonSerializer.Serialize(request), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<CollaborationException>(() => (request with { Port = 0 }).Validate());
        Assert.Throws<CollaborationException>(() => (request with { DisplayName = " " }).Validate());
    }
}
