using YeniRPA.Web.Models;

namespace YeniRPA.Tests;

/// <summary>
/// The one part of Product Status that does not need a browser: turning the scraped (seller, status,
/// count) triples into the single table the page and the export both read. Mirakl only reports the
/// statuses a seller actually has, so the widening done here is what makes two sellers comparable.
/// </summary>
public class ProductStatusPivotTests
{
    static ProductStatusResult Pivot(IReadOnlyList<string> sellers, params ProductStatusRow[] rows) =>
        ProductStatusResult.FromRows(sellers, rows, []);

    [Fact]
    public void RowsFollowTheSubmittedSellerOrderNotTheScrapeOrder()
    {
        // Sellers are read four at a time, so the order results come back in is arbitrary.
        var result = Pivot(
            ["Seller B", "Seller A", "Seller C"],
            new ProductStatusRow("Seller C", "Online", 3),
            new ProductStatusRow("Seller A", "Online", 1),
            new ProductStatusRow("Seller B", "Online", 2));

        Assert.Equal(["Seller B", "Seller A", "Seller C"], result.Rows.Select(r => r.SellerName));
    }

    [Fact]
    public void AStatusASellerDoesNotHaveReadsAsZero()
    {
        var result = Pivot(
            ["Seller A", "Seller B"],
            new ProductStatusRow("Seller A", "Online", 10),
            new ProductStatusRow("Seller A", "Taslak", 4),
            new ProductStatusRow("Seller B", "Online", 7));

        Assert.Equal(["Online", "Taslak"], result.Labels);
        Assert.Equal([10, 4], result.Rows[0].Counts);
        Assert.Equal([7, 0], result.Rows[1].Counts);
    }

    [Fact]
    public void ColumnsAreDeduplicatedAndKeepTheOrderTheyWereFirstSeenIn()
    {
        var result = Pivot(
            ["Seller A", "Seller B"],
            new ProductStatusRow("Seller A", "Online", 1),
            new ProductStatusRow("Seller A", "Reddedildi", 2),
            new ProductStatusRow("Seller B", "Reddedildi", 3),
            new ProductStatusRow("Seller B", "Taslak", 4));

        Assert.Equal(["Online", "Reddedildi", "Taslak"], result.Labels);
    }

    [Fact]
    public void ASellerNotAccountedForInAnyBucketIsLeftOut()
    {
        // A seller that is neither a scraped row nor named in withoutProducts (the 3-argument overload
        // passes none) cannot be told apart from a failed read, so it is left out rather than guessed
        // at as a zero.
        var result = Pivot(
            ["Seller A", "Empty Seller"],
            new ProductStatusRow("Seller A", "Online", 5));

        Assert.Equal(["Seller A"], result.Rows.Select(r => r.SellerName));
    }

    [Fact]
    public void ASellerWithoutProductsInMiraklGetsAZeroRow()
    {
        // No match in Mirakl's provider filter and a real seller with an empty catalogue are the same
        // thing to this module, so a seller named in withoutProducts still gets a row — all zeroes —
        // instead of vanishing from the table and the Excel export taken from it.
        var result = ProductStatusResult.FromRows(
            ["Seller A", "Empty Seller"],
            [new ProductStatusRow("Seller A", "Online", 5), new ProductStatusRow("Seller A", "Taslak", 2)],
            [],
            ["Empty Seller"]);

        Assert.Equal(["Seller A", "Empty Seller"], result.Rows.Select(r => r.SellerName));
        Assert.Equal([0, 0], result.Rows.Single(r => r.SellerName == "Empty Seller").Counts);
    }

    [Fact]
    public void FailedSellersAreCarriedThroughForTheOperatorToSee()
    {
        var result = ProductStatusResult.FromRows(
            ["Seller A", "Broken Seller"],
            [new ProductStatusRow("Seller A", "Online", 5)],
            ["Broken Seller"]);

        Assert.Equal(["Broken Seller"], result.Failed);
        Assert.DoesNotContain(result.Rows, r => r.SellerName == "Broken Seller");
    }

    [Fact]
    public void EverySubmittedSellerIsARowOrAFailureNeverBoth()
    {
        // What makes the accounting on the page trustworthy: a seller is a row (real counts, or zero
        // because withoutProducts says so) or it could not be read. If these two do not add up to the
        // list that ran, the report is claiming sellers vanished — which is the very thing it exists
        // to rule out. WithoutProducts is not a third bucket any more: it is a note on which rows are
        // zero rather than a real read, so it is a subset of Rows, not additional to it.
        var submitted = new[] { "Read", "Empty", "Broken" };

        var result = ProductStatusResult.FromRows(
            submitted,
            [new ProductStatusRow("Read", "Online", 5)],
            ["Broken"],
            ["Empty"],
            ProductStatusIntake.ParseLines(["Seller", .. submitted], null).Intake);

        Assert.Equal(result.Intake.Sellers, result.Rows.Count + result.Failed.Count);
        Assert.Contains("Empty", result.Rows.Select(r => r.SellerName));
        Assert.All(
            result.Rows.Single(r => r.SellerName == "Empty").Counts,
            count => Assert.Equal(0, count));
    }

    [Fact]
    public void AResultBuiltWithoutAnAccountStillBalances()
    {
        // The three-argument overload the tests above use, and the paths that have nothing to
        // reconcile, get an account where every submitted name is a seller.
        var result = Pivot(["Seller A"], new ProductStatusRow("Seller A", "Online", 1));

        Assert.Equal(1, result.Intake.Sellers);
        Assert.Empty(result.WithoutProducts);
    }

    [Fact]
    public void AnEmptyScrapeProducesAnEmptyTableRatherThanThrowing()
    {
        var result = Pivot(["Seller A"]);

        Assert.Empty(result.Labels);
        Assert.Empty(result.Rows);
    }
}
