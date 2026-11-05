namespace EduStream.Server.Services;

public static class StudentNameSearch
{
    public static int FindNext(IList<string> names, string query, int previous)
    {
        if (string.IsNullOrWhiteSpace(query) || names.Count == 0) return -1;
        var start = previous >= 0 && previous < names.Count ? previous : -1;
        for (var offset = 1; offset <= names.Count; offset++)
        {
            var index = (start + offset) % names.Count;
            if (names[index].Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }
}
