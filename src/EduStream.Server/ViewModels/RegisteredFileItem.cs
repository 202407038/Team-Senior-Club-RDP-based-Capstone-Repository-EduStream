using EduStream.Core.Common;
using EduStream.Core.FileSharing;

namespace EduStream.Server.ViewModels;

/// <summary>교수자 화면의 강의 파일 목록 한 줄입니다. 해제는 목록에서만 내리며 원본 파일은 건드리지 않습니다.</summary>
public sealed class RegisteredFileItem
{
    public RegisteredFileItem(SessionFileDescriptor file, Action<RegisteredFileItem> unregister)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        ArgumentNullException.ThrowIfNull(unregister);
        UnregisterCommand = new RelayCommand(() => unregister(this));
    }

    public SessionFileDescriptor File { get; }

    public string DisplayText => $"{File.FileName} ({FormatSize(File.Length)})";

    public RelayCommand UnregisterCommand { get; }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };
}
