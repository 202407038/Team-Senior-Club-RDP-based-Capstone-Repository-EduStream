using EduStream.Core.Models;

namespace EduStream.Server.Services;

/// <summary>엑셀과 입력값의 일치 대조이며 학교 본인 인증이 아니다.</summary>
public sealed class RosterAdmissionPolicy
{
    private IReadOnlyDictionary<string, RosterRow> _rows = new Dictionary<string, RosterRow>();
    public bool IsEnabled { get; private set; }
    public int Count => _rows.Count;
    public IReadOnlyList<RosterRow> ActiveRows => IsEnabled ? _rows.Values.ToArray() : [];
    public void Configure(IReadOnlyList<RosterRow> rows, bool enabled)
    {
        if (!enabled) { IsEnabled = false; return; }
        if (rows.Count == 0 || rows.Any(x => !x.IsValid || string.IsNullOrWhiteSpace(x.Name) || x.Name.Any(char.IsControl) ||
                RosterIdentity.ConnectionName(x.Name, x.StudentId).Length > 80 ||
                x.StudentId.Length == 0 || x.StudentId.Length > 32 || !x.StudentId.All(char.IsAsciiDigit)) ||
            rows.Select(x => x.StudentId).Distinct(StringComparer.Ordinal).Count() != rows.Count)
            throw new InvalidOperationException("모든 학생의 실제 학번을 입력하고 중복·오류를 수정한 명단이 필요합니다.");
        var next = rows.ToDictionary(x => x.StudentId, StringComparer.Ordinal);
        _rows = next; IsEnabled = true;
    }
    public bool Allows(SessionJoinPacket packet) => !IsEnabled ||
        (!string.IsNullOrEmpty(packet.StudentId) && _rows.TryGetValue(packet.StudentId, out var student) && student.Name == packet.RosterName &&
         packet.DisplayName == RosterIdentity.ConnectionName(student.Name, student.StudentId));
}
