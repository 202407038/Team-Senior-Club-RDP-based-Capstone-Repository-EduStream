using EduStream.Client.ViewModels;
using EduStream.Core.Models;

namespace EduStream.FileTransfer.Tests;

/// <summary>이미 실행한 로컬 교수 앱과 별도 테스트 프로세스의 실제 WDS 수신 경로를 비교한다.</summary>
[Collection("WDS integration")]
public sealed class ExternalServerAutoJoinTests
{
    [ExternalServerFact]
    public async Task RunningProfessorApp_StudentAutoJoin_ConnectsNativeViewer()
    {
        // 사용자가 실행한 무비밀번호 시험 세션만 대상으로 한다. 원격 주소/비밀은 환경 변수로 받지 않는다.
        Assert.True(int.TryParse(Environment.GetEnvironmentVariable("EDUSTREAM_EXTERNAL_SERVER_PORT"), out var port));
        Assert.InRange(port, 1024, 65534);
        await using var viewer = await RdpViewerIntegrationTests.ViewerRig.Create();
        var student = new ClientViewModel(viewer.Viewer)
        {
            HostAddress = "127.0.0.1", Port = port, DisplayName = "ExternalNativeProbe"
        };
        try
        {
            student.JoinSessionCommand.Execute(null);
            await viewer.Connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(student.IsConnected);
            Assert.Equal(RdpConnectionState.Connected, viewer.Latest?.State);
        }
        finally { await student.ShutdownAsync(); }
    }
}

public sealed class ExternalServerFactAttribute : FactAttribute
{
    public ExternalServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EDUSTREAM_WDS_SMOKE") != "1" ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDUSTREAM_EXTERNAL_SERVER_PORT")))
            Skip = "공유 중인 로컬 교수 시험 앱과 EDUSTREAM_WDS_SMOKE=1, EDUSTREAM_EXTERNAL_SERVER_PORT 지정 필요";
    }
}
