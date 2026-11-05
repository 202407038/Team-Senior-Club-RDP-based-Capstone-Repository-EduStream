using System.Reflection;
using System.Windows.Input;
using EduStream.Client.Services;
using EduStream.Client.ViewModels;
using EduStream.Server;
using EduStream.Server.Services;
using EduStream.Server.ViewModels;

namespace EduStream.FileTransfer.Tests;

public sealed class UsabilityInteractionTests
{
    [Fact]
    public void ExplorerReceivesExactFilePathAndNeverExecutesDownloadedFile()
    {
        using var folder = new ReadinessFiles();
        Directory.CreateDirectory(folder.Downloads);
        var path = Path.Combine(folder.Downloads, "자료 & 메모 (1).exe");
        File.WriteAllBytes(path, [1, 2, 3]);
        var info = DownloadLocationOpener.CreateStartInfo(path, selectFile: true);
        Assert.EndsWith("explorer.exe", info.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.False(info.UseShellExecute);
        Assert.Equal(new[] { "/select,", path }, info.ArgumentList);
        var directory = DownloadLocationOpener.CreateStartInfo(folder.Downloads, selectFile: false);
        Assert.Equal(new[] { folder.Downloads }, directory.ArgumentList);
        File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => DownloadLocationOpener.CreateStartInfo(path, true));
        Assert.Throws<IOException>(() => DownloadLocationOpener.CreateStartInfo("relative.txt", true));
    }

    [Fact]
    public async Task CompletedFileKeepsExactPathAndOpenerFailureIsVisible()
    {
        using var folder = new ReadinessFiles();
        Directory.CreateDirectory(folder.Downloads);
        var opener = new RecordingOpener();
        var vm = new ClientViewModel(downloadsDirectory: new ReadinessDirectory(folder.Downloads), downloadLocationOpener: opener);
        try
        {
            var path = Path.Combine(folder.Downloads, "강의 (2).txt");
            typeof(ClientViewModel).GetMethod("RecordDownloadedFile", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [path]);
            var item = Assert.Single(vm.ReceivedFiles);
            Assert.Equal("강의 (2).txt", item.FileName);
            Assert.Equal(path, item.LocalPath);
            Assert.Null(opener.Revealed); // 수신 완료만으로 파일/탐색기를 자동 실행하지 않는다.
            item.RevealCommand.Execute(null);
            Assert.Equal(path, opener.Revealed);
            vm.OpenDownloadsFolderCommand.Execute(null);
            Assert.Equal(folder.Downloads, opener.Folder);
            opener.Fail = true;
            item.RevealCommand.Execute(null);
            Assert.Contains("이동·삭제", vm.DownloadStatus);
            vm.OpenDownloadsFolderCommand.Execute(null);
            Assert.Contains("다운로드 폴더를 열지 못했습니다", vm.DownloadStatus);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task PanelTogglePreservesFileStateAndErrorShowsPanel()
    {
        var vm = new ClientViewModel();
        try
        {
            vm.IsFilesPanelExpanded = true;
            var original = vm.ConnectionState;
            vm.ToggleCompanionPanelCommand.Execute(null);
            Assert.False(vm.IsCompanionPanelVisible);
            Assert.Contains("펼치기", vm.CompanionPanelToggleText);
            Assert.True(vm.IsFilesPanelExpanded);
            Assert.Equal(original, vm.ConnectionState);
            typeof(ClientViewModel).GetProperty(nameof(ClientViewModel.IsStatusError))!.SetValue(vm, true);
            Assert.True(vm.IsCompanionPanelVisible);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public void SearchCyclesWithoutChangingList()
    {
        string[] names = ["Student02", "홍길동", "Student01"];
        Assert.Equal(0, StudentNameSearch.FindNext(names, "student", -1));
        Assert.Equal(2, StudentNameSearch.FindNext(names, "student", 0));
        Assert.Equal(0, StudentNameSearch.FindNext(names, "student", 2));
        Assert.Equal(1, StudentNameSearch.FindNext(names, " 홍 ", -1));
        Assert.Equal(-1, StudentNameSearch.FindNext(names, "", -1));
        Assert.Equal(-1, StudentNameSearch.FindNext(names, "없음", -1));
        Assert.Equal(-1, StudentNameSearch.FindNext([], "a", 30));
        Assert.Equal(0, StudentNameSearch.FindNext(names, "Student", 30));
        Assert.Equal(new[] { "Student02", "홍길동", "Student01" }, names);
    }

    [Fact]
    public async Task EndConfirmationOnlyWhenParticipantsExistAndCancelDoesNotClose()
    {
        var vm = new ServerViewModel();
        try
        {
            var calls = 0;
            vm.ConfirmSessionEnd = count => { calls++; Assert.Equal(2, count); return false; };
            Assert.True(vm.CanEndSession());
            Assert.Equal(0, calls);
            typeof(ServerViewModel).GetProperty(nameof(ServerViewModel.ParticipantCount))!.SetValue(vm, 2);
            typeof(ServerViewModel).GetProperty(nameof(ServerViewModel.IsSessionOpen))!.SetValue(vm, true);
            vm.CloseSessionCommand.Execute(null);
            Assert.Equal(1, calls);
            Assert.True(vm.IsSessionOpen);
            Assert.False(vm.IsBusy);
            vm.ConfirmSessionEnd = _ => true;
            Assert.True(vm.CanEndSession());
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(true, false, false, false, Key.Z, ModifierKeys.Control, true)]
    [InlineData(false, false, false, false, Key.Z, ModifierKeys.Control, false)]
    [InlineData(true, true, false, false, Key.Z, ModifierKeys.Control, false)]
    [InlineData(true, false, true, false, Key.Z, ModifierKeys.Control, false)]
    [InlineData(true, false, false, true, Key.Z, ModifierKeys.Control, false)]
    [InlineData(true, false, false, false, Key.Z, ModifierKeys.Control | ModifierKeys.Shift, false)]
    [InlineData(true, false, false, false, Key.ImeProcessed, ModifierKeys.Control, false)]
    public void UndoOnlyInIdleDrawingMode(bool drawing, bool busy, bool text, bool gesture, Key key, ModifierKeys modifiers, bool expected) =>
        Assert.Equal(expected, AnnotationShortcuts.IsUndo(drawing, busy, text, gesture, key, modifiers));

    private sealed class RecordingOpener : IDownloadLocationOpener
    {
        public string? Folder;
        public string? Revealed;
        public bool Fail;
        public void OpenFolder(string path) { if (Fail) throw new IOException(); Folder = path; }
        public void RevealFile(string path) { if (Fail) throw new IOException(); Revealed = path; }
    }
}
