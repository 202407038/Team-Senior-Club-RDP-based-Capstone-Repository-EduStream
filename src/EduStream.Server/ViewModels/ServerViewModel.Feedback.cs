using System.IO;
using System.Security.Cryptography;
using EduStream.Core.Common;
using EduStream.Core.Models;

namespace EduStream.Server.ViewModels;

public sealed partial class ServerViewModel
{
    private bool _announcementComposerOpen, _announcementImportant, _sendingAnnouncement;
    private string _announcementInput = "", _announcementError = "";
    public bool IsAnnouncementComposerOpen { get => _announcementComposerOpen; private set => SetProperty(ref _announcementComposerOpen, value); }
    public string AnnouncementTitle => _announcementImportant ? "중요 공지" : "공지";
    public string AnnouncementInput { get => _announcementInput; set { if (SetProperty(ref _announcementInput, value)) PublishAnnouncementCommand.RaiseCanExecuteChanged(); } }
    public string AnnouncementError { get => _announcementError; private set => SetProperty(ref _announcementError, value); }
    public RelayCommand ComposeAnnouncementCommand { get; private set; } = null!;
    public RelayCommand ComposeImportantAnnouncementCommand { get; private set; } = null!;
    public RelayCommand CloseAnnouncementComposerCommand { get; private set; } = null!;
    public RelayCommand PublishAnnouncementCommand { get; private set; } = null!;
    private void InitializeAnnouncementComposer()
    {
        ComposeAnnouncementCommand = new RelayCommand(() => Open(false), () => !_sendingAnnouncement);
        ComposeImportantAnnouncementCommand = new RelayCommand(() => Open(true), () => !_sendingAnnouncement);
        CloseAnnouncementComposerCommand = new RelayCommand(() => IsAnnouncementComposerOpen = false, () => !_sendingAnnouncement);
        PublishAnnouncementCommand = new RelayCommand(() => _ = PublishAnnouncementAsync(), () => IsSessionOpen && !_sendingAnnouncement && !string.IsNullOrWhiteSpace(AnnouncementInput));
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsSessionOpen)) PublishAnnouncementCommand.RaiseCanExecuteChanged(); };
        void Open(bool important) { _announcementImportant = important; OnPropertyChanged(nameof(AnnouncementTitle)); AnnouncementError = ""; IsAnnouncementComposerOpen = true; }
    }
    private async Task PublishAnnouncementAsync()
    {
        if (!PublishAnnouncementCommand.CanExecute(null)) return;
        var input = AnnouncementInput; var message = input.Trim();
        if (message.Length > 500) { AnnouncementError = "500자 이내로 입력해 주세요."; return; }
        _sendingAnnouncement = true; Refresh();
        try
        {
            await _sessionManager.BroadcastPacketAsync(new ChatPacket { Sender = "Professor", Message = message,
                IsAnnouncement = true, IsImportantAnnouncement = _announcementImportant, DataLength = System.Text.Encoding.UTF8.GetByteCount(message) });
            if (AnnouncementInput == input) { AnnouncementInput = ""; IsAnnouncementComposerOpen = false; }
            AnnouncementError = "";
        }
        catch (Exception ex) { AnnouncementError = "공지 전송 실패 · 연결을 확인하고 다시 보내 주세요."; _logSink.Write($"[Announcement] {ex.GetType().Name}"); }
        finally { _sendingAnnouncement = false; Refresh(); }
        void Refresh() { PublishAnnouncementCommand.RaiseCanExecuteChanged(); ComposeAnnouncementCommand.RaiseCanExecuteChanged(); ComposeImportantAnnouncementCommand.RaiseCanExecuteChanged(); CloseAnnouncementComposerCommand.RaiseCanExecuteChanged(); }
    }
    private async Task SendRegisteredFileAsync(RegisteredFileItem item)
    {
        try
        {
            if (!IsSessionOpen || !RegisteredFiles.Contains(item) || _sessionManager.CurrentSession?.SessionId != item.File.SessionId)
                throw new InvalidOperationException("현재 세션에 등록된 파일이 아닙니다.");
            if (ParticipantCount == 0) throw new InvalidOperationException("접속한 학생이 없습니다.");
            // 등록 후 바뀐 원본을 같은 파일로 보내지 않는다. 송신 동안 쓰기/삭제도 차단한다.
            await using var locked = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(locked));
            if (locked.Length != item.File.Length || !hash.Equals(item.File.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("원본이 변경되었습니다. 해제 후 다시 등록해 주세요.");
            await SendFileAsync(item.SourcePath, "전체");
        }
        catch (Exception ex)
        {
            FileShareStatus = ex is InvalidOperationException ? ex.Message : "파일을 보낼 수 없습니다. 원본 파일을 확인해 주세요.";
            StatusMessage = FileShareStatus; IsStatusError = true;
        }
    }
}
