using System.Reflection;
using EduStream.Client.ViewModels;
using EduStream.Core.FileSharing;

namespace EduStream.FileTransfer.Tests;

public sealed class ClientNewFileBadgeTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private static SessionFileDescriptor File() => new(Session, Guid.NewGuid(), 1, "note.txt", 1, new string('a', 64), 1024);
    private static void Apply(ClientViewModel vm, params SessionFileDescriptor[] files) =>
        typeof(ClientViewModel).GetMethod("ApplyFileCatalog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [new SessionFileCatalogSnapshot(Session, 1, files)]);

    [Fact]
    public async Task DeletedUnseenFileClearsBadgeEvenWhenReadFileRemains()
    {
        var vm = new ClientViewModel();
        try
        {
            var read = File(); var unseen = File();
            Apply(vm, read);
            Assert.True(vm.HasNewFiles);
            vm.IsFilesPanelExpanded = true;
            Assert.False(vm.HasNewFiles);
            vm.IsFilesPanelExpanded = false;
            Apply(vm, read, unseen);
            Assert.True(vm.HasNewFiles);
            Apply(vm, read);
            Assert.Single(vm.SessionFiles);
            Assert.False(vm.HasNewFiles);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task BadgeSurvivesUntilLastUnseenFileIsRemoved()
    {
        var vm = new ClientViewModel();
        try
        {
            var first = File(); var second = File();
            Apply(vm, first, second);
            Apply(vm, second);
            Assert.True(vm.HasNewFiles);
            Apply(vm);
            Assert.Empty(vm.SessionFiles);
            Assert.False(vm.HasNewFiles);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task OpenPanelMarksAdditionsReadAndNewRevisionIsUnseenWhenClosed()
    {
        var vm = new ClientViewModel();
        try
        {
            var file = File();
            vm.IsFilesPanelExpanded = true;
            Apply(vm, file);
            Assert.False(vm.HasNewFiles);
            vm.IsFilesPanelExpanded = false;
            Apply(vm, file);
            Assert.False(vm.HasNewFiles);
            Apply(vm, file with { Revision = 2 });
            Assert.True(vm.HasNewFiles);
            vm.IsFilesPanelExpanded = true;
            Assert.False(vm.HasNewFiles);
        }
        finally { await vm.ShutdownAsync(); }
    }
}
