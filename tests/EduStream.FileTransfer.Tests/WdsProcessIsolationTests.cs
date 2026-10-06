using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using EduStream.Client.ViewModels;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Server.Services;
using EduStream.Server.ViewModels;

namespace EduStream.FileTransfer.Tests;

/// <summary>방 참가/TLS/역방향 공유를 제외하고 별도 프로세스 사이의 실제 WDS 연결만 검사한다.</summary>
[Collection("WDS integration")]
public sealed class WdsProcessIsolationTests
{
    private const string PipeVariable = "EDUSTREAM_WDS_PROBE_PIPE";
    public sealed record Handoff(RdpInvitationPacket? Invitation, string Password, int Port = 0);

    [WdsProcessFact]
    public Task NativeSharingAcrossProcesses_ConnectsWithoutRoomTransport() => RunAsync(false);

    [WdsProcessFact]
    public Task NativeSharingAcrossProcesses_ConnectsThroughRoomTransport() => RunAsync(true);

    private async Task RunAsync(bool useRoom)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var pipeName = Environment.GetEnvironmentVariable(PipeVariable);
        if (!string.IsNullOrEmpty(pipeName))
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(client, leaveOpen: true);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            await using var earlyHost = Environment.GetEnvironmentVariable("EDUSTREAM_WDS_PROBE_EARLY_HOST") == "1"
                ? new RdpSharingService(new InMemoryLogSink()) : null;
            if (earlyHost is not null)
            {
                await earlyHost.StartAsync(Guid.NewGuid(), timeout.Token);
                await writer.WriteLineAsync("early-host-ready");
            }
            var payload = await reader.ReadLineAsync(timeout.Token);
            var received = JsonSerializer.Deserialize<Handoff>(payload!);
            Assert.NotNull(received);
            await using var viewer = await RdpViewerIntegrationTests.ViewerRig.Create();
            ClientViewModel? student = null;
            StudentDesktopHost? reverseHost = null;
            RdpSharingService? secondSharer = null;
            try
            {
                if (useRoom)
                {
                    student = new ClientViewModel(viewer.Viewer)
                    { HostAddress = "127.0.0.1", Port = received.Port, DisplayName = "ProcessProbe" };
                    if (Environment.GetEnvironmentVariable("EDUSTREAM_WDS_PROBE_REVERSE") == "1")
                        typeof(ClientViewModel).GetField("_desktopAttached", BindingFlags.NonPublic | BindingFlags.Instance)!
                            .SetValue(student, true);
                    student.JoinSessionCommand.Execute(null);
                }
                else
                {
                    if (Environment.GetEnvironmentVariable("EDUSTREAM_WDS_PROBE_REVERSE") == "1")
                    {
                        reverseHost = new StudentDesktopHost("ProcessProbe");
                        await reverseHost.StartAsync(Guid.NewGuid(), timeout.Token);
                    }
                    if (Environment.GetEnvironmentVariable("EDUSTREAM_WDS_PROBE_SECOND_SHARER") == "1")
                    {
                        secondSharer = new RdpSharingService(new InMemoryLogSink());
                        await secondSharer.StartAsync(Guid.NewGuid(), timeout.Token);
                    }
                    await viewer.Viewer.ConnectAsync(received.Invitation!, received.Password, timeout.Token);
                }
                await viewer.Connected.Task.WaitAsync(timeout.Token);
                Assert.Equal(RdpConnectionState.Connected, viewer.Latest?.State);
                await writer.WriteLineAsync("connected");
            }
            finally
            {
                if (student is not null) await student.ShutdownAsync();
                if (reverseHost is not null) await reverseHost.DisposeAsync();
                if (secondSharer is not null) await secondSharer.DisposeAsync();
            }
            return;
        }

        // 초대/비밀번호는 현재 사용자 전용 파이프로만 전달한다. 파일/명령줄/로그에 남기지 않는다.
        pipeName = "EduStream-WdsProbe-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var log = new InMemoryLogSink();
        await using var sharer = new RdpSharingService(log);
        ServerViewModel? server = null;
        async Task<Handoff> StartParentAsync()
        {
            if (useRoom)
            {
                server = new ServerViewModel(sharer, LoadProbeCertificate)
                { Port = TestPortAllocator.GetFreePortPair() };
                await (Task)typeof(ServerViewModel).GetMethod("OpenSessionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(server, null)!;
                await server.StartRdpShareAsync();
                Assert.True(server.IsRdpSharing);
                return new(null, "", server.Port);
            }
            var session = Guid.NewGuid();
            var sharing = await sharer.StartAsync(session, timeout.Token);
            var password = Guid.NewGuid().ToString("N");
            var invitation = await sharer.CreateInvitationAsync(session, sharing, "ProcessProbe",
                Guid.NewGuid(), password, DateTimeOffset.UtcNow.AddMinutes(2), timeout.Token);
            return new(invitation, password);
        }
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(WdsProcessIsolationTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=" + typeof(WdsProcessIsolationTests).FullName +
            "." + (useRoom ? nameof(NativeSharingAcrossProcesses_ConnectsThroughRoomTransport) :
                nameof(NativeSharingAcrossProcesses_ConnectsWithoutRoomTransport)));
        start.Environment[PipeVariable] = pipeName;
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            if (Environment.GetEnvironmentVariable("EDUSTREAM_WDS_PROBE_EARLY_HOST") == "1")
                Assert.Equal("early-host-ready", await reader.ReadLineAsync(timeout.Token));
            var handoff = await StartParentAsync();
            await writer.WriteLineAsync(JsonSerializer.Serialize(handoff));
            var result = await reader.ReadLineAsync(timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            Assert.True(child.ExitCode == 0, await output + Environment.NewLine + await error);
            Assert.Equal("connected", result);
            Assert.Contains(log.Snapshot(), entry => entry.Contains("ControlLevel=2"));
        }
        finally
        {
            // 이 검사에서 생성한 자식만 종료한다. 기존 앱/다른 테스트 프로세스는 건드리지 않는다.
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            if (server is not null) await server.ShutdownAsync();
        }
    }

    private static X509Certificate2 LoadProbeCertificate()
    {
        if (Environment.GetEnvironmentVariable("EDUSTREAM_WDS_EXISTING_CERT") != "1")
            return ProfessorCertificateStore.CreateEphemeral();
        // 실제 앱과의 차이를 확인하되 인증서를 새로 만들거나 저장소를 수정하지 않는다.
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName,
                ProfessorCertificateStore.SubjectName, false)
            .Where(c => c.HasPrivateKey && c.NotAfter > DateTime.Now && c.NotBefore <= DateTime.Now)
            .OrderByDescending(c => c.NotAfter).First();
    }

}

public sealed class WdsProcessFactAttribute : FactAttribute
{
    public WdsProcessFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EDUSTREAM_WDS_SMOKE") != "1" ||
            Environment.GetEnvironmentVariable("EDUSTREAM_WDS_PROCESS_PROBE") != "1")
            Skip = "별도 프로세스 WDS 검사는 EDUSTREAM_WDS_SMOKE=1 및 EDUSTREAM_WDS_PROCESS_PROBE=1 필요";
    }
}
