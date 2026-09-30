namespace EduStream.Client.Services;

public enum ReconnectAttemptOutcome
{
    /// <summary>다시 참가했습니다.</summary>
    Succeeded,
    /// <summary>교수자 PC에 닿지 않았습니다. 잠시 뒤 다시 시도합니다.</summary>
    RetryLater,
    /// <summary>토큰 거부·세션 종료 등으로 더 시도해도 소용없습니다.</summary>
    GiveUp
}

/// <summary>
/// 2번 구현: 비정상 끊김 뒤 자동 재연결 시도 간격과 제한 시간을 관리합니다. 서버의 토큰 유효 시간 안에서만 시도하고,
/// 점점 간격을 늘려 교수자 PC가 다시 켜질 때 학생 앱들이 한꺼번에 몰리지 않게 합니다.
/// </summary>
public static class ReconnectScheduler
{
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)
    ];

    /// <returns>다시 참가했으면 true입니다. 포기·시간 초과면 false이며, 취소는 예외로 전달됩니다.</returns>
    public static async Task<bool> RunAsync(Func<int, CancellationToken, Task<ReconnectAttemptOutcome>> attempt,
        TimeSpan window, IReadOnlyList<TimeSpan>? delays = null, TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        delays ??= DefaultDelays;
        if (delays.Count == 0) throw new ArgumentException("재시도 간격이 필요합니다.", nameof(delays));
        timeProvider ??= TimeProvider.System;
        var deadline = timeProvider.GetUtcNow() + window;

        for (var number = 1; ; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await attempt(number, cancellationToken))
            {
                case ReconnectAttemptOutcome.Succeeded:
                    return true;
                case ReconnectAttemptOutcome.GiveUp:
                    return false;
            }

            var delay = delays[Math.Min(number - 1, delays.Count - 1)];
            // 다음 시도가 토큰 유효 시간을 넘기면 서버가 어차피 거부하므로 그만둔다.
            if (timeProvider.GetUtcNow() + delay >= deadline) return false;
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }
}
