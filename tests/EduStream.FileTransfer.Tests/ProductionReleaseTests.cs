using System.Reflection;
using EduStream.Client.ViewModels;
using EduStream.Server.ViewModels;

namespace EduStream.FileTransfer.Tests;

public sealed class ProductionReleaseTests
{
    [Fact]
    public async Task FreshClientRequiresNameInsteadOfUsingDemoIdentity()
    {
        var client = new ClientViewModel();
        try
        {
            Assert.Empty(client.DisplayName);
            Assert.Empty(client.StudentId);
            client.JoinSessionCommand.Execute(null);
            Assert.False(client.IsConnected);
            Assert.False(client.IsConnecting);
            Assert.Contains("이름을 입력", client.StatusMessage);
        }
        finally { await client.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("SimulateScreenRenderCommand")]
    [InlineData("SimulateFileReceiveCommand")]
    public void ClientDoesNotExposeSyntheticReceiveCommands(string command)
        => Assert.Null(typeof(ClientViewModel).GetProperty(command));

    [Theory]
    [InlineData(typeof(ClientViewModel))]
    [InlineData(typeof(ServerViewModel))]
    public void DesktopAssembliesHaveStableReleaseMetadata(Type appType)
    {
        var assembly = appType.Assembly;
        Assert.Equal(new Version(1, 1, 2, 0), assembly.GetName().Version);
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal("1.1.2", info.Split('+')[0]);
        Assert.DoesNotContain("preview", info, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test", info, StringComparison.OrdinalIgnoreCase);
    }
}
