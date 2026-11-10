using System.Text;

namespace EduStream.Core.Common;

public static class StudentInputRules
{
    public const int NameMaxLength = 20;
    public static string StudentId(string? text) => new((text ?? "").Where(c => c is >= '0' and <= '9').Take(32).ToArray());
    // 유학생·긴 이름·띄어 쓴 이름도 명단과 똑같이 입력할 수 있게 한글·영문·공백·이름 구분 기호(-.·)를 받는다.
    // 명단과 다르면 대조에서 거절되므로 여기서는 이모지·제어 문자 같은 표시 문제만 막는다.
    // MaxLength/붙여넣기로 이모지의 surrogate가 잘린 경우도 조합 정규화 예외를 내지 않는다.
    public static string Name(string? text)
    {
        var composed = new string((text ?? "").Where(c => !char.IsSurrogate(c)).ToArray()).Normalize(NormalizationForm.FormC);
        var result = new StringBuilder();
        foreach (var c in composed)
        {
            if (result.Length == NameMaxLength) break;
            var allowed = c is >= '가' and <= '힣' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '-' or '.' or '·';
            if (c == ' ' && result.Length > 0 && result[^1] != ' ') allowed = true; // 앞 공백·연속 공백은 버린다
            if (allowed) result.Append(c);
        }
        return result.ToString();
    }
}
