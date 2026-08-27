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
    public void AnEmptyScrapeProducesAnEmptyTableRatherThanThrowing()
    {
        var result = Pivot(["Seller A"]);

        Assert.Empty(result.Labels);
        Assert.Empty(result.Rows);
    }
}
