using EduStream.Client.Services;
using EduStream.Core.Common;
using System.IO;

namespace EduStream.Client.ViewModels;

public sealed partial class ClientViewModel
{
    public System.Collections.ObjectModel.ObservableCollection<ReceivedFileItem> SubmittedFiles { get; } = [];
    private SubmissionClient? _submissionClient;
    private CancellationTokenSource? _submissionCancellation;
    public RelayCommand SubmitFileCommand { get; private set; } = null!;
    public RelayCommand CancelSubmissionCommand { get; private set; } = null!;
    public RelayCommand SendSelectedSubmissionCommand { get; private set; } = null!;
    public RelayCommand ClearSelectedSubmissionCommand { get; private set; } = null!;
    private string _selectedSubmissionPath = "";
    /// <summary>제출 대기 중인 파일. 고르거나 끌어 놓기만 하고, 전송은 '제출하기'를 눌러야 한다.</summary>
    public string SelectedSubmissionPath
    {
        get => _selectedSubmissionPath;
        private set { if (SetProperty(ref _selectedSubmissionPath, value)) { OnPropertyChanged(nameof(SelectedSubmissionName)); OnPropertyChanged(nameof(HasSelectedSubmission)); RefreshSubmissionCommands(); } }
    }
    private bool _isSubmissionOpen;
    /// <summary>교수자가 제출을 열어 둔 상태. '파일' 제목 옆에 '제출 열림'을 띄운다.</summary>
    public bool IsSubmissionOpen { get => _isSubmissionOpen; private set => SetProperty(ref _isSubmissionOpen, value); }
    private bool _isSubmissionPanelExpanded;
    public bool IsSubmissionPanelExpanded { get => _isSubmissionPanelExpanded; set => SetProperty(ref _isSubmissionPanelExpanded, value); }
    public string SelectedSubmissionName => Path.GetFileName(_selectedSubmissionPath);
    public bool HasSelectedSubmission => _selectedSubmissionPath.Length > 0;
    private string _submissionStatus = "제출 닫힘";
    public string SubmissionStatus { get => _submissionStatus; private set => SetProperty(ref _submissionStatus, value); }
    private int _submissionProgress;
    public bool IsSubmitting => _submissionCancellation is not null;
    public int SubmissionProgress { get => _submissionProgress; private set => SetProperty(ref _submissionProgress, value); }
    private void InitializeSubmissions()
    {
        SubmitFileCommand = new RelayCommand(ChooseSubmission, () => _submissionCancellation is null);
        CancelSubmissionCommand = new RelayCommand(() => _submissionCancellation?.Cancel(), () => _submissionCancellation is not null);
        SendSelectedSubmissionCommand = new RelayCommand(() => _ = SendSelectedSubmissionAsync(),
            () => HasSelectedSubmission && _submissionClient?.IsOpen == true && _submissionCancellation is null);
        ClearSelectedSubmissionCommand = new RelayCommand(() => SelectedSubmissionPath = "", () => HasSelectedSubmission && _submissionCancellation is null);
    }
    private void RefreshSubmissionCommands()
    {
        // 생성자 초기화 중 선택 경로가 바뀌어도 안전하도록 null을 허용한다.
        SubmitFileCommand?.RaiseCanExecuteChanged(); CancelSubmissionCommand?.RaiseCanExecuteChanged();
        SendSelectedSubmissionCommand?.RaiseCanExecuteChanged(); ClearSelectedSubmissionCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsSubmitting));
    }
    private void ChooseSubmission()
    {
        if (_submissionCancellation is not null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "교수자에게 제출할 파일 선택", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        PrepareSubmissionFile([dialog.FileName]);
    }
    /// <summary>파일 선택창·끌어 놓기 공통. 제출할 파일을 정해 두기만 하고 보내지는 않는다.</summary>
    public void PrepareSubmissionFile(IEnumerable<string> paths)
    {
        if (_submissionCancellation is not null) return;
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length != 1 || !File.Exists(files[0]))
        {
            SubmissionStatus = "파일을 한 개씩 끌어 놓아 주세요. 폴더는 제출할 수 없습니다.";
            return;
        }
        SelectedSubmissionPath = files[0];
        SubmissionStatus = _submissionClient?.IsOpen == true ? "'제출하기'를 누르면 교수자에게 보냅니다." : "교수자가 제출을 열면 '제출하기'를 누를 수 있습니다.";
    }
    private async Task SendSelectedSubmissionAsync()
    {
        var path = SelectedSubmissionPath;
        if (path.Length == 0) return;
        if (await SubmitPathAsync(path) && SelectedSubmissionPath == path) SelectedSubmissionPath = "";
    }
    public async Task<bool> SubmitPathAsync(string path)
    {
        var client = _submissionClient;
        if (client?.IsOpen != true || _submissionCancellation is not null) return false;
        using var cancellation = new CancellationTokenSource(); _submissionCancellation = cancellation;
        SubmissionProgress = 0; SubmissionStatus = "파일 확인 및 제출 준비 중…"; RefreshSubmissionCommands();
        try
        {
            await client.SubmitAsync(path, new Progress<int>(p => { if (_submissionCancellation == cancellation) { SubmissionProgress = p; SubmissionStatus = $"전송 {p}%"; } }), cancellation.Token);
            SubmittedFiles.Insert(0, new ReceivedFileItem(path, RevealDownloadedFile));
            SubmissionProgress = 100; SubmissionStatus = "제출 완료 · " + Path.GetFileName(path);
            return true;
        }
        catch (OperationCanceledException) { SubmissionStatus = cancellation.IsCancellationRequested ? "제출 취소 요청됨 · 완료 여부는 교수자에게 확인해 주세요." : "응답 시간 초과 · 접수 완료 여부를 확인하고 다시 시도해 주세요."; }
        catch (Exception ex) { SubmissionStatus = "제출 미확인: " + (ex is IOException or InvalidOperationException ? ex.Message : "파일 또는 연결 상태를 확인해 주세요."); }
        finally { _submissionCancellation = null; RefreshSubmissionCommands(); }
        return false;
    }
}
