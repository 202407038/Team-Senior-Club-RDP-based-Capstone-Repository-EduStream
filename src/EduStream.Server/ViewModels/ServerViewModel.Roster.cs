using System.Collections.ObjectModel;
using EduStream.Core.Common;
using EduStream.Core.Models;
using EduStream.Server.Services;
using System.IO;
using System.Text;

namespace EduStream.Server.ViewModels;

public sealed partial class ServerViewModel
{
    public ObservableCollection<RosterRow> RosterPreviewRows { get; } = [];
    public ObservableCollection<FileDeliveryStatus> FileDeliveries { get; } = [];
    public ObservableCollection<RosterPresence> RosterPresenceRows { get; } = [];
    public ObservableCollection<AttendanceEntry> AttendanceRows { get; } = [];
    public RelayCommand ExportAttendanceCommand { get; private set; } = null!;
    public string AttendanceSummary => $"명단 {RosterPresenceRows.Count}명 · 접속 {RosterPresenceRows.Count(x => x.Connected)}명 · 기록 {AttendanceRows.Count}건";
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
        OnPropertyChanged(nameof(AttendanceSummary)); ExportAttendanceCommand.RaiseCanExecuteChanged();
    }
    private void ExportAttendance()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV 접속 기록 (*.csv)|*.csv", FileName = $"EduStream-접속기록-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, _sessionManager.Attendance.ToCsv(), new UTF8Encoding(true)); RosterImportStatus = "접속 기록을 저장했습니다. 출석 인정은 별도로 확인해 주세요."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RosterImportStatus = "접속 기록 저장 실패. 경로와 파일 사용 여부를 확인해 주세요."; }
    }
    public RelayCommand PreviewRosterCommand { get; }
    public RelayCommand ApplyRosterAdmissionCommand { get; }
    public RelayCommand DisableRosterAdmissionCommand { get; }
    private RosterImportPreview? _rosterPreview;
    public string RosterAdmissionStatus => _sessionManager.IsRosterAdmissionEnabled
        ? $"명단 대조 켜짐 · {_sessionManager.RosterCount}명 · 이름·학번 불일치 시 입장 거부"
        : "명단 대조 꺼짐 · 기존 이름 입력 방식으로 참가 가능";
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
        RosterImportStatus = "미리보기 명단을 적용했습니다. 다음에 여는 세션부터 이름·학번이 일치해야 참가할 수 있습니다.";
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
    private string _rosterImportStatus = "엑셀의 '학생명단' 시트를 확인합니다. 현재는 미리보기이며 입장 제한을 적용하지 않습니다.";
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
            RosterImportStatus = preview.Summary + (preview.CanUseForAdmission
                ? ". 형식 확인 완료. 아래 적용 버튼으로 입장 대조에 사용할 수 있습니다."
                : ". 확인 필요 행을 엑셀에서 수정한 뒤 다시 가져오세요. 입장 대조에는 사용할 수 없습니다.");
            if (_sessionManager.IsRosterAdmissionEnabled) RosterImportStatus += " 기존에 적용한 명단은 그대로 유지됩니다.";
        }
        catch (Exception ex)
        {
            RosterImportStatus = "가져오기 실패. 기존 미리보기는 유지됩니다. " +
                (ex is System.IO.InvalidDataException ? ex.Message : "파일 형식·접근 권한·열 제목을 확인해 주세요.");
        }
        finally { IsLoadingRoster = false; }
    }
}
