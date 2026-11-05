using EduStream.Core.Models;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class SessionAttendanceTests
{
    [Fact]
    public void PresenceUsesIdAndReconnectIsNotAnotherInitialEntry()
    {
        var log = new SessionAttendance();
        log.Start(Guid.NewGuid(), [new(2, "학생", "001", "학과", ""), new(3, "학생", "002", "학과", "")]);
        log.Join("a", "학생", "001", false); log.Join("a", "학생", "001", false);
        Assert.True(log.Presence[0].Connected); Assert.False(log.Presence[1].Connected);
        log.Leave("a", "연결 끊김"); log.Leave("a", "연결 끊김");
        log.Join("b", "학생", "001", true);
        Assert.Equal(new[] { "입장", "연결 끊김", "재접속" }, log.Entries.Select(x => x.Action));
        log.Close(); Assert.All(log.Presence, x => Assert.False(x.Connected));
        Assert.Equal("세션 종료", log.Entries.Last().Action);
        log.Start(Guid.NewGuid(), []); Assert.Empty(log.Entries);
    }

    [Fact]
    public void ExportQuotesAndNeutralizesFormulaNames()
    {
        var log = new SessionAttendance(); log.Start(Guid.NewGuid(), []);
        log.Join("a", "  =SUM(1,2)\"", "", false);
        var csv = log.ToCsv();
        Assert.Contains("\"'  =SUM(1,2)\"\"\"", csv);
        Assert.StartsWith("세션,시각,이름,학번,구분", csv);
    }
}
