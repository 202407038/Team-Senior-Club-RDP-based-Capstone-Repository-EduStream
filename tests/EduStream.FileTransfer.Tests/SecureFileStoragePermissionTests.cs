using System.Security.AccessControl;
using System.Security.Principal;

namespace EduStream.FileTransfer.Tests;

public sealed partial class SecureFileRoutingWiringTests
{
    [Fact]
    public async Task DeniedStorage_NoFalseAck_OtherStudentAndRetryStillWork()
    {
        await using var rig = await Rig.OpenAsync();
        var source = await rig.WriteSourceAsync(200000);
        var file = await rig.SessionManager.RegisterFileAsync(source);
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");
        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Count == 1 && bob.Files.Catalog?.Files.Count == 1);
        var stored = 0;
        rig.SessionManager.FileTransfers!.FileStored += (_, _) => Interlocked.Increment(ref stored);
        var directory = Directory.CreateDirectory(Path.Combine(rig.Root, "downloads-Alice"));
        var original = directory.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var denied = directory.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(denied);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => alice.Files.DownloadAsync(file.FileId).WaitAsync(Wait));
            await WaitUntilAsync(() => rig.SessionManager.FileTransfers.PendingTransferCount == 0);
            Assert.Equal(0, Volatile.Read(ref stored));
            Assert.Empty(Directory.GetFiles(directory.FullName));

            // Alice 저장 실패가 Bob의 실제 TLS/TCP 다운로드와 완료 ACK를 막으면 안 됩니다.
            var other = await bob.Files.DownloadAsync(file.FileId).WaitAsync(Wait);
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(other.LocalPath));
            await WaitUntilAsync(() => Volatile.Read(ref stored) == 1);
        }
        finally
        {
            denied.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            directory.SetAccessControl(denied);
        }

        var retry = await alice.Files.DownloadAsync(file.FileId).WaitAsync(Wait);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(retry.LocalPath));
        await WaitUntilAsync(() => Volatile.Read(ref stored) == 2 && rig.SessionManager.FileTransfers.PendingTransferCount == 0);
        Assert.Empty(Directory.GetFiles(directory.FullName, "*.partial"));
    }
}
