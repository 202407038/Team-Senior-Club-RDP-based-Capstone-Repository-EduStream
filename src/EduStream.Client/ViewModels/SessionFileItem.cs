using EduStream.Core.Common;
using EduStream.Core.FileSharing;

namespace EduStream.Client.ViewModels;

/// <summary>학생 화면의 강의 파일 목록 한 줄입니다. 다운로드는 항목마다 새 요청으로 시작합니다.</summary>
public sealed class SessionFileItem : ObservableObject
{
    private string _status = "받기 전";
    private bool _isDownloading;

    public SessionFileItem(SessionFileDescriptor file, Func<SessionFileItem, Task> download)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        ArgumentNullException.ThrowIfNull(download);
        DownloadCommand = new RelayCommand(() => _ = download(this), () => !IsDownloading);
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
            if (SetProperty(ref _isDownloading, value)) DownloadCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand DownloadCommand { get; }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };
}
