using EduStream.Core.Models;
using EduStream.Server.Services;
using EduStream.Client.ViewModels;
using EduStream.Core.Logging;
using EduStream.Core.Network;
using EduStream.Core.Serialization;

namespace EduStream.FileTransfer.Tests;

public sealed class RosterAdmissionTests
{
    [Theory]
    [InlineData("001", true)]
    [InlineData("999", false)]
    public async Task RealClientTlsTicketTcpJoinAndLeaveUseTheSameRosterIdentity(string id, bool allowed)
    {
        using var certificate = ProfessorCertificateStore.CreateEphemeral();
        var log = new InMemoryLogSink();
        var temp = Path.Combine(Path.GetTempPath(), "EduStream-TlsSubmission-" + Guid.NewGuid().ToString("N"));
        var manager = new SessionManager(log, new TcpServerService(log, new PacketSerializer()), temp);
        manager.ConfigureRosterAdmission([new(2, "합성 학생", "001", "", "")], true);
        var port = TestPortAllocator.GetFreePortPair();
        await manager.OpenSessionAsync("Roster test", port, ReadOnlyMemory<char>.Empty, certificate);
        var vm = new ClientViewModel { HostAddress = "127.0.0.1", Port = port, DisplayName = "합성 학생", StudentId = id };
        try
        {
            vm.JoinSessionCommand.Execute(null);
            await Until(() => !vm.IsConnecting);
            Assert.Equal(allowed, vm.IsConnected);
            if (allowed)
            {
                Assert.Contains("합성 학생 (001)", manager.ParticipantNames);
                Assert.Contains(manager.Attendance.Entries, x => x.Action == "입장");
                Assert.DoesNotContain(vm.ChatMessages, x => x.IsSystem && x.Message.Contains("님이 세션에 참여했습니다."));
                var chat = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                manager.ChatReceived += (sender, message) => { if (message == "identity-test") chat.TrySetResult(sender); };
                vm.ChatInput = "identity-test"; vm.SendChatCommand.Execute(null);
                Assert.Equal("합성 학생 (001)", await chat.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                await manager.SetSubmissionsOpenAsync(true);
                var submission = (EduStream.Client.Services.SubmissionClient)typeof(ClientViewModel)
                    .GetField("_submissionClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
                await Until(() => submission.IsOpen);
                Directory.CreateDirectory(temp);
                var path = Path.Combine(temp, "source.txt"); var bytes = new byte[140_000]; Random.Shared.NextBytes(bytes);
                await File.WriteAllBytesAsync(path, bytes);
                await submission.SubmitAsync(path).WaitAsync(TimeSpan.FromSeconds(10));
                var receipt = Assert.Single(manager.Submissions!.Receipts);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(receipt.LocalPath));
                Assert.Equal("합성 학생 (001)", receipt.Student);
                await submission.SubmitAsync(path).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(2, manager.Submissions.Receipts.Count);
                await manager.SetSubmissionsOpenAsync(false); await Until(() => !submission.IsOpen);
                await Assert.ThrowsAsync<InvalidOperationException>(() => submission.SubmitAsync(path));
                Assert.True(manager.Attendance.Presence.Single().Connected);
                vm.StudentId = "edited-after-join"; // 참가 뒤 입력값 변경이 이탈 신원을 바꾸지 않음
                vm.DisconnectCommand.Execute(null);
                await Until(() => manager.ParticipantCount == 0 && !vm.IsConnected);
            }
            else
            {
                Assert.Contains("명단", vm.LastErrorMessage);
                // 거부 뒤 연결 정리 안내가 덮어써도 학생이 보는 안내는 명단 불일치여야 한다.
                await Task.Delay(1500);
                Assert.Contains("명단", vm.StatusMessage);
                Assert.DoesNotContain("호스트 주소", vm.StatusMessage);
            }
        }
        finally { await vm.ShutdownAsync(); await manager.CloseSessionAsync(); if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }
    [Fact]
    public void OptionalPolicyRequiresExactNameAndIdOnlyWhenEnabled()
    {
        var policy = new RosterAdmissionPolicy();
        Assert.True(policy.Allows(new SessionJoinPacket { DisplayName = "일반 학생" }));
        policy.Configure([new(2, "학생 A", "001", "", ""), new(3, "학생 A", "002", "", "")], true);
        Assert.False(policy.Allows(new SessionJoinPacket { DisplayName = "학생 A" }));
        Assert.True(policy.Allows(new SessionJoinPacket { DisplayName = "학생 A (001)", RosterName = "학생 A", StudentId = "001" }));
        Assert.True(policy.Allows(new SessionJoinPacket { DisplayName = "학생 A (002)", RosterName = "학생 A", StudentId = "002" }));
        Assert.False(policy.Allows(new SessionJoinPacket { DisplayName = "학생 A (001)", RosterName = "다른 이름", StudentId = "001" }));
        Assert.False(policy.Allows(new SessionJoinPacket { DisplayName = "학생 A (001)", RosterName = "학생 A", StudentId = "002" }));
        policy.Configure([], false);
        Assert.True(policy.Allows(new SessionJoinPacket { DisplayName = "일반 학생" }));
    }

    [Fact]
    public void InvalidReplacementDoesNotErasePreviouslyConfiguredRoster()
    {
        var policy = new RosterAdmissionPolicy();
        policy.Configure([new(2, "학생 A", "001", "", "")], true);
        Assert.Throws<InvalidOperationException>(() => policy.Configure([new(2, "학생 B", "", "", "")], true));
        Assert.Throws<InvalidOperationException>(() => policy.Configure([new(2, "학생 B", "2024***", "", "")], true));
        Assert.Throws<InvalidOperationException>(() => policy.Configure([new(2, "학생 B", "002", "", ""), new(3, "학생 C", "002", "", "")], true));
        Assert.True(policy.Allows(new SessionJoinPacket { DisplayName = "학생 A (001)", RosterName = "학생 A", StudentId = "001" }));
    }
}
