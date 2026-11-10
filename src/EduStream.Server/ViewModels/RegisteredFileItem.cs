using EduStream.Core.Common;
using EduStream.Core.FileSharing;

namespace EduStream.Server.ViewModels;

/// <summary>교수자 화면의 강의 파일 목록 한 줄입니다. 해제는 목록에서만 내리며 원본 파일은 건드리지 않습니다.</summary>
public sealed class RegisteredFileItem : ObservableObject
{
    public RegisteredFileItem(SessionFileDescriptor file, Action<RegisteredFileItem> unregister,
        string sourcePath = "", Func<RegisteredFileItem, Task>? sendAll = null)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        ArgumentNullException.ThrowIfNull(unregister);
        SourcePath = sourcePath;
        UnregisterCommand = new RelayCommand(() => unregister(this), () => !IsSending);
        SendAllCommand = new RelayCommand(() => _ = SendAsync(), () => !IsSending && sendAll is not null);
        async Task SendAsync()
        {
            if (IsSending || sendAll is null) return;
            IsSending = true;
            try { await sendAll!(this); }
            finally { IsSending = false; }
        }
    }

    public SessionFileDescriptor File { get; }

    public string DisplayText => $"{File.FileName} ({FormatSize(File.Length)})";

    public RelayCommand UnregisterCommand { get; }
    public RelayCommand SendAllCommand { get; }
    public string SourcePath { get; }
    private bool _isSending;
    public bool IsSending
    {
        get => _isSending;
        private set { if (SetProperty(ref _isSending, value)) { UnregisterCommand.RaiseCanExecuteChanged(); SendAllCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(SendLabel)); } }
    }
    public string SendLabel => IsSending ? "전송 중" : "전체 전송";

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };
}
