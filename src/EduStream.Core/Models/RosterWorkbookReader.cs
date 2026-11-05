using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace EduStream.Core.Models;

public sealed record RosterRow(int SourceRow, string Name, string StudentId, string Department, string Issue)
{
    public bool IsValid => Issue.Length == 0;
    public string Summary => $"{Name} · {(StudentId.Length == 0 ? "학번 미입력" : StudentId)}";
}
public sealed record RosterImportPreview(IReadOnlyList<RosterRow> Rows)
{
    public bool CanUseForAdmission => Rows.Count > 0 && Rows.All(x => x.IsValid);
    public string Summary => $"학생 {Rows.Count}명 · 확인 필요 {Rows.Count(x => !x.IsValid)}행";
}

/// <summary>학생명단 시트만 읽는 제한된 XLSX 가져오기. 매크로·수식·외부 참조를 실행하지 않는다.</summary>
public sealed class RosterWorkbookReader
{
    private static readonly XNamespace SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    public RosterImportPreview Read(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(".xlsx 파일을 선택해 주세요. .xls·매크로 파일은 지원하지 않습니다.");
        using var stream = File.OpenRead(path);
        return Read(stream);
    }
    public RosterImportPreview Read(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 500 || archive.Entries.Sum(x => x.Length) > 20 * 1024 * 1024)
            throw new InvalidDataException("명단 파일이 너무 큽니다. 학생명단만 담은 파일로 저장해 주세요.");
        XDocument Xml(string entryPath)
        {
            var entry = archive.GetEntry(entryPath) ?? throw new InvalidDataException($"엑셀 구성 파일이 없습니다: {entryPath}");
            using var source = entry.Open();
            using var reader = XmlReader.Create(source, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        var workbook = Xml("xl/workbook.xml");
        var sheet = workbook.Descendants(SheetNs + "sheet").SingleOrDefault(x => (string?)x.Attribute("name") == "학생명단")
            ?? throw new InvalidDataException("'학생명단' 시트가 필요합니다. 제공원문·교수자 시트는 가져오지 않습니다.");
        var relationship = Xml("xl/_rels/workbook.xml.rels").Descendants(PackageNs + "Relationship")
            .SingleOrDefault(x => (string?)x.Attribute("Id") == (string?)sheet.Attribute(RelNs + "id"));
        var target = (string?)relationship?.Attribute("Target");
        if (target is null || target.Contains("..") || target.Contains('\\') || target.Contains(':') ||
            (string?)relationship?.Attribute("TargetMode") == "External")
            throw new InvalidDataException("외부 또는 잘못된 시트 경로입니다.");
        var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        if (!sheetPath.StartsWith("xl/", StringComparison.Ordinal)) throw new InvalidDataException("잘못된 시트 경로입니다.");
        var strings = archive.GetEntry("xl/sharedStrings.xml") is null ? [] : Xml("xl/sharedStrings.xml")
            .Descendants(SheetNs + "si").Select(x => string.Concat(x.Descendants(SheetNs + "t").Select(t => t.Value))).ToArray();
        string Text(XElement cell)
        {
            if (cell.Element(SheetNs + "f") is not null) return "[수식 입력]";
            var value = cell.Element(SheetNs + "v")?.Value ?? "";
            return (string?)cell.Attribute("t") switch
            {
                "s" => int.TryParse(value, out var i) && i >= 0 && i < strings.Length ? strings[i].Trim() : throw new InvalidDataException("문자열 참조가 잘못되었습니다."),
                "inlineStr" => string.Concat(cell.Descendants(SheetNs + "t").Select(x => x.Value)).Trim(),
                _ => value.Trim()
            };
        }
        var rows = Xml(sheetPath).Descendants(SheetNs + "row").ToArray();
        if (rows.Length > 5040) throw new InvalidDataException("한 파일에 최대 5,000명의 명단을 넣어 주세요.");
        static string Column(XElement cell) => new(((string?)cell.Attribute("r") ?? "").TakeWhile(char.IsLetter).ToArray());
        var header = rows.Take(40).FirstOrDefault(r => r.Elements(SheetNs + "c").Any(c => Text(c) == "이름") &&
                                                     r.Elements(SheetNs + "c").Any(c => Text(c) == "학번"))
            ?? throw new InvalidDataException("40행 이내에 '이름'과 '학번' 열 제목이 필요합니다.");
        var headers = header.Elements(SheetNs + "c").ToArray();
        string HeaderColumn(string title) => Column(headers.Single(x => Text(x) == title));
        var nameCol = HeaderColumn("이름"); var idCol = HeaderColumn("학번");
        var departmentCol = headers.FirstOrDefault(x => Text(x) == "학과") is { } department ? Column(department) : null;
        var roleCol = headers.FirstOrDefault(x => Text(x) == "역할") is { } role ? Column(role) : null;
        var result = new List<RosterRow>();
        foreach (var row in rows.SkipWhile(x => !ReferenceEquals(x, header)).Skip(1))
        {
            var cells = row.Elements(SheetNs + "c").ToDictionary(Column, Text);
            var name = cells.GetValueOrDefault(nameCol, ""); var id = cells.GetValueOrDefault(idCol, "");
            if (name.Length == 0 && id.Length == 0) continue;
            var sourceRow = (int?)row.Attribute("r") ?? 0;
            var issues = new List<string>();
            if (name.Length == 0 || name.Length > 80 || name.Any(char.IsControl) || name == "[수식 입력]") issues.Add("이름 확인 필요");
            if (RosterIdentity.ConnectionName(name, id).Length > 80) issues.Add("이름·학번 표시 길이는 80자 이내여야 함");
            if (id.Length == 0) issues.Add("실제 학번 입력 필요");
            else if (id.Length > 32 || !id.All(char.IsAsciiDigit)) issues.Add("학번은 마스킹·수식 없는 숫자 텍스트로 입력");
            if (roleCol is not null && cells.TryGetValue(roleCol, out var roleText) && roleText.Length != 0 && roleText != "학생")
                issues.Add("학생 이외 역할은 입장 명단에서 제외 필요");
            result.Add(new(sourceRow, name, id, departmentCol is null ? "" : cells.GetValueOrDefault(departmentCol, ""), string.Join(" / ", issues)));
        }
        if (result.Count == 0) throw new InvalidDataException("가져올 학생이 없습니다.");
        if (result.Count > 5000) throw new InvalidDataException("한 파일에 최대 5,000명의 명단을 넣어 주세요.");
        var duplicates = result.Where(x => x.StudentId.Length > 0).GroupBy(x => x.StudentId)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        return new(result.Select(x => duplicates.Contains(x.StudentId)
            ? x with { Issue = x.Issue.Length == 0 ? "중복 학번" : x.Issue + " / 중복 학번" } : x).ToArray());
    }
}
