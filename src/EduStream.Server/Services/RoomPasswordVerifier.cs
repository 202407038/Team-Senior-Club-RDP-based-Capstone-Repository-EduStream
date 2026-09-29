using System.Security.Cryptography;
using System.Text;

namespace EduStream.Server.Services;

public enum RoomPasswordResult { Accepted, Rejected, LockedOut }

/// <summary>
/// 2번 구현: 방 비밀번호를 평문으로 보관하지 않고 솔트 PBKDF2 해시로만 검증합니다.
/// 같은 시도 키(원격 주소 권장)에서 연속 실패하면 일정 시간 잠급니다.
/// </summary>
/// <remarks>
/// 현재 단계에서는 보호 채널(교수자 신원 검증·암호화) 계약이 확정되지 않아 학생 비밀번호를 이 검증기로
/// 전달하는 wire 경로가 없습니다. 기존 v1 평문 TCP 패킷에 비밀번호를 싣지 않습니다.
/// </remarks>
public sealed class RoomPasswordVerifier
{
    public const int MaxPasswordLength = 128;
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(1);

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;
    // 시도 기록이 무한히 쌓이지 않도록 상한을 넘으면 잠기지 않은 항목부터 비운다.
    private const int MaxTrackedAttemptKeys = 1024;

    private readonly byte[] _salt;
    private readonly byte[] _hash;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, AttemptState> _attempts = new(StringComparer.Ordinal);

    private RoomPasswordVerifier(byte[] salt, byte[] hash, TimeProvider timeProvider)
    {
        _salt = salt;
        _hash = hash;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 비밀번호가 비어 있으면 비밀번호 없는 방이므로 null을 반환합니다.
    /// 공백만 있거나 너무 긴 값은 교수자 입력 오류로 거부합니다.
    /// </summary>
    public static RoomPasswordVerifier? Create(ReadOnlySpan<char> password, TimeProvider? timeProvider = null)
    {
        if (password.IsEmpty)
            return null;
        if (password.IsWhiteSpace())
            throw new ArgumentException("방 비밀번호는 공백만으로 지정할 수 없습니다.", nameof(password));
        if (password.Length > MaxPasswordLength)
            throw new ArgumentException($"방 비밀번호는 {MaxPasswordLength}자 이하여야 합니다.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new RoomPasswordVerifier(salt, Derive(password, salt), timeProvider ?? TimeProvider.System);
    }

    public RoomPasswordResult Verify(string attemptKey, ReadOnlySpan<char> candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptKey);

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_attempts.TryGetValue(attemptKey, out var state) && state.LockedUntil > now)
                return RoomPasswordResult.LockedOut;
        }

        // 길이가 초과해도 해시 비교를 거쳐 응답 시간 차이를 줄인다.
        var candidateHash = Derive(candidate.Length <= MaxPasswordLength ? candidate : candidate[..MaxPasswordLength], _salt);
        var accepted = candidate.Length <= MaxPasswordLength &&
                       CryptographicOperations.FixedTimeEquals(candidateHash, _hash);

        lock (_gate)
        {
            if (accepted)
            {
                _attempts.Remove(attemptKey);
                return RoomPasswordResult.Accepted;
            }

            if (!_attempts.TryGetValue(attemptKey, out var state))
            {
                TrimAttemptsLocked(now);
                state = new AttemptState();
                _attempts[attemptKey] = state;
            }

            state.Failures++;
            if (state.Failures >= MaxFailedAttempts)
            {
                state.Failures = 0;
                state.LockedUntil = now + LockoutDuration;
            }
            return RoomPasswordResult.Rejected;
        }
    }

    private void TrimAttemptsLocked(DateTimeOffset now)
    {
        if (_attempts.Count < MaxTrackedAttemptKeys)
            return;
        foreach (var key in _attempts.Where(pair => pair.Value.LockedUntil <= now).Select(pair => pair.Key).ToArray())
            _attempts.Remove(key);
    }

    private static byte[] Derive(ReadOnlySpan<char> password, byte[] salt)
    {
        var buffer = new byte[Encoding.UTF8.GetByteCount(password)];
        try
        {
            Encoding.UTF8.GetBytes(password, buffer);
            return Rfc2898DeriveBytes.Pbkdf2(buffer, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private sealed class AttemptState
    {
        public int Failures { get; set; }
        public DateTimeOffset LockedUntil { get; set; } = DateTimeOffset.MinValue;
    }
}
