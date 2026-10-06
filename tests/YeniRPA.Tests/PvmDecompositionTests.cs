using YeniRPA.Web.Services.SalesAnalysis;
using static YeniRPA.Tests.SalesAnalysisFixtures;

namespace YeniRPA.Tests;

/// <summary>
/// <see cref="PvmDecomposition"/>: volume + price + mix must equal the change in sales exactly, with
/// no residual — overall and for every single item.
/// </summary>
public class PvmDecompositionTests
{
    const double Tolerance = 1e-6;

    static PvmResult Pvm(IEnumerable<SalesLine> a, IEnumerable<SalesLine> b) =>
        PvmDecomposition.Compute(a, b, l => l.ProductKey);

    static void AssertReconciles(PvmResult r)
    {
        Assert.Equal(r.Delta, r.Volume + r.Price + r.Mix, Tolerance);
        foreach (var item in r.Items)
            Assert.Equal(item.Delta, item.Volume + item.Price + item.Mix, Tolerance);
    }

    [Fact]
    public void Hand_calculated_example()
    {
        // A: X 10 @ 10, Y 10 @ 30 → 400, avg 20.   B: X 5 @ 12, Y 20 @ 30 → 660.
        var r = Pvm(
            [Line("a1", "X", 10, 100), Line("a2", "Y", 10, 300)],
            [Line("b1", "X", 5, 60), Line("b2", "Y", 20, 600)]);

        Assert.Equal(260, r.Delta, Tolerance);
        Assert.Equal(100, r.Volume, Tolerance);   // (25 − 20) × 20
        Assert.Equal(10, r.Price, Tolerance);     // 5 × (12 − 10)
        Assert.Equal(150, r.Mix, Tolerance);      // (5−10)(10−20) + (20−10)(30−20)
        AssertReconciles(r);

        Assert.Equal(20, r.AvgPriceA!.Value, Tolerance);
        Assert.Equal(26.4, r.AvgPriceB!.Value, Tolerance);
        Assert.Equal(0.4, r.AvgPriceFromPrice!.Value, Tolerance);
        Assert.Equal(6, r.AvgPriceFromMix!.Value, Tolerance);
        Assert.Equal(r.AvgPriceDelta!.Value, r.AvgPriceFromPrice.Value + r.AvgPriceFromMix.Value, Tolerance);
    }

    [Fact]
    public void A_single_product_has_no_mix()
    {
        var r = Pvm([Line("a", "X", 10, 100)], [Line("b", "X", 12, 132)]);

        Assert.Equal(20, r.Volume, Tolerance);
        Assert.Equal(12, r.Price, Tolerance);
        Assert.Equal(0, r.Mix, Tolerance);
        AssertReconciles(r);
    }

    [Fact]
    public void New_and_lost_products_land_in_mix()
    {
        var r = Pvm([Line("a", "X", 10, 100)], [Line("b", "Z", 5, 200)]);

        Assert.Equal(-50, r.Volume, Tolerance);
        Assert.Equal(0, r.Price, Tolerance);
        Assert.Equal(150, r.Mix, Tolerance);
        Assert.Equal(150, r.MixFromNewAndLost, Tolerance);
        Assert.Contains(r.Items, i => i.Key == "Z" && i.IsNew);
        Assert.Contains(r.Items, i => i.Key == "X" && i.IsLost);
        AssertReconciles(r);
    }

    [Fact]
    public void An_empty_base_period_puts_everything_in_mix()
    {
        var r = Pvm([], [Line("b", "X", 2, 50)]);

        Assert.Equal(50, r.Delta);
        Assert.Equal(0, r.Volume);
        Assert.Equal(50, r.Mix, Tolerance);
        Assert.Null(r.AvgPriceA);
        Assert.Null(r.AvgPriceFromMix);
        AssertReconciles(r);
    }

    [Fact]
    public void An_empty_compared_period_is_all_volume()
    {
        var r = Pvm([Line("a", "X", 2, 50), Line("a", "Y", 1, 10)], []);

        Assert.Equal(-60, r.Delta);
        Assert.Equal(-60, r.Volume, Tolerance);
        Assert.Null(r.AvgPriceB);
        AssertReconciles(r);
    }

    [Fact]
    public void Both_periods_empty_is_all_zero()
    {
        var r = Pvm([], []);

        Assert.Equal(0, r.Delta);
        Assert.Equal(0, r.Volume + r.Price + r.Mix);
    }

    [Fact]
    public void Zero_quantity_lines_with_an_amount_still_reconcile()
    {
        var r = Pvm(
            [Line("a1", "X", 0, 40), Line("a2", "Y", 3, 30)],
            [Line("b1", "X", 2, 90), Line("b2", "Y", 0, 15)]);

        AssertReconciles(r);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    public void Random_periods_always_reconcile_exactly(int seed)
    {
        var random = new Random(seed);
        List<SalesLine> Period(string prefix) => [.. Enumerable.Range(0, random.Next(0, 300)).Select(i =>
        {
            var qty = random.Next(0, 5);
            var price = random.NextDouble() * 5000;
            return Line(prefix + i, "SKU" + random.Next(0, 60), qty, Math.Round(qty * price, 2));
        })];

        var r = Pvm(Period("a"), Period("b"));

        AssertReconciles(r);
    }
}
