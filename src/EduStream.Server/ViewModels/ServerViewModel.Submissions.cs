using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using EduStream.Core.Common;
using EduStream.Server.Services;

namespace EduStream.Server.ViewModels;

public sealed partial class ServerViewModel
{
    public ObservableCollection<SubmissionReceiptItem> SubmissionReceipts { get; } = [];
    /// <summary>접혀 있어도 새 제출을 알 수 있게 개수와 열림 상태를 제목에 보인다.</summary>
    public string SubmissionHeader => "학생 파일 제출" +
        (SubmissionReceipts.Count > 0 ? $" ({SubmissionReceipts.Count})" : "") +
        (_sessionManager.Submissions?.IsOpen == true ? " · 열림" : "");
    public RelayCommand ToggleSubmissionsCommand { get; private set; } = null!;
    public RelayCommand OpenSubmissionsFolderCommand { get; private set; } = null!;
    private string? _submissionFolder;
    private bool _changingSubmissions;
    public string SubmissionToggleText => _sessionManager.Submissions?.IsOpen == true ? "제출 닫기" : "제출 열기";
    private string _submissionStatus = "제출 닫힘";
    public string SubmissionStatus { get => _submissionStatus; private set => SetProperty(ref _submissionStatus, value); }
    private void InitializeSubmissionUi()
    {
        ToggleSubmissionsCommand = new RelayCommand(() => _ = ToggleSubmissionsAsync(), () => IsSessionOpen && !_changingSubmissions);
        OpenSubmissionsFolderCommand = new RelayCommand(() =>
        {
            try { if (_submissionFolder is { } path && Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            catch { SubmissionStatus = "저장 폴더를 열지 못했습니다."; }
        }, () => _submissionFolder is not null && Directory.Exists(_submissionFolder));
        _sessionManager.SubmissionsChanged += () => RunOnUi(RefreshSubmissionUi);
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsSessionOpen)) RefreshSubmissionUi(); };
    }
    private void RefreshSubmissionUi()
    {
        if (_sessionManager.Submissions is { } store)
        {
            _submissionFolder = store.DirectoryPath;
            SubmissionReceipts.Clear(); foreach (var receipt in store.Receipts.Reverse()) SubmissionReceipts.Add(new SubmissionReceiptItem(receipt, RevealSubmission));
            SubmissionStatus = store.IsOpen ? "제출 열림" : "제출 닫힘";
        }
        else SubmissionStatus = "제출 닫힘";
        OnPropertyChanged(nameof(SubmissionToggleText)); OnPropertyChanged(nameof(SubmissionHeader)); ToggleSubmissionsCommand.RaiseCanExecuteChanged(); OpenSubmissionsFolderCommand.RaiseCanExecuteChanged();
    }
    private void RevealSubmission(string path)
    {
        try
        {
            if (!File.Exists(path)) { SubmissionStatus = "제출 파일이 이동 또는 삭제되었습니다."; return; }
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
            start.ArgumentList.Add("/select,"); start.ArgumentList.Add(Path.GetFullPath(path));
            using var process = Process.Start(start);
        }
        catch { SubmissionStatus = "파일 위치를 열지 못했습니다."; }
    }
    private async Task ToggleSubmissionsAsync()
    {
        if (!ToggleSubmissionsCommand.CanExecute(null)) return;
        _changingSubmissions = true; ToggleSubmissionsCommand.RaiseCanExecuteChanged();
        bool failed = false;
        try { await _sessionManager.SetSubmissionsOpenAsync(!(_sessionManager.Submissions?.IsOpen ?? false)); }
        catch { failed = true; }
        finally
        {
            _changingSubmissions = false; RefreshSubmissionUi();
            if (failed) SubmissionStatus += " · 상태 알림 전송을 완료하지 못했습니다. 연결 상태를 확인해 주세요.";
        }
    }
}

/// <summary>제출 목록 한 줄. 파일 위치 열기와 받은 시간을 함께 보인다.</summary>
public sealed class SubmissionReceiptItem(SubmissionReceipt receipt, Action<string> reveal)
{
    public SubmissionReceipt Receipt { get; } = receipt;
    public string FileName => Receipt.FileName;
    public string Student => Receipt.Student;
    public string LocalPath => Receipt.LocalPath;
    public string TimeText => Receipt.At.ToLocalTime().ToString("HH:mm:ss");
    public RelayCommand RevealCommand { get; } = new(() => reveal(receipt.LocalPath));
}
