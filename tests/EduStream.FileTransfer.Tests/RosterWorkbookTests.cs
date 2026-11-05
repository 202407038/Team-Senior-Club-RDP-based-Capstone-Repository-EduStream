using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using EduStream.Core.Models;

namespace EduStream.FileTransfer.Tests;

public sealed class RosterWorkbookTests
{
    [Fact]
    public void ReadsOnlyStudentSheetPreservesTextIdsAndAllowsSameNameWithDistinctIds()
    {
        using var workbook = Workbook([("학생 A", "00123", "학생"), ("학생 A", "00456", "학생")]);
        var preview = new RosterWorkbookReader().Read(workbook);
        Assert.Equal(2, preview.Rows.Count);
        Assert.True(preview.CanUseForAdmission);
        Assert.Equal("00123", preview.Rows[0].StudentId);
        Assert.DoesNotContain(preview.Rows, x => x.Name == "교수자 자료");
    }

    [Fact]
    public void MissingMaskedDuplicateAndNonStudentRowsRemainVisibleButCannotAuthorizeAdmission()
    {
        using var workbook = Workbook([("학생 A", "", "학생"), ("학생 B", "2024*****", "학생"),
            ("학생 C", "20240001", "학생"), ("학생 D", "20240001", "학생"), ("교수자", "9999", "교수자")]);
        var preview = new RosterWorkbookReader().Read(workbook);
        Assert.Equal(5, preview.Rows.Count);
        Assert.All(preview.Rows, row => Assert.False(row.IsValid));
        Assert.False(preview.CanUseForAdmission);
        Assert.Contains("중복", preview.Rows[2].Issue);
        Assert.Contains("학생 이외", preview.Rows[4].Issue);
    }

    [Fact]
    public void MissingStudentSheetAndFormulaIdentityAreNotSilentlyAccepted()
    {
        using var missing = Workbook([("학생 A", "001", "학생")], "제공원문");
        Assert.Throws<InvalidDataException>(() => new RosterWorkbookReader().Read(missing));
        using var formula = Workbook([("학생 A", "001", "학생")], formulaId: true);
        Assert.False(new RosterWorkbookReader().Read(formula).CanUseForAdmission);
    }

    private static MemoryStream Workbook((string Name, string Id, string Role)[] rows, string sheetName = "학생명단", bool formulaId = false)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(text);
            }
            Add("xl/workbook.xml", $"<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='{sheetName}' sheetId='1' r:id='rId1'/><sheet name='다른자료' sheetId='2' r:id='rId2'/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Target='worksheets/sheet1.xml'/><Relationship Id='rId2' Target='worksheets/sheet2.xml'/></Relationships>");
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XElement Cell(string reference, string value) => new(ns + "c", new XAttribute("r", reference), new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", value)));
            var data = new XElement(ns + "sheetData", new XElement(ns + "row", new XAttribute("r", 1), Cell("A1", "이름"), Cell("B1", "학번"), Cell("C1", "역할")));
            for (int i = 0; i < rows.Length; i++)
            {
                int r = i + 2;
                var id = Cell($"B{r}", rows[i].Id);
                if (formulaId) id.Add(new XElement(ns + "f", "1+1"));
                data.Add(new XElement(ns + "row", new XAttribute("r", r), Cell($"A{r}", rows[i].Name), id, Cell($"C{r}", rows[i].Role)));
            }
            Add("xl/worksheets/sheet1.xml", new XElement(ns + "worksheet", data).ToString());
            Add("xl/worksheets/sheet2.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>교수자 자료</t></is></c></row></sheetData></worksheet>");
        }
        stream.Position = 0;
        return stream;
    }
}
