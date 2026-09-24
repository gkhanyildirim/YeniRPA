using ClosedXML.Excel;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// <see cref="TitleCleanWorkbook.BuildFromOriginal"/> — the Excel download edits the uploaded
/// workbook's own bytes rather than building a new one, so a dropdown, a second sheet or a cell
/// nobody asked to change has to survive a round trip untouched.
/// </summary>
public class TitleCleanWorkbookTests
{
    /// <summary>A two-sheet workbook with a data validation dropdown on "Marka" — the shape a real
    /// marketplace export carries, cut down to the minimum that still exercises the risk.</summary>
    static byte[] SampleWorkbook()
    {
        using var workbook = new XLWorkbook();
        var data = workbook.AddWorksheet("Data");

        data.Cell(1, 1).SetValue("Başlık");
        data.Cell(1, 2).SetValue("Marka");
        data.Cell(2, 1).SetValue("Lenovo Thinkpad E16 Notebook");
        data.Cell(2, 2).SetValue("Lenovo");

        data.Range("B2:B100").CreateDataValidation().List("Lenovo,Acer");

        var reference = workbook.AddWorksheet("ReferenceData");
        reference.Cell(1, 1).SetValue("untouched");

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
    }

    static CompiledRuleSet Rules() => CompiledRuleSet.Compile(
        new TitleRuleSet("Test", "Başlık", [new TitleAttributeRule("Marka")]));

    [Fact]
    public void EveryOtherSheetSurvivesUntouched()
    {
        var original = SampleWorkbook();
        var table = TabularFile.Read(new MemoryStream(original), "sample.xlsx");
        var rules = Rules();
        var rows = TitleCleanBuilder.Clean(rules, table);

        var result = TitleCleanWorkbook.BuildFromOriginal(new MemoryStream(original), table, rules, rows);

        using var workbook = new XLWorkbook(new MemoryStream(result));

        Assert.Equal(2, workbook.Worksheets.Count);

        var reference = workbook.Worksheet("ReferenceData");
        Assert.Equal("untouched", reference.Cell(1, 1).GetString());
    }

    [Fact]
    public void TheDataValidationDropdownSurvives()
    {
        var original = SampleWorkbook();
        var table = TabularFile.Read(new MemoryStream(original), "sample.xlsx");
        var rules = Rules();
        var rows = TitleCleanBuilder.Clean(rules, table);

        var result = TitleCleanWorkbook.BuildFromOriginal(new MemoryStream(original), table, rules, rows);

        using var workbook = new XLWorkbook(new MemoryStream(result));
        var sheet = workbook.Worksheet("Data");

        Assert.True(sheet.DataValidations.Any());
    }

    /// <summary>Only the title cell a rule actually changed is rewritten; the attribute cell "Marka"
    /// holds a Text-kind value that can never be corrected, so it must come back exactly as it was —
    /// still under the validation nobody touched.</summary>
    [Fact]
    public void OnlyTheChangedTitleCellIsRewritten()
    {
        var original = SampleWorkbook();
        var table = TabularFile.Read(new MemoryStream(original), "sample.xlsx");
        var rules = Rules();
        var rows = TitleCleanBuilder.Clean(rules, table);

        var result = TitleCleanWorkbook.BuildFromOriginal(new MemoryStream(original), table, rules, rows);

        using var workbook = new XLWorkbook(new MemoryStream(result));
        var sheet = workbook.Worksheet("Data");

        // "Lenovo" removed by the Marka rule, and the model casing fixed on the way — the same
        // engine result TitleCleanBuilder.Clean already produced for `rows`.
        Assert.Equal("ThinkPad E16 Notebook", sheet.Cell(2, 1).GetString());
        Assert.Equal("Lenovo", sheet.Cell(2, 2).GetString());

        // The header row is never touched by BuildFromOriginal.
        Assert.Equal("Başlık", sheet.Cell(1, 1).GetString());
        Assert.Equal("Marka", sheet.Cell(1, 2).GetString());
    }
}
