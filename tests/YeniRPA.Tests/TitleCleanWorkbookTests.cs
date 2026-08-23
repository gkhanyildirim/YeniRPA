using ClosedXML.Excel;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// The workbook a run produces. Sheet 1 has to be re-uploadable — to the marketplace, and back into
/// this app — and sheet 2 has to be the only thing standing between a wrong rule set and a lost
/// catalogue.
/// </summary>
public class TitleCleanWorkbookTests
{
    static readonly MeasureUnit Gb = new("GB", ["gb"], 1);
    static readonly MeasureUnit Tb = new("TB", ["tb"], 1024);

    static TitleRuleSet Rules() => new(
        "Laptop",
        "Başlık",
        [
            new TitleAttributeRule("Marka"),
            new TitleAttributeRule("RAM", TitleAttributeKind.Measure, Units: [Gb, Tb]),
            new TitleAttributeRule("Ekran Kartı", Remove: false),
        ]);

    static List<List<string>> Table() =>
    [
        ["Başlık", "Marka", "RAM", "Ekran Kartı"],
        ["Dell Pro Max 16 MC16250_3 32GB RTXPRO2000", "Dell", "32", "RTXPRO2000"],
        ["HP ProBook 450 16GB", "HP", "64", "Iris Xe"],
    ];

    static XLWorkbook Run(out List<List<string>> table)
    {
        table = Table();
        var rules = CompiledRuleSet.Compile(Rules());
        var rows = TitleCleanBuilder.Clean(rules, table);

        return new XLWorkbook(new MemoryStream(TitleCleanWorkbook.Build(table, rules, rows)));
    }

    [Fact]
    public void TheWorkbookCarriesTheThreeSheetsARunHasToLeaveBehind()
    {
        using var workbook = Run(out _);

        Assert.Equal(
            ["Temizlenmiş", "Orijinal", "Kural Seti"],
            workbook.Worksheets.Select(s => s.Name).ToArray());
    }

    /// <summary>The uploaded layout is preserved and the added columns go on the end, so nothing an
    /// existing importer reads by position moves.</summary>
    [Fact]
    public void TheCleanedSheetKeepsTheOriginalColumnsAndAppendsTheNewOnes()
    {
        using var workbook = Run(out _);
        var sheet = workbook.Worksheet("Temizlenmiş");

        Assert.Equal("Başlık", sheet.Cell(1, 1).GetString());
        Assert.Equal("Marka", sheet.Cell(1, 2).GetString());
        Assert.Equal("RAM", sheet.Cell(1, 3).GetString());
        Assert.Equal("Ekran Kartı", sheet.Cell(1, 4).GetString());

        Assert.Equal(TitleCleanWorkbook.OriginalTitleHeader, sheet.Cell(1, 5).GetString());
        Assert.Equal(TitleCleanWorkbook.StatusHeader, sheet.Cell(1, 6).GetString());
        Assert.Equal(TitleCleanWorkbook.ErrorHeader, sheet.Cell(1, 7).GetString());
        Assert.Equal("Marka Durumu", sheet.Cell(1, 8).GetString());
        Assert.Equal("RAM Durumu", sheet.Cell(1, 9).GetString());
    }

    /// <summary>
    /// The cleaned title goes into the title column, not beside it.
    ///
    /// <para>The marketplace reads that column. Leaving the old title in it — which this used to do,
    /// putting the clean one in an appended column instead — meant the sheet uploaded every title
    /// unchanged while correcting the attributes around it, and on a 298-column export nobody could
    /// see the difference because the appended column was past everything they scroll to.</para>
    /// </summary>
    [Fact]
    public void TheTitleColumnHoldsTheCleanedTitleAndTheOldOneIsAppended()
    {
        using var workbook = Run(out _);
        var sheet = workbook.Worksheet("Temizlenmiş");

        Assert.Equal("Pro Max 16 MC16250_3 RTXPRO2000", sheet.Cell(2, 1).GetString());
        Assert.Equal(
            "Dell Pro Max 16 MC16250_3 32GB RTXPRO2000", sheet.Cell(2, 5).GetString());
    }

    /// <summary>The corrected value replaces the original in its own column — that is what makes the
    /// sheet re-uploadable rather than a report about the file.</summary>
    [Fact]
    public void ACorrectedValueIsWrittenBackIntoItsOwnColumn()
    {
        using var workbook = Run(out _);
        var sheet = workbook.Worksheet("Temizlenmiş");

        Assert.Equal("32 GB", sheet.Cell(2, 3).GetString());
        Assert.Equal("DÜZELTİLDİ", sheet.Cell(2, 9).GetString());
        Assert.Equal("Pro Max 16 MC16250_3 RTXPRO2000", sheet.Cell(2, 1).GetString());
    }

    /// <summary>A disagreement leaves both the cell and the title alone and says so on the row.</summary>
    [Fact]
    public void ADisagreementIsReportedOnTheRowRatherThanActedOn()
    {
        using var workbook = Run(out _);
        var sheet = workbook.Worksheet("Temizlenmiş");

        Assert.Equal("64", sheet.Cell(3, 3).GetString());
        Assert.Equal("ÇAKIŞMA", sheet.Cell(3, 9).GetString());
        Assert.Equal("İncelenecek", sheet.Cell(3, 6).GetString());
        Assert.Contains("16 GB", sheet.Cell(3, 7).GetString(), StringComparison.Ordinal);

        // The disagreeing value stays in the title — the brand on this row was cleaned as usual, so
        // one attribute disagreeing does not stop the rest of the row.
        Assert.Equal("ProBook 450 16GB", sheet.Cell(3, 1).GetString());
        Assert.Equal("HP ProBook 450 16GB", sheet.Cell(3, 5).GetString());
    }

    /// <summary>
    /// The only record of what the titles used to say. A cleaner rewrites data that cannot be
    /// reconstructed from the result, so this sheet is what makes the whole thing safe to run.
    /// </summary>
    [Fact]
    public void TheOriginalSheetIsAVerbatimCopyOfWhatWentIn()
    {
        using var workbook = Run(out var table);
        var sheet = workbook.Worksheet("Orijinal");

        for (var r = 0; r < table.Count; r++)
        {
            for (var c = 0; c < table[r].Count; c++)
                Assert.Equal(table[r][c], sheet.Cell(r + 1, c + 1).GetString());
        }
    }

    [Fact]
    public void TheRuleSetSheetRecordsWhatRan()
    {
        using var workbook = Run(out _);
        var sheet = workbook.Worksheet("Kural Seti");

        Assert.Equal("Laptop", sheet.Cell(1, 2).GetString());
        Assert.Equal("Başlık", sheet.Cell(2, 2).GetString());

        // The GPU column is on the sheet, recorded as not removed — which is why RTXPRO2000 is still
        // in the cleaned title.
        var gpu = sheet.RowsUsed().First(r => r.Cell(1).GetString() == "Ekran Kartı");
        Assert.Equal("Hayır", gpu.Cell(3).GetString());
    }

    /// <summary>
    /// Running the cleaner over its own output changes nothing further.
    ///
    /// <para>This is the cheapest check that catches the failure that would matter most: a second
    /// pass that kept eating characters would corrupt a catalogue one re-run at a time, and every
    /// individual run would look like it had worked.</para>
    /// </summary>
    [Fact]
    public void ReUploadingTheOutputChangesNothingFurther()
    {
        using var workbook = Run(out _);

        // Read sheet 1 back exactly the way an upload would, minus the columns this app added. No
        // fixing up needed: the title column already holds the cleaned title, which is the whole
        // point of writing it there.
        var sheet = workbook.Worksheet("Temizlenmiş");

        var second = sheet.RowsUsed()
            .Select(row => Enumerable.Range(1, 4).Select(c => row.Cell(c).GetString()).ToList())
            .ToList();

        var rows = TitleCleanBuilder.Clean(CompiledRuleSet.Compile(Rules()), second);

        Assert.Equal("Pro Max 16 MC16250_3 RTXPRO2000", rows[0].CleanTitle);
        Assert.False(rows[0].Changed);
    }
}
