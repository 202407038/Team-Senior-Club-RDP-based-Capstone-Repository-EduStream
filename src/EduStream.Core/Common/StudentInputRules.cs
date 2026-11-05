using System.Text;

namespace EduStream.Core.Common;

public static class StudentInputRules
{
    public static string StudentId(string? text) => new((text ?? "").Where(c => c is >= '0' and <= '9').Take(32).ToArray());
    // MaxLength/붙여넣기로 이모지의 surrogate가 잘린 경우도 조합 정규화 예외를 내지 않는다.
    public static string Name(string? text) => new(new string((text ?? "").Where(c => !char.IsSurrogate(c)).ToArray()).Normalize(NormalizationForm.FormC)
        .Where(c => c is >= '\uAC00' and <= '\uD7A3').Take(5).ToArray());
}
