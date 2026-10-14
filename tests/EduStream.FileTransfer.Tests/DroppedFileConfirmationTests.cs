using System.Reflection;
using EduStream.Core.Network;
using EduStream.Server.ViewModels;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

[Collection("WDS integration")]
public sealed class DroppedFileConfirmationTests
{
    private static Task Invoke(ServerViewModel vm, string name) =>
        (Task)typeof(ServerViewModel).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;

    [Fact]
    public async Task DropBeforeSessionPreparesOnlyAndRegistrationRequiresExplicitAction()
    {
        using var files = new ReadinessFiles();
        var path = await files.SourceAsync(128);
        var vm = new ServerViewModel(certificateProvider: ProfessorCertificateStore.CreateEphemeral)
            { Port = TestPortAllocator.GetFreePortPair() };
        try
        {
            vm.PrepareDroppedFile([path]);
            Assert.Equal(path, vm.SelectedFilePath);
            Assert.Empty(vm.RegisteredFiles);
            Assert.False(vm.RegisterSelectedFileCommand.CanExecute(null));
            await Invoke(vm, "OpenSessionAsync");
            Assert.True(vm.IsSessionOpen);
            vm.PrepareDroppedFile([path]);
            Assert.Empty(vm.SessionManager.GetFileCatalogSnapshot()!.Files);
            Assert.Empty(vm.RegisteredFiles);
            Assert.True(vm.RegisterSelectedFileCommand.CanExecute(null));
            await Invoke(vm, "RegisterSelectedFileAsync");
            Assert.Single(vm.RegisteredFiles);
            Assert.Single(vm.SessionManager.GetFileCatalogSnapshot()!.Files);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task MultipleFilesDirectoriesAndMissingFilesDoNotReplaceSelectionOrRegister()
    {
        using var files = new ReadinessFiles();
        var first = await files.SourceAsync(10, "first.txt");
        var second = await files.SourceAsync(10, "second.txt");
        var vm = new ServerViewModel();
        try
        {
            vm.PrepareDroppedFile([first]);
            foreach (var paths in new[] { new[] { first, second }, new[] { files.Root }, new[] { second + ".missing" }, Array.Empty<string>() })
            {
                vm.PrepareDroppedFile(paths);
                Assert.Equal(first, vm.SelectedFilePath);
                Assert.Empty(vm.RegisteredFiles);
            }
            vm.PrepareDroppedFile([second]);
            Assert.Equal(second, vm.SelectedFilePath);
            Assert.Empty(vm.RegisteredFiles);
        }
        finally { await vm.ShutdownAsync(); }
    }
}
