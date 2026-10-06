using YeniRPA.Web.Services.SalesAnalysis;
using YeniRPA.Web.Services.SalesAnalysis.Country;
using static YeniRPA.Tests.SalesAnalysisFixtures;

namespace YeniRPA.Tests;

/// <summary>
/// The country comparison: leaders and gaps, the currency rule (no money difference without one
/// currency), the overlap scope, category strengths, and that highlights only appear for material,
/// comparable differences.
/// </summary>
public class CountryComparisonTests
{
    static SalesDataset Dataset(IEnumerable<SalesLine> lines, string? currency = "TRY", params string[] missingColumns)
    {
        var list = lines.Select(l => l with { Currency = currency ?? "" }).ToList();
        return new SalesDataset(list, new ImportReport(list.Count, list.Count, [], missingColumns, currency is null ? [] : [currency]));
    }

    /// <summary><paramref name="perDay"/> one-unit orders a day for <paramref name="days"/> days from <paramref name="firstDay"/>.</summary>
    static IEnumerable<SalesLine> Orders(string prefix, int days, int perDay, double amount, string category = "SMARTPHONES", int firstDay = 0)
    {
        for (var d = 0; d < days; d++)
            for (var i = 0; i < perDay; i++)
                yield return Line($"{prefix}-{d}-{i}", $"{prefix}SKU{i % 5}", 1, amount, day: firstDay + d, category: category);
    }

    static CountryComparisonResponse Analyze(SalesDataset a, SalesDataset b, string scope = "all", double? rate = null)
    {
        var store = new CountryComparisonStore();
        var token = store.Put(new CountryPair(a, "Turkiye.xlsx", b, "Almanya.xlsx"));
        return new CountryComparisonService(store).Analyze(new CountryComparisonRequest(token, "Türkiye", "Almanya", null, scope, rate));
    }

    [Fact]
    public void Kpi_names_the_leader_and_the_gap_relative_to_B()
    {
        var orders = CountryComparisonService.Kpi("orders", "Sipariş", "count", "up", 120, 100);
        Assert.Equal(20, orders.Abs);
        Assert.Equal(0.2, orders.Pct!.Value, 6);
        Assert.Equal("a", orders.Higher);
        Assert.Equal("a", orders.Leader);

        // Lower is better for a cancellation rate; a rate's gap is in points, with no percent.
        var cancel = CountryComparisonService.Kpi("cancelRate", "İptal", "rate", "down", 0.02, 0.05);
        Assert.Equal("b", cancel.Higher);
        Assert.Equal("a", cancel.Leader);
        Assert.Null(cancel.Pct);

        // Neutral metrics say which is higher but crown no one; a sub-0.5% gap is a tie.
        Assert.Null(CountryComparisonService.Kpi("avgPrice", "Fiyat", "money", null, 10, 8).Leader);
        Assert.Equal("tie", CountryComparisonService.Kpi("orders", "Sipariş", "count", "up", 100, 100.4).Leader);
    }

    [Fact]
    public void Different_currencies_without_a_rate_compare_no_money()
    {
        var result = Analyze(Dataset(Orders("A", 7, 10, 100)), Dataset(Orders("B", 7, 8, 50), "EUR"));

        Assert.False(result.MoneyComparable);
        var gross = result.Kpis.Single(k => k.Key == "gross");
        Assert.False(gross.Comparable);
        Assert.Null(gross.Abs);
        Assert.Null(gross.Leader);
        Assert.Equal(7 * 8 * 50, gross.B!.Value, 6);   // still shown, in its own currency

        var orders = result.Kpis.Single(k => k.Key == "orders");
        Assert.True(orders.Comparable);
        Assert.Equal("a", orders.Leader);

        Assert.Contains(result.NotComparable, n => n.StartsWith("Tutarlar", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Insights, i => i.Text.StartsWith("Satış (brüt)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rate_converts_country_B_money_into_country_A_currency()
    {
        var result = Analyze(Dataset(Orders("A", 7, 10, 100)), Dataset(Orders("B", 7, 10, 50), "EUR"), rate: 3);

        Assert.True(result.MoneyComparable);
        var gross = result.Kpis.Single(k => k.Key == "gross");
        Assert.Equal(7 * 10 * 150, gross.B!.Value, 6);
        Assert.Equal("b", gross.Leader);
        Assert.Equal("TRY", result.B.DisplayCurrency);
    }

    [Fact]
    public void Overlap_scope_keeps_only_the_common_days()
    {
        var result = Analyze(Dataset(Orders("A", 10, 5, 100)), Dataset(Orders("B", 10, 5, 100, firstDay: 5)), scope: "overlap");

        Assert.Equal(5, result.A.Days);
        Assert.Equal(5, result.B.Days);
        Assert.Equal(result.A.From, result.B.From);
        Assert.Equal(25, result.A.Orders);
    }

    [Fact]
    public void Different_date_spans_are_called_out()
    {
        var result = Analyze(Dataset(Orders("A", 7, 10, 100)), Dataset(Orders("B", 14, 10, 100)));
        Assert.Contains(result.Warnings, w => w.Contains("farklı tarihleri", StringComparison.Ordinal));

        // With unequal spans the highlights use daily averages, never the raw totals.
        Assert.DoesNotContain(result.Insights, i => i.Text.StartsWith("Sipariş sayısı", StringComparison.Ordinal));
    }

    [Fact]
    public void A_category_much_bigger_in_one_country_is_its_strength()
    {
        var a = Orders("A", 7, 8, 100, "SMARTPHONES").Concat(Orders("A2", 7, 2, 100, "TELEVISIONS"));
        var b = Orders("B", 7, 2, 100, "SMARTPHONES").Concat(Orders("B2", 7, 8, 100, "TELEVISIONS"));
        var result = Analyze(Dataset(a), Dataset(b));

        Assert.Equal("Smartphones", Assert.Single(result.Category.StrongA).Label);
        Assert.Equal("Televisions", Assert.Single(result.Category.StrongB).Label);
        Assert.Contains(result.Insights, i => i.Topic == "kategori" && i.Text.StartsWith("Smartphones", StringComparison.Ordinal));
        Assert.Contains(result.StrengthsA, s => s.StartsWith("Smartphones", StringComparison.Ordinal));
    }

    [Fact]
    public void Identical_countries_produce_no_highlights_or_strengths()
    {
        var result = Analyze(Dataset(Orders("A", 7, 10, 100)), Dataset(Orders("A", 7, 10, 100)));

        Assert.DoesNotContain(result.Insights, i => i.Topic is "kpi" or "kategori" or "marka");
        Assert.Empty(result.StrengthsA);
        Assert.Empty(result.StrengthsB);
    }

    [Fact]
    public void Few_orders_and_a_missing_column_are_reported()
    {
        var result = Analyze(Dataset(Orders("A", 2, 5, 100), "TRY", SalesColumnMap.Commission), Dataset(Orders("B", 2, 5, 100)));

        Assert.Contains(result.Warnings, w => w.Contains("siparişten az", StringComparison.Ordinal));
        var commission = result.Kpis.Single(k => k.Key == "commissionRate");
        Assert.False(commission.Comparable);
        Assert.Contains(result.NotComparable, n => n.StartsWith("Komisyon oranı", StringComparison.Ordinal));
    }
}
