using ClosedXML.Excel;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// The workbook a run produces. It is one sheet with the uploaded file's own layout, and it has to
/// be re-uploadable — to the marketplace, and back into this app.
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

    /// <summary>
    /// One sheet, nothing else.
    ///
    /// <para>The category team uploads this file to the marketplace. An <em>Orijinal</em> and a
    /// <em>Kural Seti</em> sheet beside it were two things they had to delete first, every time.</para>
    /// </summary>
    [Fact]
    public void TheWorkbookCarriesTheCleanedSheetAndNothingElse()
    {
        using var workbook = Run(out _);

        Assert.Equal(["Temizlenmiş"], workbook.Worksheets.Select(s => s.Name).ToArray());
    }

    /// <summary>
    /// The uploaded layout is reproduced exactly — same columns, same order, nothing appended.
    ///
    /// <para>This used to append an original-title column, a status, an error list and a verdict
    /// column per rule. On a 299-column export that is twelve columns the operator has to find and
    /// strip before the file can go anywhere.</para>
    /// </summary>
    [Fact]
    public void TheCleanedSheetReproducesTheUploadedColumnsAndAddsNone()
    {
        using var workbook = Run(out var table);
        var sheet = workbook.Worksheet("Temizlenmiş");

        Assert.Equal("Başlık", sheet.Cell(1, 1).GetString());
        Assert.Equal("Marka", sheet.Cell(1, 2).GetString());
        Assert.Equal("RAM", sheet.Cell(1, 3).GetString());
        Assert.Equal("Ekran Kartı", sheet.Cell(1, 4).GetString());

        Assert.Equal(table[0].Count, sheet.LastColumnUsed()!.ColumnNumber());
        Assert.Equal("", sheet.Cell(1, table[0].Count + 1).GetString());
    }

    /// <summary>
    /// The cleaned title goes into the title column, not beside it.
    ///
    /// <para>The marketplace reads that column. Leaving the old title in it — which this used to do,
    /// putting the clean one in an appended column instead — meant the sheet uploaded every title
    /// unchanged while correcting the attributes around it.</para>
    /// </summary>
    [Fact]
    public void TheTitleColumnHoldsTheCleanedTitle()
    {
        using var workbook = Run(out _);
        var sheet = workbook.Worksheet("Temizlenmiş");

        Assert.Equal("Pro Max 16 MC16250_3 RTXPRO2000", sheet.Cell(2, 1).GetString());
    }

    /// <summary>The corrected value replaces the original in its own column — that is what makes the
    /// sheet re-uploadable rather than a report about the file.</summary>
    [Fact]
    public void ACorrectedValueIsWrittenBackIntoItsOwnColumn()
    {
        using var workbook = Run(out _);
        var sheet = workbook.Worksheet("Temizlenmiş");

        Assert.Equal("32 GB", sheet.Cell(2, 3).GetString());
        Assert.Equal("Pro Max 16 MC16250_3 RTXPRO2000", sheet.Cell(2, 1).GetString());
    }

    /// <summary>
    /// A disagreement leaves both the cell and the title alone.
    ///
    /// <para>The row is reported on screen instead. Nothing about it is written into this file, which
    /// is the point: what goes to the marketplace is data, not verdicts.</para>
    /// </summary>
    [Fact]
    public void ADisagreementLeavesTheCellAndTheTitleUntouched()
    {
        using var workbook = Run(out _);
        var sheet = workbook.Worksheet("Temizlenmiş");

        Assert.Equal("64", sheet.Cell(3, 3).GetString());

        // The disagreeing value stays in the title — the brand on this row was cleaned as usual, so
        // one attribute disagreeing does not stop the rest of the row.
        Assert.Equal("ProBook 450 16GB", sheet.Cell(3, 1).GetString());
    }

    /// <summary>
    /// Running the cleaner over its own output changes nothing further.
    ///
    /// <para>This is the cheapest check that catches the failure that would matter most: a second
    /// pass that kept eating characters would corrupt a catalogue one re-run at a time, and every
    /// individual run would look like it had worked. It is also what the single-sheet layout buys —
    /// the output reads back with no columns to strip first.</para>
    /// </summary>
    [Fact]
    public void ReUploadingTheOutputChangesNothingFurther()
    {
        using var workbook = Run(out var table);
        var sheet = workbook.Worksheet("Temizlenmiş");

        var second = sheet.RowsUsed()
            .Select(row => Enumerable.Range(1, table[0].Count).Select(c => row.Cell(c).GetString()).ToList())
            .ToList();

        var rows = TitleCleanBuilder.Clean(CompiledRuleSet.Compile(Rules()), second);

        Assert.Equal("Pro Max 16 MC16250_3 RTXPRO2000", rows[0].CleanTitle);
        Assert.False(rows[0].Changed);
    }
}
