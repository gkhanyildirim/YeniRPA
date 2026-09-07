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
    public void ASellerThatReturnedNothingIsLeftOutRatherThanShownAsZeros()
    {
        // "No products" and "could not be read" both produce no rows; neither is the same claim as a
        // catalogue of zero online offers, so neither gets a row.
        var result = Pivot(
            ["Seller A", "Empty Seller"],
            new ProductStatusRow("Seller A", "Online", 5));

        Assert.Equal(["Seller A"], result.Rows.Select(r => r.SellerName));
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
    public void EverySellerThatRanIsInExactlyOneOfTheThreeBuckets()
    {
        // What makes the accounting on the page trustworthy: a seller is a row, or has no products,
        // or could not be read. If these three do not add up to the list that ran, the report is
        // claiming sellers vanished — which is the very thing it exists to rule out.
        var submitted = new[] { "Read", "Empty", "Broken" };

        var result = ProductStatusResult.FromRows(
            submitted,
            [new ProductStatusRow("Read", "Online", 5)],
            ["Broken"],
            ["Empty"],
            ProductStatusIntake.ParseLines(["Seller", .. submitted], null).Intake);

        Assert.Equal(
            result.Intake.Sellers,
            result.Rows.Count + result.WithoutProducts.Count + result.Failed.Count);
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
