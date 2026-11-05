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
    private string _submissionStatus = "제출 닫힘";
    public string SubmissionStatus { get => _submissionStatus; private set => SetProperty(ref _submissionStatus, value); }
    private int _submissionProgress;
    public bool IsSubmitting => _submissionCancellation is not null;
    public int SubmissionProgress { get => _submissionProgress; private set => SetProperty(ref _submissionProgress, value); }
    private void InitializeSubmissions()
    {
        SubmitFileCommand = new RelayCommand(() => _ = ChooseSubmissionAsync(), () => _submissionClient?.IsOpen == true && _submissionCancellation is null);
        CancelSubmissionCommand = new RelayCommand(() => _submissionCancellation?.Cancel(), () => _submissionCancellation is not null);
    }
    private void RefreshSubmissionCommands() { SubmitFileCommand.RaiseCanExecuteChanged(); CancelSubmissionCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(IsSubmitting)); }
    private async Task ChooseSubmissionAsync()
    {
        var client = _submissionClient;
        if (client?.IsOpen != true || _submissionCancellation is not null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "교수자에게 제출할 파일 선택", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        await SubmitPathAsync(dialog.FileName);
    }
    public async Task SubmitPathAsync(string path)
    {
        var client = _submissionClient;
        if (client?.IsOpen != true || _submissionCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(); _submissionCancellation = cancellation;
        SubmissionProgress = 0; SubmissionStatus = "파일 확인 및 제출 준비 중…"; RefreshSubmissionCommands();
        try
        {
            await client.SubmitAsync(path, new Progress<int>(p => { if (_submissionCancellation == cancellation) { SubmissionProgress = p; SubmissionStatus = $"전송 {p}%"; } }), cancellation.Token);
            SubmittedFiles.Insert(0, new ReceivedFileItem(path, RevealDownloadedFile));
            SubmissionProgress = 100; SubmissionStatus = "";
        }
        catch (OperationCanceledException) { SubmissionStatus = cancellation.IsCancellationRequested ? "제출 취소 요청됨 · 완료 여부는 교수자에게 확인해 주세요." : "응답 시간 초과 · 접수 완료 여부를 확인하고 다시 시도해 주세요."; }
        catch (Exception ex) { SubmissionStatus = "제출 미확인: " + (ex is IOException or InvalidOperationException ? ex.Message : "파일 또는 연결 상태를 확인해 주세요."); }
        finally { _submissionCancellation = null; RefreshSubmissionCommands(); }
    }
}
