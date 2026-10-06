namespace YeniRPA.Web.Services.SalesAnalysis.Providers;

/// <summary>
/// One headline tile. <see cref="Kind"/> tells the page how to format it: <c>money</c>, <c>count</c>
/// or <c>rate</c> (a rate's change is shown in percentage points, not percent). <see cref="GoodWhen"/>
/// is "up", "down" or null when neither direction is good news.
/// </summary>
public sealed record KpiItem(string Key, string Label, string Kind, string? GoodWhen, MetricChange Change);

/// <summary>(A) The headline KPIs, each in both periods with its absolute and percent change.</summary>
public sealed class KpiProvider : IInsightProvider
{
    public string Key => "kpi";
    public string Title => "Özet göstergeler";

    public InsightResult Compute(SalesAnalysisContext ctx)
    {
        var a = ctx.MetricsA;
        var b = ctx.MetricsB;

        List<KpiItem> items =
        [
            new("gross", "Satış (brüt)", "money", "up", MetricChange.Of(a.GrossSales, b.GrossSales)),
            new("orders", "Sipariş sayısı", "count", "up", MetricChange.Of(a.Orders, b.Orders)),
            new("units", "Satılan ürün adedi", "count", "up", MetricChange.Of(a.Units, b.Units)),
            new("avgPrice", "Ürün başına ort. fiyat", "money", null, MetricChange.Of(a.AvgUnitPrice, b.AvgUnitPrice)),
            new("aov", "Sipariş başına ort. tutar", "money", "up", MetricChange.Of(a.AverageOrderValue, b.AverageOrderValue)),
            new("cancelRate", "İptal oranı (satır)", "rate", "down", MetricChange.Of(a.CancelRateLines, b.CancelRateLines)),
            new("cancelAmountRate", "İptal oranı (tutar)", "rate", "down", MetricChange.Of(a.CancelRateAmount, b.CancelRateAmount)),
            new("canceledAmount", "İptal edilen tutar", "money", "down", MetricChange.Of(a.CanceledAmount, b.CanceledAmount)),
            new("rejected", "Satıcının reddettiği satır", "count", "down", MetricChange.Of(a.RejectedLines, b.RejectedLines)),
            new("commissionRate", "Komisyon oranı", "rate", null, MetricChange.Of(a.CommissionRate, b.CommissionRate)),
        ];

        return new InsightResult(Key, Title, [ReasonsComposer.Headline(a, b)], [], items);
    }
}
