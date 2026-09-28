using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace EduStream.Core.Network;

/// <summary>
/// 교수자 TLS 인증서 지문에서 만든 접속 코드입니다. 교수자 화면에 표시하고 학생이 참가할 때 입력해
/// 연결한 상대가 그 교수자 PC인지 확인합니다. 비밀값이 아니라 인증서 식별자이므로 화면에 보여도 됩니다.
/// </summary>
/// <remarks>
/// 12자(60비트)는 같은 코드를 갖는 가짜 인증서를 미리 만들어 두는 공격을 교실 사용 기간 안에 비현실적으로 만들기 위한 길이입니다.
/// 8자(40비트)는 GPU로 수 분 안에 맞출 수 있어 사용하지 않습니다.
/// </remarks>
public static class ConnectionCode
{
    public const int Length = 12;
    private const int GroupSize = 4;
    // Crockford Base32: 헷갈리는 I, L, O, U를 뺀 문자 집합.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>표시용 형식(XXXX-XXXX-XXXX)으로 만듭니다.</summary>
    public static string FromCertificate(X509Certificate2 certificate)
    {
        var raw = Compute(certificate);
        var builder = new StringBuilder(Length + Length / GroupSize - 1);
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % GroupSize == 0) builder.Append('-');
            builder.Append(raw[i]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 사용자 입력을 비교용 형식으로 바꿉니다. 대소문자·공백·하이픈을 무시하고 O→0, I/L→1로 읽습니다.
    /// 형식이 맞지 않으면 false입니다.
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var builder = new StringBuilder(Length);
        foreach (var c in input.ToUpperInvariant())
        {
            if (c is '-' or ' ') continue;
            var mapped = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (Alphabet.IndexOf(mapped) < 0 || builder.Length == Length) return false;
            builder.Append(mapped);
        }
        if (builder.Length != Length) return false;
        normalized = builder.ToString();
        return true;
    }

    /// <summary>입력한 코드가 인증서와 맞는지 고정 시간으로 비교합니다. 형식 오류도 false입니다.</summary>
    public static bool Matches(string? input, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!TryNormalize(input, out var normalized)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(normalized), Encoding.ASCII.GetBytes(Compute(certificate)));
    }

    private static string Compute(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var hash = SHA256.HashData(certificate.RawData);
        ulong bits = 0;
        for (var i = 0; i < 8; i++) bits = (bits << 8) | hash[i];
        var chars = new char[Length];
        // 상위 60비트를 5비트씩 끊어 사용한다.
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[(int)((bits >> (64 - 5 * (i + 1))) & 0x1F)];
        return new string(chars);
    }
}
