using ClosedXML.Excel;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The streaming .xlsx reader Seller Offer Warnings runs on. It exists because the offer export is two
/// orders of magnitude larger than anything else this app reads, and the thing that has to be proved
/// about it is that streaming did not change what the cells say.
/// </summary>
public class OfferExportReaderTests
{
    static MemoryStream Workbook(Action<IXLWorksheet> fill, string sheetName = "Sheet1")
    {
        using var workbook = new XLWorkbook();
        fill(workbook.AddWorksheet(sheetName));

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    static List<List<string>> Read(Stream stream, string? sheetName = null) =>
        [.. OfferExportReader.Read(stream, "offers.xlsx", sheetName)];

    [Fact]
    public void TheHeaderIsTheFirstRowAndTheCellsComeBackAsWritten()
    {
        using var stream = Workbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "Seller";
            sheet.Cell(1, 2).Value = "Product SKU";
            sheet.Cell(1, 3).Value = "Lead time to ship";
            sheet.Cell(2, 1).Value = "Prodesk";
            sheet.Cell(2, 2).Value = "SKU-A";
            sheet.Cell(2, 3).Value = 1;
        });

        var rows = Read(stream);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["Seller", "Product SKU", "Lead time to ship"], rows[0]);
        Assert.Equal(["Prodesk", "SKU-A", "1"], rows[1]);
    }

    /// <summary>
    /// <b>The reason this reader indexes on the cell reference.</b> Excel writes no element at all for
    /// an empty cell, so a row that skips one arrives short. Appending would shift every column after
    /// the gap by one — in the real export that reads the EAN column as the lead time.
    /// </summary>
    [Fact]
    public void AnEmptyCellInTheMiddleKeepsTheColumnsWhereTheyAre()
    {
        using var stream = Workbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "A";
            sheet.Cell(1, 2).Value = "B";
            sheet.Cell(1, 3).Value = "C";
            sheet.Cell(2, 1).Value = "left";
            // Column 2 deliberately never touched.
            sheet.Cell(2, 3).Value = "right";
        });

        var rows = Read(stream);

        Assert.Equal("left", rows[1][0]);
        Assert.Equal("", rows[1][1]);
        Assert.Equal("right", rows[1][2]);
    }

    /// <summary>Seller ids and SKUs are read as text, never reformatted — a number that picked up a
    /// thousands separator on the way through would stop matching the address list.</summary>
    [Fact]
    public void ANumericCellKeepsItsStoredDigits()
    {
        using var stream = Workbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "Seller ID";
            sheet.Cell(2, 1).Value = 11835;
        });

        Assert.Equal("11835", Read(stream)[1][0]);
    }

    /// <summary>The onboarding-style case: a workbook whose useful sheet is not the first one.</summary>
    [Fact]
    public void ANamedSheetIsReadInsteadOfTheFirst()
    {
        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("Summary").Cell(1, 1).Value = "not this one";
        workbook.AddWorksheet("Data").Cell(1, 1).Value = "this one";

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        Assert.Equal("this one", Read(stream, "Data")[0][0]);
    }

    /// <summary>
    /// A name that matches nothing says so and lists what the workbook holds. Quietly reading sheet one
    /// would turn "you uploaded last month's file" into "the Lead time column was not found", which
    /// sends the operator looking at the wrong problem.
    /// </summary>
    [Fact]
    public void AMissingSheetIsRefusedAndTheRealNamesAreListed()
    {
        using var stream = Workbook(sheet => sheet.Cell(1, 1).Value = "x", "Offers");

        var error = Assert.Throws<InvalidOperationException>(() => Read(stream, "Data"));

        Assert.Contains("'Data'", error.Message);
        Assert.Contains("'Offers'", error.Message);
    }

    /// <summary>CSV has exactly one table and no size problem, so it goes through the ordinary reader —
    /// but it has to come back in the same shape or the builder would need two code paths.</summary>
    [Fact]
    public void ACsvComesBackInTheSameShape()
    {
        using var stream = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes("Seller;Product SKU\nProdesk;SKU-A\n"));

        var rows = OfferExportReader.Read(stream, "offers.csv").ToList();

        Assert.Equal(["Seller", "Product SKU"], rows[0]);
        Assert.Equal(["Prodesk", "SKU-A"], rows[1]);
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("B2", 1)]
    [InlineData("Z9", 25)]
    [InlineData("AA1", 26)]
    [InlineData("AB203544", 27)]
    [InlineData("", -1)]
    [InlineData("1", -1)]
    public void AColumnReferenceBecomesAZeroBasedIndex(string reference, int expected)
    {
        Assert.Equal(expected, OfferExportReader.ColumnIndex(reference));
    }
}
