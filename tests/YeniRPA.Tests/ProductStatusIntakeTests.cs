using YeniRPA.Web.Models;

namespace YeniRPA.Tests;

/// <summary>
/// What the submitted list loses on its way to becoming a list of sellers.
///
/// <para>A spreadsheet of 765 rows can honestly produce a table of 472, because five filters run
/// before the browser ever opens — a skipped header, blank rows, '#' rows, and de-duplication. None
/// of them used to be counted, so the drop was indistinguishable from the scrape losing sellers.
/// These tests pin the arithmetic that makes the difference reportable.</para>
/// </summary>
public class ProductStatusIntakeTests
{
    static (IReadOnlyList<string> Sellers, ProductStatusIntake Intake) FromFile(params string[] rows) =>
        ProductStatusIntake.ParseLines(rows, null);

    [Fact]
    public void RepeatedNamesAreCountedAndRunOnce()
    {
        // The likeliest cause of a big drop: a per-order export repeats its seller column.
        var (sellers, intake) = FromFile("Seller", "Acme", "Acme", "Beta", "Acme");

        Assert.Equal(["Acme", "Beta"], sellers);
        Assert.Equal(2, intake.Duplicates);
        Assert.Equal(2, intake.Sellers);
    }

    [Fact]
    public void BlankAndWhitespaceOnlyRowsAreCountedAsBlank()
    {
        var (sellers, intake) = FromFile("Seller", "Acme", "", "   ", "Beta");

        Assert.Equal(["Acme", "Beta"], sellers);
        Assert.Equal(2, intake.Blank);
    }

    [Fact]
    public void ASpreadsheetErrorCellIsDroppedAsThoughItWereAComment()
    {
        // The '#' rule exists for comments, but a column of broken VLOOKUPs arrives as "#N/A" and is
        // taken the same way. Counting it is what lets the operator recognise it.
        var (sellers, intake) = FromFile("Seller", "Acme", "#N/A", "#REF!", "# a real comment");

        Assert.Equal(["Acme"], sellers);
        Assert.Equal(3, intake.Comments);
    }

    [Fact]
    public void TheSkippedHeaderIsReportedSoAHeaderlessFileCanBeSpotted()
    {
        // The skip is unconditional, so a file that starts straight into data loses a real seller.
        // Nothing detects that; quoting the row back is what makes it visible.
        var (sellers, intake) = FromFile("Acme", "Beta");

        Assert.Equal(1, intake.HeaderRows);
        Assert.Equal("Acme", intake.HeaderText);
        Assert.Equal(["Beta"], sellers);
    }

    [Fact]
    public void CaseDifferencesAreSeparateSellers()
    {
        // Ordinal de-duplication, deliberately: "ACME" and "Acme" may be two sellers on Mirakl.
        var (sellers, intake) = FromFile("Seller", "Acme", "ACME", "acme");

        Assert.Equal(3, intake.Sellers);
        Assert.Equal(0, intake.Duplicates);
    }

    [Fact]
    public void AFileAndPastedTextAreBothCountedAndDeDuplicatedAcross()
    {
        var (sellers, intake) = ProductStatusIntake.ParseLines(["Seller", "Acme"], "Beta\nAcme");

        Assert.Equal(["Acme", "Beta"], sellers);
        Assert.Equal(2, intake.FileRows);
        Assert.Equal(2, intake.PastedLines);
        Assert.Equal(1, intake.Duplicates);
    }

    [Fact]
    public void PastedTextAloneReportsNoFileAndNoHeader()
    {
        // Nothing is skipped off the top of a textarea — the first line is a seller.
        var (sellers, intake) = ProductStatusIntake.ParseLines(null, "Acme\nBeta");

        Assert.Equal(["Acme", "Beta"], sellers);
        Assert.Equal(0, intake.FileRows);
        Assert.Equal(0, intake.HeaderRows);
        Assert.Null(intake.HeaderText);
    }

    [Fact]
    public void EverySubmittedLineEndsInExactlyOneBucket()
    {
        // The invariant the whole report rests on: nothing is dropped without being counted.
        var rows = new List<string> { "Seller" };
        rows.AddRange(Enumerable.Range(0, 400).Select(i => $"Seller {i}"));
        rows.AddRange(Enumerable.Range(0, 300).Select(i => $"Seller {i % 100}"));   // repeats
        rows.AddRange(Enumerable.Repeat("", 40));
        rows.AddRange(Enumerable.Repeat("#N/A", 25));

        var (sellers, intake) = ProductStatusIntake.ParseLines(rows, "Extra\n\nSeller 1");

        Assert.Equal(
            intake.FileRows - intake.HeaderRows + intake.PastedLines,
            intake.Blank + intake.Comments + intake.Duplicates + intake.Sellers);
        Assert.Equal(intake.Sellers, sellers.Count);
    }

    [Fact]
    public void TheReportedCaseAddsUp()
    {
        // 765 data rows under a header, 293 of them repeats: 472 sellers, and every figure named.
        var rows = new List<string> { "Satıcı Adı" };
        rows.AddRange(Enumerable.Range(0, 472).Select(i => $"Seller {i}"));
        rows.AddRange(Enumerable.Range(0, 293).Select(i => $"Seller {i}"));

        var (sellers, intake) = ProductStatusIntake.ParseLines(rows, null);

        Assert.Equal(766, intake.FileRows);
        Assert.Equal("Satıcı Adı", intake.HeaderText);
        Assert.Equal(293, intake.Duplicates);
        Assert.Equal(472, intake.Sellers);
        Assert.Equal(472, sellers.Count);
    }

    [Fact]
    public void AnEmptySubmissionIsAnAccountOfNothingRatherThanAThrow()
    {
        var (sellers, intake) = ProductStatusIntake.ParseLines(null, null);

        Assert.Empty(sellers);
        Assert.Equal(0, intake.Sellers);
        Assert.Equal(0, intake.FileRows);
    }
}
