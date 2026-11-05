using System.Net;
using System.Text;
using EduStream.Client.Services;
using EduStream.Client.ViewModels;
using EduStream.Core.Network;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class TeacherDiscoveryTests
{
    [Fact]
    public void ProtocolRejectsStaleMalformedOversizedAndInvalidReplies()
    {
        var nonce = Guid.NewGuid(); var session = Guid.NewGuid(); var sender = new IPEndPoint(IPAddress.Loopback, 45050);
        var valid = TeacherDiscoveryProtocol.Reply(nonce, session, "Classroom-A", 5000, true);
        Assert.Equal("127.0.0.1", TeacherDiscoveryProtocol.ReadReply(valid, sender, nonce)!.Address);
        Assert.Null(TeacherDiscoveryProtocol.ReadReply(valid, sender, Guid.NewGuid()));
        Assert.Null(TeacherDiscoveryProtocol.ReadReply(new byte[1025], sender, nonce));
        Assert.Null(TeacherDiscoveryProtocol.ReadReply(Encoding.UTF8.GetBytes("null"), sender, nonce));
        Assert.Null(TeacherDiscoveryProtocol.ReadReply(Encoding.UTF8.GetBytes("not-json"), sender, nonce));
        Assert.Null(TeacherDiscoveryProtocol.ReadReply(TeacherDiscoveryProtocol.Reply(nonce, session, "PC\nwrong", 5000, false), sender, nonce));
        Assert.Null(TeacherDiscoveryProtocol.ReadReply(TeacherDiscoveryProtocol.Reply(nonce, session, "PC", 65535, false), sender, nonce));
        Assert.Null(TeacherDiscoveryProtocol.ReadQuery(TeacherDiscoveryProtocol.Query(Guid.Empty)));
        Assert.Equal(nonce, TeacherDiscoveryProtocol.ReadQuery(TeacherDiscoveryProtocol.Query(nonce)));
    }

    [Fact]
    public async Task UdpDiscoveryFindsTwoTeachersDeduplicatesAndStopsAfterDisposal()
    {
        var a = new TeacherDiscoveryResponder(Guid.NewGuid(), "Teacher A", 5000, false, discoveryPort: 0);
        await using var b = new TeacherDiscoveryResponder(Guid.NewGuid(), "Teacher B", 5010, true, discoveryPort: 0);
        var endpointA = new IPEndPoint(IPAddress.Loopback, a.ListeningPort);
        var endpointB = new IPEndPoint(IPAddress.Loopback, b.ListeningPort);
        var client = new TeacherDiscoveryClient();
        try
        {
            var results = await client.FindAsync([endpointA, endpointB, endpointA], TimeSpan.FromMilliseconds(250), default);
            Assert.Equal(2, results.Count);
            Assert.Contains(results, x => x.ComputerName == "Teacher A" && !x.PasswordRequired);
            Assert.Contains(results, x => x.ComputerName == "Teacher B" && x.PasswordRequired && x.Port == 5010);
        }
        finally { await a.DisposeAsync(); }
        var after = await client.FindAsync([endpointA, endpointB], TimeSpan.FromMilliseconds(250), default);
        Assert.Single(after);
        Assert.Equal("Teacher B", after[0].ComputerName);
    }

    [Fact]
    public async Task CancelledSearchDoesNotReturnPartialResultsAsCompleted()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TeacherDiscoveryClient().FindAsync(
            [new IPEndPoint(IPAddress.Loopback, 45050)], TimeSpan.FromSeconds(3), stop.Token));
    }

    [Fact]
    public async Task ViewModelRequiresChoiceAndKeepsManualAddressWhenSearchIsEmpty()
    {
        var fake = new FakeFinder();
        var a = new DiscoveredTeacher(Guid.NewGuid(), "Teacher A", "192.168.10.2", 5010, true);
        fake.Results = [a, new(Guid.NewGuid(), "Teacher B", "192.168.10.3", 5000, false)];
        var vm = new ClientViewModel(teacherDiscovery: fake) { HostAddress = "192.168.10.4" };
        try
        {
            await vm.FindTeachersAsync();
            Assert.Equal(2, vm.DiscoveredTeachers.Count);
            Assert.Null(vm.SelectedTeacher);
            Assert.Equal("192.168.10.4", vm.HostAddress);
            vm.SelectedTeacher = a;
            Assert.Equal(a.Address, vm.HostAddress);
            Assert.Equal(5010, vm.Port);
            Assert.True(vm.RoomPasswordRequired);
            fake.Results = [];
            await vm.FindTeachersAsync();
            Assert.Empty(vm.DiscoveredTeachers);
            Assert.Equal(a.Address, vm.HostAddress);
            Assert.Contains("직접 입력", vm.DiscoveryStatus);
        }
        finally { await vm.ShutdownAsync(); }
    }

    private sealed class FakeFinder : ITeacherDiscoveryClient
    {
        public IReadOnlyList<DiscoveredTeacher> Results { get; set; } = [];
        public Task<IReadOnlyList<DiscoveredTeacher>> FindAsync(CancellationToken cancellationToken) => Task.FromResult(Results);
    }
}
