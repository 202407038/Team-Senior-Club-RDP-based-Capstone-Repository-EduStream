using System.Collections.ObjectModel;
using EduStream.Core.Common;
using EduStream.Core.Models;
using EduStream.Server.Services;

namespace EduStream.Server.ViewModels;

public sealed class StudentListRow(string name, string studentId, int invalidSourceRow = 0) : ObservableObject
{
    public string Name { get; } = name;
    public string StudentId { get; } = studentId;
    public int InvalidSourceRow { get; } = invalidSourceRow;
    // 화면 뷰와 WDS 라우터가 사용하는 인증된 참가자 식별자를 유지한다.
    public string ParticipantName => RosterIdentity.ConnectionName(Name, StudentId);
    public ObservableCollection<AttendanceEntry> History { get; } = [];
    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(Status));
            if (!value) IsScreenExpanded = false;
        }
    }
    public string Status => IsConnected ? "접속 중" : "미접속";
    private string _issue = "";
    public string Issue { get => _issue; private set => SetProperty(ref _issue, value); }
    private bool _isHistoryExpanded;
    public bool IsHistoryExpanded { get => _isHistoryExpanded; set => SetProperty(ref _isHistoryExpanded, value); }
    private bool _isScreenExpanded;
    public bool IsScreenExpanded { get => _isScreenExpanded; set => SetProperty(ref _isScreenExpanded, value && IsConnected); }

    internal void Update(bool connected, string issue, IEnumerable<AttendanceEntry> history)
    {
        IsConnected = connected;
        Issue = issue;
        var next = history.ToArray();
        if (History.SequenceEqual(next)) return;
        History.Clear();
        foreach (var entry in next) History.Add(entry);
    }
}
