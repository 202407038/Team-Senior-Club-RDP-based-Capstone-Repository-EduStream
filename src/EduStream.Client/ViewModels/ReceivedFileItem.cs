using System.IO;
using EduStream.Core.Common;

namespace EduStream.Client.ViewModels;

public sealed class ReceivedFileItem
{
    public string LocalPath { get; }
    public string FileName => Path.GetFileName(LocalPath);
    public RelayCommand RevealCommand { get; }

    public ReceivedFileItem(string localPath, Action<string> reveal)
    {
        if (!Path.IsPathFullyQualified(localPath)) throw new ArgumentException("절대 경로가 필요합니다.", nameof(localPath));
        LocalPath = Path.GetFullPath(localPath);
        RevealCommand = new RelayCommand(() => reveal(LocalPath));
    }
}
