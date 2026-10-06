namespace YeniRPA.Web.Services.SalesAnalysis.Providers;

public sealed record CommissionRow(
    string Label, double SalesA, double SalesB, double CommissionA, double CommissionB, double? RateA, double? RateB, double? ChangePp, double RateEffect);

public sealed record ProfitabilityData(
    MetricChange Commission,
    MetricChange CommissionRate,
    MetricChange TransferredToSeller,
    IReadOnlyList<CommissionRow> ByCategory);

/// <summary>
/// (I) Commission: how the commission rate moved and which categories moved it. The rate effect of
/// a category is SalesB × (rateB − rateA) — the commission B would have earned differently had the
/// category kept A's rate. Commission is excl. taxes while Amount is VAT-incl., so the rate is a
/// consistent ratio between periods rather than a true margin.
/// </summary>
public sealed class ProfitabilityProvider : IInsightProvider
{
    const int TopCount = 20;

    public string Key => "profit";
    public string Title => "Kârlılık (komisyon)";

    public InsightResult Compute(SalesAnalysisContext ctx)
    {
        static string Cat(SalesLine l) => SalesTextTr.TitleCase(l.CategoryLabel);
        var a = ctx.SalesA.GroupBy(Cat).ToDictionary(g => g.Key, g => (Sales: g.Sum(l => l.Amount), Commission: g.Sum(l => l.Commission)));
        var b = ctx.SalesB.GroupBy(Cat).ToDictionary(g => g.Key, g => (Sales: g.Sum(l => l.Amount), Commission: g.Sum(l => l.Commission)));

        var rows = a.Keys.Union(b.Keys)
            .Select(k =>
            {
                var va = a.GetValueOrDefault(k);
                var vb = b.GetValueOrDefault(k);
                var rateA = SalesMetrics.Ratio(va.Commission, va.Sales);
                var rateB = SalesMetrics.Ratio(vb.Commission, vb.Sales);
                var change = rateA is not null && rateB is not null ? rateB - rateA : null;
                return new CommissionRow(k, va.Sales, vb.Sales, va.Commission, vb.Commission, rateA, rateB,
                    change * 100, change is null ? 0 : vb.Sales * change.Value);
            })
            .OrderByDescending(r => Math.Abs(r.RateEffect))
            .ThenByDescending(r => r.CommissionB)
            .Take(TopCount)
            .ToList();

        var m = (A: ctx.MetricsA, B: ctx.MetricsB);
        var rate = MetricChange.Of(m.A.CommissionRate, m.B.CommissionRate);
        var summary = new List<string>();
        var findings = new List<Finding>();

        if (rate.A is { } ra && rate.B is { } rb)
        {
            var movedPoints = Math.Abs(rb - ra) * 100;
            var text = $"Komisyon oranı Dönem A'da {SalesTextTr.Pct(ra, 2)} iken Dönem B'de {SalesTextTr.Pct(rb, 2)} oldu" +
                       (movedPoints < 0.005 ? "." : $" ({SalesTextTr.Number(movedPoints, 2)} puan {(rb > ra ? "arttı" : "azaldı")}).") +
                       $" Toplam komisyon {SalesTextTr.Tl(m.A.Commission)}'den {SalesTextTr.Tl(m.B.Commission)}'ye " +
                       $"{(m.B.Commission >= m.A.Commission ? "çıktı" : "indi")}.";
            summary.Add(text);

            // Worth a reason only when the rate actually moved by a meaningful amount.
            if (Math.Abs(rb - ra) >= 0.002)
                findings.Add(new Finding(text, m.B.GrossSales * (rb - ra), Key));
        }

        return new InsightResult(Key, Title, summary, findings, new ProfitabilityData(
            MetricChange.Of(m.A.Commission, m.B.Commission),
            rate,
            MetricChange.Of(m.A.TransferredToSeller, m.B.TransferredToSeller),
            rows));
    }
}
