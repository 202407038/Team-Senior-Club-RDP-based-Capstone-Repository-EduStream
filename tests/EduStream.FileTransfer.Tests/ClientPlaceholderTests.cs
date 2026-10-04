using System.Reflection;
using EduStream.Client.ViewModels;

namespace EduStream.FileTransfer.Tests;

public sealed class ClientPlaceholderTests
{
    [Theory]
    [InlineData(false, false, "연결 대기 중", "세션에 참여하면")]
    [InlineData(true, false, "공유 화면 대기 중", "세션에 참가했습니다")]
    [InlineData(false, true, "세션 재연결 중", "다시 연결")]
    [InlineData(true, true, "공유 화면 대기 중", "세션에 참가했습니다")]
    public async Task Placeholder_FollowsConnectionInsteadOfAlwaysAskingToJoin(
        bool connected, bool reconnecting, string title, string subtitle)
    {
        var vm = new ClientViewModel();
        try
        {
            typeof(ClientViewModel).GetProperty(nameof(ClientViewModel.IsConnected))!.SetValue(vm, connected);
            typeof(ClientViewModel).GetMethod("SetReconnecting", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(vm, [reconnecting]);
            Assert.Equal(title, vm.PlaceholderTitle);
            Assert.Contains(subtitle, vm.PlaceholderSubtitle);
            // 퇴장 뒤에는 이전 참가 상태 안내가 남지 않는다.
            typeof(ClientViewModel).GetProperty(nameof(ClientViewModel.IsConnected))!.SetValue(vm, false);
            typeof(ClientViewModel).GetMethod("SetReconnecting", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(vm, [false]);
            Assert.Equal("연결 대기 중", vm.PlaceholderTitle);
        }
        finally { await vm.ShutdownAsync(); }
    }
}
