using System.ComponentModel;
using System.Windows.Threading;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.ShareViewer;
using Forms = System.Windows.Forms;

namespace EduStream.FileTransfer.Tests;

/// <summary>실제 학생 호스트/교수 뷰어와 앱 연결 서비스의 권한 적용. 한 PC의 COM 검증이며 다중 PC/입력 시연이 아니다.</summary>
[Collection("WDS integration")]
public sealed class StudentSharingSessionIntegrationTests
{
    private sealed class Channel : ICollaborationChannel
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Frames = new();
        public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default) { Frames.Enqueue(frame); return Task.CompletedTask; }
        public T Last<T>() where T : notnull => Frames.Select(frame =>
        {
            try { return CollaborationMessageCodec.Decode<T>(frame, out _); } catch { return default; }
        }).Last(item => item is not null)!;
    }

    [WdsFact(Timeout = 120000)]
    public Task RoomCreatesRealHost_ViewerConnects_GrantRevokeAcknowledgesNativeState() => OnSta(async () =>
    {
        var session = Guid.NewGuid();
        var student = new ParticipantConnection(session, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
        var professor = new ParticipantConnection(session, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var self = new ParticipantSnapshot(student, "IntegrationStudent", true, true, true, 0);
        var room = new RoomJoined(student, 1, new[] { self, new ParticipantSnapshot(professor, "Professor", true, false, false, 0) });
        var channel = new Channel(); var log = new InMemoryLogSink();
        var routing = new ReverseCollaborationClient(session, channel, log);
        routing.ApplyRoom(room);
        await using var sharing = new StudentSharingSession(routing, channel, log);
        await sharing.ApplyRoomAsync(room);
        var invitation = channel.Last<ReverseRdpInvitationNotice>();
        var secret = channel.Last<ReverseRdpInvitationSecretNotice>();
        Assert.Equal(ReverseRdpIdentity.For(student), invitation.StudentId);
        secret.ValidateForInvitation(invitation, DateTimeOffset.UtcNow);

        using var form = new Forms.Form { Text = "EduStream native integration verification", Width = 640, Height = 400, ShowInTaskbar = false };
        await using var reception = new ProfessorReception();
        var viewer = new AxRDPCOMAPILib.AxRDPViewer { Dock = Forms.DockStyle.Fill };
        ((ISupportInitialize)viewer).BeginInit(); form.Controls.Add(viewer); ((ISupportInitialize)viewer).EndInit();
        form.Show(); viewer.CreateControl(); viewer.SmartSizing = true;
        var connection = reception.Watch(invitation.StudentId, viewer, invitation.ConnectionString, invitation.ProfessorId, secret.Password);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!connection.IsConnectionLive && !connection.Failed && DateTime.UtcNow < deadline) await Task.Delay(30);
            Assert.True(connection.IsConnectionLive, string.Join(Environment.NewLine, log.Snapshot()));
            var grant = new RemoteInputCommandNotice(Guid.NewGuid(), session, invitation.SharingId,
                Guid.NewGuid(), invitation.ProfessorId, RemoteInputAction.Grant);
            await sharing.HandleInputAsync(grant);
            Assert.True(channel.Last<RemoteInputResultNotice>().Applied);
            var revoke = grant with { CommandId = Guid.NewGuid(), Action = RemoteInputAction.Revoke };
            await sharing.HandleInputAsync(revoke);
            Assert.True(channel.Last<RemoteInputResultNotice>().Applied);

            // 학생 허용을 다시 토글하지 않아도 native viewer 이탈 후 새 초대로 재접속한다.
            await reception.ReleaseAsync(invitation.StudentId);
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (channel.Last<ReverseRdpInvitationNotice>().InvitationId == invitation.InvitationId && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            var renewed = channel.Last<ReverseRdpInvitationNotice>();
            var renewedSecret = channel.Last<ReverseRdpInvitationSecretNotice>();
            Assert.NotEqual(invitation.InvitationId, renewed.InvitationId);
            Assert.NotEqual(invitation.SharingId, renewed.SharingId);
            viewer = new AxRDPCOMAPILib.AxRDPViewer { Dock = Forms.DockStyle.Fill };
            ((ISupportInitialize)viewer).BeginInit(); form.Controls.Add(viewer); ((ISupportInitialize)viewer).EndInit();
            viewer.CreateControl(); viewer.SmartSizing = true;
            connection = reception.Watch(renewed.StudentId, viewer, renewed.ConnectionString, renewed.ProfessorId, renewedSecret.Password);
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (!connection.IsConnectionLive && !connection.Failed && DateTime.UtcNow < deadline) await Task.Delay(30);
            Assert.True(connection.IsConnectionLive, string.Join(Environment.NewLine, log.Snapshot()));

            // 서버가 이전 ON 스냅샷을 보내도 학생의 로컬 OFF가 우선한다.
            grant = grant with { CommandId = Guid.NewGuid(), SharingId = renewed.SharingId };
            revoke = revoke with { CommandId = Guid.NewGuid(), SharingId = renewed.SharingId };
            await sharing.ApplyLocalPermissionsAsync(true, false);
            await sharing.ApplyRoomAsync(room);
            await sharing.HandleInputAsync(grant with { CommandId = Guid.NewGuid() });
            Assert.False(channel.Last<RemoteInputResultNotice>().Applied);

            // 다른 공유 세대의 허용은 native 호출 전에 거절한다.
            await sharing.HandleInputAsync(grant with { CommandId = Guid.NewGuid(), SharingId = Guid.NewGuid() });
            Assert.False(channel.Last<RemoteInputResultNotice>().Applied);
            var withdrawn = room with { Revision = 2, Participants = new[] { self with { AllowViewing = false, AllowControl = false, PermissionRevision = 1 }, room.Participants[1] } };
            routing.ApplyRoom(withdrawn);
            await sharing.ApplyRoomAsync(withdrawn);
            await sharing.HandleInputAsync(revoke with { CommandId = Guid.NewGuid() });
            Assert.True(channel.Last<RemoteInputResultNotice>().Applied); // 실제 종료가 끝난 이전 공유도 회수 완료로 응답한다.
        }
        finally { await sharing.DisposeAsync(); await reception.ReleaseAsync(invitation.StudentId); }
    });

    private static Task OnSta(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            async void Run()
            {
                try { await body(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }
            dispatcher.BeginInvoke((Action)Run); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
