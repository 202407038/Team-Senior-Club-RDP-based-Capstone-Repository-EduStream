using EduStream.Core.Common;
using EduStream.Core.FileSharing;

namespace EduStream.Client.ViewModels;

/// <summary>학생 화면의 강의 파일 목록 한 줄입니다. 다운로드는 항목마다 새 요청으로 시작합니다.</summary>
public sealed class SessionFileItem : ObservableObject
{
    private string _status = "";
    private bool _isDownloading;
    private CancellationTokenSource? _downloadCancellation;

    public SessionFileItem(SessionFileDescriptor file, Func<SessionFileItem, Task> download)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        ArgumentNullException.ThrowIfNull(download);
        DownloadCommand = new RelayCommand(() => _ = download(this), () => !IsDownloading);
        CancelCommand = new RelayCommand(() =>
        {
            if (_downloadCancellation is null || _downloadCancellation.IsCancellationRequested) return;
            Status = "취소 중…";
            _downloadCancellation.Cancel();
            CancelCommand!.RaiseCanExecuteChanged();
        }, () => IsDownloading && _downloadCancellation is { IsCancellationRequested: false });
    }

    public SessionFileDescriptor File { get; }

    public string DisplayText => $"{File.FileName} ({FormatSize(File.Length)})";

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                DownloadCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand DownloadCommand { get; }
    public RelayCommand CancelCommand { get; }

    public CancellationToken BeginDownload()
    {
        if (IsDownloading) throw new InvalidOperationException("이미 다운로드 중입니다.");
        _downloadCancellation = new CancellationTokenSource();
        IsDownloading = true;
        return _downloadCancellation.Token;
    }

    public void EndDownload()
    {
        _downloadCancellation?.Dispose();
        _downloadCancellation = null;
        IsDownloading = false;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };
}
