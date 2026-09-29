using EduStream.Core.Logging;
using EduStream.Server.Services;

namespace RdpSharingDemo;

/// <summary>
/// RDP 공유 서비스 단독 실행 데모 프로그램
/// 실제 WDS RDPSession COM 객체를 사용하여 공유 시작/중지/초대 생성을 테스트합니다.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== RDP 공유 서비스 데모 ===");
        Console.WriteLine();

        var logSink = new ConsoleLogSink();
        var service = new RdpSharingService(logSink);

        var sessionId = Guid.NewGuid();

        try
        {
            // 1. 공유 시작
            Console.WriteLine("1. 공유 시작 시도...");
            var sharingId = await service.StartAsync(sessionId);
            Console.WriteLine($"   공유 시작 성공: SharingId={sharingId}");
            Console.WriteLine();

            // 2. 첫 번째 학생 초대 생성
            Console.WriteLine("2. 첫 번째 학생 초대 생성...");
            var invitation1 = await service.CreateInvitationAsync(
                sessionId,
                sharingId,
                "student1",
                Guid.NewGuid(),
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow.AddMinutes(5));
            Console.WriteLine($"   초대 생성 성공: InvitationId={invitation1.InvitationId}");
            Console.WriteLine("   ConnectionString: [REDACTED]");
            Console.WriteLine($"   DataLength: {invitation1.DataLength}");
            Console.WriteLine();

            // 3. 두 번째 학생 초대 생성
            Console.WriteLine("3. 두 번째 학생 초대 생성...");
            var invitation2 = await service.CreateInvitationAsync(
                sessionId,
                sharingId,
                "student2",
                Guid.NewGuid(),
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow.AddMinutes(5));
            Console.WriteLine($"   초대 생성 성공: InvitationId={invitation2.InvitationId}");
            Console.WriteLine("   ConnectionString: [REDACTED]");
            Console.WriteLine($"   DataLength: {invitation2.DataLength}");
            Console.WriteLine();

            // 4. 세 번째 학생 초대 시도 (최대 참가자 수 초과)
            Console.WriteLine("4. 세 번째 학생 초대 시도 (최대 참가자 수 초과 예상)...");
            try
            {
                await service.CreateInvitationAsync(
                    sessionId,
                    sharingId,
                    "student3",
                    Guid.NewGuid(),
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow.AddMinutes(5));
                Console.WriteLine("   예외 발생하지 않음 (실패)");
                Environment.ExitCode = 1;
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"   예상된 예외 발생: {ex.Message}");
            }
            Console.WriteLine();

            // 5. 첫 번째 초대 폐기
            Console.WriteLine("5. 첫 번째 초대 폐기...");
            await service.RevokeInvitationAsync(invitation1.InvitationId);
            Console.WriteLine($"   초대 폐기 성공: InvitationId={invitation1.InvitationId}");
            Console.WriteLine();

            // 6. 폐기 후 새로운 초대 생성
            Console.WriteLine("6. 같은 학생 재접속용 초대 생성...");
            var invitation3 = await service.CreateInvitationAsync(
                sessionId,
                sharingId,
                "student1",
                Guid.NewGuid(),
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow.AddMinutes(5));
            Console.WriteLine($"   초대 생성 성공: InvitationId={invitation3.InvitationId}");
            Console.WriteLine("   ConnectionString: [REDACTED]");
            Console.WriteLine();

            // 7. 공유 중지
            Console.WriteLine("7. 공유 중지...");
            await service.StopAsync();
            Console.WriteLine("   공유 중지 성공");
            Console.WriteLine();

            // 8. 재시작 시도 (새로운 SharingId)
            Console.WriteLine("8. 재시작 시도 (새로운 SharingId 생성 확인)...");
            var newSharingId = await service.StartAsync(Guid.NewGuid());
            Console.WriteLine($"   공유 재시작 성공: SharingId={newSharingId}");
            Console.WriteLine($"   이전 SharingId와 다름: {newSharingId != sharingId}");
            Console.WriteLine();

            // 9. 정리
            Console.WriteLine("9. 정리...");
            await service.StopAsync();
            Console.WriteLine("   정리 완료");
        }
        catch (PlatformNotSupportedException ex)
        {
            Environment.ExitCode = 1;
            Console.WriteLine($"플랫폼 지원 오류: {ex.Message}");
            Console.WriteLine("Windows Desktop Sharing API가 설치되지 않았거나 지원되지 않는 플랫폼입니다.");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            Console.WriteLine($"오류 발생: {ex.Message}");
            Console.WriteLine($"스택 추적: {ex.StackTrace}");
        }
        finally
        {
            await service.DisposeAsync();
        }

        Console.WriteLine();
        Console.WriteLine("=== 데모 종료 ===");
        Console.WriteLine("아무 키나 누르면 종료합니다...");
        if (!Console.IsInputRedirected && !args.Contains("--non-interactive")) Console.ReadKey();
    }
}

/// <summary>
/// 콘솔 로그 싱크
/// </summary>
public class ConsoleLogSink : ILogSink
{
    private readonly List<string> _logs = new();

    public void Write(string message)
    {
        var logMessage = $"[{DateTimeOffset.UtcNow:HH:mm:ss}] {message}";
        Console.WriteLine(logMessage);
        _logs.Add(logMessage);
    }

    public IReadOnlyList<string> Snapshot()
    {
        return _logs.AsReadOnly();
    }
}
