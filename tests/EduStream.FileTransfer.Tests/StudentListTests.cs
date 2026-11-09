using EduStream.Core.Models;
using EduStream.Server.ViewModels;

namespace EduStream.FileTransfer.Tests;

public sealed class StudentListTests
{
    [Fact]
    public async Task ConnectedStudentsSortFirstThenNumericIdWithoutReplacingRowsOrHistory()
    {
        var vm = new ServerViewModel();
        try
        {
            var roster = new[] { new RosterRow(2, "학생", "20", "", ""), new RosterRow(3, "학생", "003", "", ""),
                new RosterRow(4, "학생", "10", "", ""), new RosterRow(5, "학번 없음", "", "", "학번 미입력") };
            foreach (var entry in roster) vm.RosterPreviewRows.Add(entry);
            var log = vm.SessionManager.Attendance;
            log.Start(Guid.NewGuid(), roster);
            Assert.Equal(new[] { "003", "10", "20", "" }, vm.StudentRows.Select(x => x.StudentId));
            var twenty = vm.StudentRows.Single(x => x.StudentId == "20");
            twenty.IsHistoryExpanded = true;
            var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            vm.StudentRows.CollectionChanged += (_, e) => changes.Add(e.Action);
            log.Join("twenty", "학생", "20", false);
            twenty.IsScreenExpanded = true;
            Assert.Same(twenty, vm.StudentRows[0]);
            log.Join("three", "학생", "003", false);
            Assert.Equal(new[] { "003", "20", "10", "" }, vm.StudentRows.Select(x => x.StudentId));
            Assert.Same(twenty, vm.StudentRows[1]); Assert.True(twenty.IsScreenExpanded);
            Assert.True(twenty.IsHistoryExpanded);
            log.Leave("twenty", "퇴장");
            Assert.Equal(new[] { "003", "10", "20", "" }, vm.StudentRows.Select(x => x.StudentId));
            Assert.Same(twenty, vm.StudentRows[2]); Assert.True(twenty.IsHistoryExpanded);
            Assert.Equal(2, twenty.History.Count);
            log.Join("twenty-again", "학생", "20", true);
            Assert.Same(twenty, vm.StudentRows[1]);
            Assert.Equal(3, twenty.History.Count);
            Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Move, changes);
            Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, changes);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task RealTlsJoinLeaveAndRejoinUpdateUnifiedRowsWithoutDuplicates()
    {
        using var certificate = EduStream.Server.Services.ProfessorCertificateStore.CreateEphemeral();
        var vm = new ServerViewModel();
        var roster = new[] { new RosterRow(2, "합성 학생", "001", "", ""), new RosterRow(3, "합성 학생", "002", "", "") };
        foreach (var row in roster) vm.RosterPreviewRows.Add(row);
        vm.SessionManager.ConfigureRosterAdmission(roster, true);
        var port = TestPortAllocator.GetFreePortPair();
        var client = new EduStream.Client.ViewModels.ClientViewModel { HostAddress = "127.0.0.1", Port = port, DisplayName = "합성 학생", StudentId = "001" };
        try
        {
            await vm.SessionManager.OpenSessionAsync("Unified list test", port, ReadOnlyMemory<char>.Empty, certificate);
            Assert.Equal(2, vm.StudentRows.Count);
            var row = vm.StudentRows[0]; row.IsHistoryExpanded = true;
            // 다음 명단의 미리보기로 진행 중인 세션 명단을 바꾸지 않는다.
            vm.RosterPreviewRows.Clear();
            vm.RosterPreviewRows.Add(new(2, "다음 수업 학생", "999", "", ""));
            client.JoinSessionCommand.Execute(null);
            await Until(() => client.IsConnected && row.IsConnected);
            Assert.Contains(row.ParticipantName, vm.Participants);
            Assert.Single(row.History); Assert.False(vm.StudentRows[1].IsConnected);
            Assert.DoesNotContain(vm.StudentRows, x => x.StudentId == "999");
            client.DisconnectCommand.Execute(null);
            await Until(() => !client.IsConnected && !row.IsConnected);
            client.JoinSessionCommand.Execute(null);
            await Until(() => client.IsConnected && row.IsConnected && row.History.Count == 3);
            Assert.Equal(2, vm.StudentRows.Count); Assert.Same(row, vm.StudentRows[0]);
            Assert.True(row.IsHistoryExpanded); Assert.Empty(vm.StudentRows[1].History);
            client.DisconnectCommand.Execute(null);
            await Until(() => !client.IsConnected && !row.IsConnected && row.History.Count == 4);
            Assert.True(vm.ExportAttendanceCommand.CanExecute(null));
            Assert.Contains("합성 학생", vm.SessionManager.Attendance.ToCsv());
        }
        finally { await client.ShutdownAsync(); await vm.ShutdownAsync(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Unified student list update timed out.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task RosterAndConnectionsShareRowsAndIndependentHistorySurvivesReconnect()
    {
        var vm = new ServerViewModel();
        try
        {
            var roster = Enumerable.Range(1, 32).Select(i => new RosterRow(i + 1, "합성 학생", i.ToString("D5"), "", "")).ToArray();
            foreach (var row in roster) vm.RosterPreviewRows.Add(row);
            var ledger = vm.SessionManager.Attendance;
            ledger.Start(Guid.NewGuid(), roster);
            Assert.Equal(32, vm.StudentRows.Count);
            var first = vm.StudentRows[0]; var second = vm.StudentRows[1];
            first.IsHistoryExpanded = true; second.IsHistoryExpanded = true;
            ledger.Join("one", first.Name, first.StudentId, false);
            first.IsScreenExpanded = true;
            ledger.Join("two", second.Name, second.StudentId, false);
            Assert.Same(first, vm.StudentRows[0]);
            Assert.True(first.IsScreenExpanded); // 다른 학생의 갱신으로 기존 화면 뷰를 다시 만들지 않는다.
            Assert.True(first.IsHistoryExpanded); Assert.True(second.IsHistoryExpanded);
            Assert.Equal("합성 학생 (00001)", first.ParticipantName);
            Assert.Equal("학생 32명 · 접속 2명", vm.AttendanceSummary);
            ledger.Leave("one", "퇴장");
            Assert.False(first.IsConnected); Assert.False(first.IsScreenExpanded);
            Assert.True(first.IsHistoryExpanded); Assert.True(second.IsConnected);
            ledger.Join("rejoined", first.Name, first.StudentId, true);
            Assert.Same(first, vm.StudentRows[0]); Assert.True(first.IsConnected);
            Assert.Equal(new[] { "재접속", "퇴장", "입장" }, first.History.Select(x => x.Action));
            Assert.Single(second.History);
            Assert.All(first.History, x => Assert.Equal(first.StudentId, x.StudentId));
            Assert.Contains("재접속", ledger.ToCsv());
            ledger.Close();
            Assert.All(vm.StudentRows, x => Assert.False(x.IsConnected));
            Assert.Equal("세션 종료", first.History[0].Action);
            Assert.True(vm.ExportAttendanceCommand.CanExecute(null));
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task NoRosterGuestsRemainWithHistoryAfterLeavingAndResetOnNextSession()
    {
        var vm = new ServerViewModel();
        try
        {
            var ledger = vm.SessionManager.Attendance;
            ledger.Start(Guid.NewGuid(), []);
            ledger.Join("guest", "방문 학생", "", false);
            var row = Assert.Single(vm.StudentRows);
            Assert.Equal("방문 학생", row.ParticipantName); Assert.True(row.IsConnected);
            ledger.Leave("guest", "연결 끊김");
            Assert.Same(row, Assert.Single(vm.StudentRows)); Assert.False(row.IsConnected);
            Assert.Equal(2, row.History.Count);
            ledger.Start(Guid.NewGuid(), []);
            Assert.Empty(vm.StudentRows); Assert.False(vm.ExportAttendanceCommand.CanExecute(null));
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task InvalidDuplicateRowsStayVisibleAndAreNotTreatedAsConnectedStudents()
    {
        var vm = new ServerViewModel();
        try
        {
            vm.RosterPreviewRows.Add(new(2, "합성 학생", "001", "", "중복 학번"));
            vm.RosterPreviewRows.Add(new(3, "합성 학생", "001", "", "중복 학번"));
            vm.SessionManager.Attendance.Start(Guid.NewGuid(), []);
            Assert.Equal(2, vm.StudentRows.Count);
            Assert.All(vm.StudentRows, row =>
            {
                Assert.Equal("중복 학번", row.Issue); Assert.False(row.IsConnected);
                row.IsScreenExpanded = true; Assert.False(row.IsScreenExpanded);
            });
            Assert.False(vm.ApplyRosterAdmissionCommand.CanExecute(null));
        }
        finally { await vm.ShutdownAsync(); }
    }
}
