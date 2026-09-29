using System.Reflection;
using EduStream.Client.ViewModels;

namespace EduStream.FileTransfer.Tests;

public sealed class ClientFileStatusTests
{
    private static void Status(ClientViewModel vm, string message, ClientViewModel.StatusPriority priority, bool error, string? source)
        => typeof(ClientViewModel).GetMethod("UpdateStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [message, priority, error, source]);

    [Fact]
    public async Task FileCompletion_ShouldReplaceOwnProgress()
    {
        var vm = new ClientViewModel();
        try
        {
            Status(vm, "파일 수신 중: 67%", ClientViewModel.StatusPriority.Progress, false, "file");
            Status(vm, "파일 수신 완료: 100%", ClientViewModel.StatusPriority.Success, false, "file");
            Assert.Equal("파일 수신 완료: 100%", vm.StatusMessage);
            Assert.False(vm.IsStatusError);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task FileRetry_ShouldClearOwnError_ButNotUnrelatedConnectionError()
    {
        var vm = new ClientViewModel();
        try
        {
            Status(vm, "파일 오류", ClientViewModel.StatusPriority.Error, true, "file");
            Status(vm, "다시 수신 중", ClientViewModel.StatusPriority.Progress, false, "file");
            Assert.False(vm.IsStatusError);
            Status(vm, "연결 끊김", ClientViewModel.StatusPriority.Error, true, null);
            Status(vm, "파일 완료", ClientViewModel.StatusPriority.Success, false, "file");
            Assert.Equal("연결 끊김", vm.StatusMessage);
            Assert.True(vm.IsStatusError);
        }
        finally { await vm.ShutdownAsync(); }
    }
}
