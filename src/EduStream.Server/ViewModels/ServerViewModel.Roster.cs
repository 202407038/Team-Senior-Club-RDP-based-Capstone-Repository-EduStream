using System.Collections.ObjectModel;
using EduStream.Core.Common;
using EduStream.Core.Models;
using EduStream.Server.Services;
using System.IO;
using System.Text;
using System.Globalization;
using System.Numerics;

namespace EduStream.Server.ViewModels;

public sealed partial class ServerViewModel
{
    public ObservableCollection<RosterRow> RosterPreviewRows { get; } = [];
    public ObservableCollection<FileDeliveryStatus> FileDeliveries { get; } = [];
    public ObservableCollection<RosterPresence> RosterPresenceRows { get; } = [];
    public ObservableCollection<AttendanceEntry> AttendanceRows { get; } = [];
    public ObservableCollection<StudentListRow> StudentRows { get; } = [];
    public RelayCommand ExportAttendanceCommand { get; private set; } = null!;
    public string AttendanceSummary => $"학생 {StudentRows.Count}명 · 접속 {StudentRows.Count(x => x.IsConnected)}명";
    private void InitializeAttendance()
    {
        ExportAttendanceCommand = new RelayCommand(ExportAttendance, () => AttendanceRows.Count > 0);
        _sessionManager.Attendance.Changed += () => RunOnUi(RefreshAttendance);
    }
    private void RefreshAttendance()
    {
        RosterPresenceRows.Clear();
        foreach (var row in _sessionManager.Attendance.Presence) RosterPresenceRows.Add(row);
        AttendanceRows.Clear();
        foreach (var row in _sessionManager.Attendance.Entries.Reverse()) AttendanceRows.Add(row);
        RefreshStudentRows();
        ExportAttendanceCommand.RaiseCanExecuteChanged();
    }
    private void RefreshStudentRows()
    {
        var attendance = _sessionManager.Attendance;
        var entries = attendance.Entries;
        var connected = attendance.ConnectedStudents.Select(x => (x.Name, x.StudentId)).ToHashSet();
        // 새로 가져온 미리보기로 진행 중인 세션의 입장 대조 명단을 바꾸지 않는다.
        var roster = _sessionManager.CurrentSession is not null && _sessionManager.IsRosterAdmissionEnabled
            ? attendance.Presence.Select(x => new RosterRow(0, x.Name, x.StudentId, "", "")).ToArray()
            : RosterPreviewRows.ToArray();
        var desired = new Dictionary<(string Name, string Id, int InvalidRow), string>();
        foreach (var row in roster)
            desired.TryAdd((row.Name, row.StudentId, row.IsValid ? 0 : row.SourceRow), row.Issue);
        foreach (var entry in entries)
            desired.TryAdd((entry.Name, entry.StudentId, 0), "");
        foreach (var student in connected)
            desired.TryAdd((student.Name, student.StudentId, 0), "");

        for (var i = StudentRows.Count - 1; i >= 0; i--)
        {
            var row = StudentRows[i];
            if (!desired.ContainsKey((row.Name, row.StudentId, row.InvalidSourceRow))) StudentRows.RemoveAt(i);
        }
        foreach (var (key, issue) in desired)
        {
            var row = StudentRows.FirstOrDefault(x => x.Name == key.Name && x.StudentId == key.Id && x.InvalidSourceRow == key.InvalidRow);
            if (row is null) { row = new StudentListRow(key.Name, key.Id, key.InvalidRow); StudentRows.Add(row); }
            row.Update(key.InvalidRow == 0 && connected.Contains((key.Name, key.Id)), issue,
                key.InvalidRow == 0 ? entries.Where(x => x.Name == key.Name && x.StudentId == key.Id).Reverse() : []);
        }
        var sorted = StudentRows.Select(row => new
            {
                Row = row,
                Number = BigInteger.TryParse(row.StudentId, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    ? (BigInteger?)number : null
            })
            .OrderByDescending(x => x.Row.IsConnected)
            .ThenBy(x => x.Number is null)
            .ThenBy(x => x.Number)
            .ThenBy(x => x.Row.StudentId, StringComparer.Ordinal)
            .ThenBy(x => x.Row.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Row.InvalidSourceRow)
            .Select(x => x.Row).ToArray();
        // 목록을 초기화하지 않고 기존 행을 이동해 기록 펼침과 화면 뷰를 유지한다.
        for (var i = 0; i < sorted.Length; i++)
        {
            var current = StudentRows.IndexOf(sorted[i]);
            if (current != i) StudentRows.Move(current, i);
        }
        OnPropertyChanged(nameof(AttendanceSummary));
    }
    private void ExportAttendance()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV 접속 기록 (*.csv)|*.csv", FileName = $"EduStream-접속기록-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, _sessionManager.Attendance.ToCsv(), new UTF8Encoding(true)); RosterImportStatus = "접속 기록 저장 완료"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RosterImportStatus = "접속 기록 저장 실패. 경로와 파일 사용 여부를 확인해 주세요."; }
    }
    public RelayCommand PreviewRosterCommand { get; }
    public RelayCommand ApplyRosterAdmissionCommand { get; }
    public RelayCommand DisableRosterAdmissionCommand { get; }
    private RosterImportPreview? _rosterPreview;
    public string RosterAdmissionStatus => _sessionManager.IsRosterAdmissionEnabled
        ? $"명단 대조 켜짐 · {_sessionManager.RosterCount}명"
        : "명단 대조 꺼짐";
    private void RefreshRosterCommands()
    {
        ApplyRosterAdmissionCommand?.RaiseCanExecuteChanged();
        DisableRosterAdmissionCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(RosterAdmissionStatus));
    }
    public void ApplyRosterAdmission()
    {
        if (!ApplyRosterAdmissionCommand.CanExecute(null)) return;
        _sessionManager.ConfigureRosterAdmission(_rosterPreview!.Rows, true);
        RosterImportStatus = "명단 적용 완료";
        RefreshRosterCommands();
    }
    public void DisableRosterAdmission()
    {
        if (!DisableRosterAdmissionCommand.CanExecute(null)) return;
        _sessionManager.ConfigureRosterAdmission([], false);
        RefreshRosterCommands();
    }
    private bool _isLoadingRoster;
    public bool IsLoadingRoster
    {
        get => _isLoadingRoster;
        private set { SetProperty(ref _isLoadingRoster, value); PreviewRosterCommand.RaiseCanExecuteChanged(); RefreshRosterCommands(); }
    }
    private string _rosterImportStatus = string.Empty;
    public string RosterImportStatus { get => _rosterImportStatus; private set => SetProperty(ref _rosterImportStatus, value); }
    private async Task ChooseRosterAsync()
    {
        if (IsLoadingRoster) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Excel 학생 명단 (*.xlsx)|*.xlsx", Multiselect = false };
        if (dialog.ShowDialog() == true) await PreviewRosterAsync(dialog.FileName);
    }
    public async Task PreviewRosterAsync(string path)
    {
        if (IsLoadingRoster) return;
        IsLoadingRoster = true;
        try
        {
            var preview = await Task.Run(() => new RosterWorkbookReader().Read(path));
            _rosterPreview = preview;
            RosterPreviewRows.Clear();
            foreach (var row in preview.Rows) RosterPreviewRows.Add(row);
            RefreshStudentRows();
            if (!preview.CanUseForAdmission)
            {
                RosterImportStatus = preview.Summary + " · 오류 행 수정 필요 · 적용 불가";
                if (_sessionManager.IsRosterAdmissionEnabled) RosterImportStatus += " 기존에 적용한 명단은 그대로 유지됩니다.";
            }
            else if (!IsSessionOpen && !IsBusy)
            {
                // 명단만 불러오고 대조를 켜지 않아 명단 밖 학생이 들어오는 일을 막는다.
                _sessionManager.ConfigureRosterAdmission(preview.Rows, true);
                RosterImportStatus = preview.Summary + " · 명단 대조 켜짐";
            }
            else
            {
                RosterImportStatus = preview.Summary + " · 세션이 열려 있어 아직 대조에 쓰이지 않습니다. 세션을 닫고 '명단 대조 켜기'를 누른 뒤 다시 여세요.";
                if (_sessionManager.IsRosterAdmissionEnabled) RosterImportStatus += " 기존에 적용한 명단은 그대로 유지됩니다.";
            }        }
        catch (Exception ex)
        {
            RosterImportStatus = "가져오기 실패. 기존 미리보기는 유지됩니다. " +
                (ex is System.IO.InvalidDataException ? ex.Message : "파일 형식·접근 권한·열 제목을 확인해 주세요.");
        }
        finally { IsLoadingRoster = false; }
    }
}
