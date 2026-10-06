namespace YeniRPA.Web.Services.SalesAnalysis.Providers;

public sealed record OrderTotal(string OrderNumber, string Date, string Seller, string Category, string Title, double Amount);

/// <summary>"If the top N orders of each period are left out, the change in sales becomes …".</summary>
public sealed record ExclusionScenario(string Label, int OrdersA, int OrdersB, double DeltaWithout, double Effect, double? ShareOfDelta);

public sealed record OutlierData(
    double Delta,
    double? LargestOrderShareB,
    IReadOnlyList<ExclusionScenario> Scenarios,
    IReadOnlyList<OrderTotal> TopOrdersA,
    IReadOnlyList<OrderTotal> TopOrdersB,
    OutlierExclusion? Excluded);

/// <summary>
/// (J) Single-order effect. Large orders (an iPhone or a notebook can be a 236,000 TL order) can
/// move a week's total on their own; this shows how much of the change hangs on the biggest one, the
/// top five and the top 1% of orders, so "sales rose" is never read off one big basket.
///
/// <para>The switch that actually removes outliers from every section lives in
/// <see cref="SalesAnalysisService"/> (see <see cref="OutlierFilter"/>); this provider reports on
/// whatever data is left after it.</para>
/// </summary>
public sealed class OutlierProvider : IInsightProvider
{
    const int ListCount = 10;

    /// <summary>A scenario whose effect is at least this share of the change is worth a reason.</summary>
    const double NotableShare = 0.3;

    public string Key => "outlier";
    public string Title => "Büyük siparişlerin etkisi";

    public InsightResult Compute(SalesAnalysisContext ctx)
    {
        var ordersA = OrderTotals(ctx.SalesA);
        var ordersB = OrderTotals(ctx.SalesB);
        var salesA = ordersA.Sum(o => o.Amount);
        var salesB = ordersB.Sum(o => o.Amount);
        var delta = salesB - salesA;

        ExclusionScenario Scenario(string label, int nA, int nB)
        {
            var without = (salesB - ordersB.Take(nB).Sum(o => o.Amount)) - (salesA - ordersA.Take(nA).Sum(o => o.Amount));
            var effect = delta - without;
            return new ExclusionScenario(label, nA, nB, without, effect, SalesMetrics.Ratio(effect, delta));
        }

        static int OnePercent(int count) => count == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(count * 0.01));

        List<ExclusionScenario> scenarios =
        [
            Scenario("En büyük sipariş", Math.Min(1, ordersA.Count), Math.Min(1, ordersB.Count)),
            Scenario("En büyük 5 sipariş", Math.Min(5, ordersA.Count), Math.Min(5, ordersB.Count)),
            Scenario("En büyük %1'lik dilim", OnePercent(ordersA.Count), OnePercent(ordersB.Count)),
        ];

        static string Signed(double v) => (v > 0 ? "+" : v < 0 ? "−" : "") + SalesTextTr.Tl(v);

        var summary = new List<string>();
        var findings = new List<Finding>();
        var largestShare = ordersB.Count > 0 ? SalesMetrics.Ratio(ordersB[0].Amount, salesB) : null;

        if (ordersB.Count > 0)
        {
            summary.Add($"Dönem B'deki en büyük sipariş {SalesTextTr.Tl(ordersB[0].Amount)} tutarında; " +
                        $"bu tek sipariş dönemin toplam satışının {SalesTextTr.PctOf(largestShare ?? 0, 1)}.");
        }

        if (delta != 0)
        {
            // The widest scenario decides the verdict: if even dropping the top 1% barely moves the
            // change, no handful of baskets is behind it.
            var widest = scenarios[^1];
            var share = Math.Abs(widest.ShareOfDelta ?? 0);
            var compare = $"Her iki dönemden en büyük %1'lik siparişler çıkarıldığında fark {Signed(delta)} yerine {Signed(widest.DeltaWithout)} oluyor.";

            if (share < 0.1)
                summary.Add("Sonuç: Fark büyük siparişlerden kaynaklanmıyor, satışların geneline yayılmış. " + compare);
            else if (share < NotableShare)
                summary.Add("Sonuç: Büyük siparişlerin farkta küçük bir payı var, ama farkın çoğu satışların genelinden geliyor. " + compare);
            else
                summary.Add("Dikkat: Farkın önemli bir kısmı birkaç büyük siparişten geliyor. " + compare);

            var notable = scenarios.FirstOrDefault(s => s.ShareOfDelta is { } sh && Math.Abs(sh) >= NotableShare);
            if (notable is not null)
            {
                var label = notable.Label.ToLower(SalesTextTr.Tr);
                findings.Add(new Finding(
                    $"Farkın {SalesTextTr.PctOf(Math.Abs(notable.ShareOfDelta ?? 0))} birkaç büyük siparişten geliyor: " +
                    $"{label} çıkarılınca fark {Signed(delta)} yerine {Signed(notable.DeltaWithout)} oluyor. " +
                    "Yani bu değişim satışların geneline yayılmış değil.",
                    notable.Effect, Key));
            }
        }

        return new InsightResult(Key, Title, summary, findings, new OutlierData(
            delta, largestShare, scenarios,
            [.. ordersA.Take(ListCount)], [.. ordersB.Take(ListCount)], ctx.Exclusion));
    }

    /// <summary>Order totals from line Amounts (never the repeated order-level total), largest first.</summary>
    internal static List<OrderTotal> OrderTotals(IEnumerable<SalesLine> lines) =>
        [.. lines.GroupBy(l => l.OrderNumber, StringComparer.Ordinal)
            .Select(g =>
            {
                var biggest = g.MaxBy(l => l.Amount)!;
                return new OrderTotal(g.Key, g.Min(l => l.Created).ToString("dd.MM.yyyy HH:mm", SalesTextTr.Tr),
                    biggest.Seller, SalesTextTr.TitleCase(biggest.CategoryLabel), biggest.Title, g.Sum(l => l.Amount));
            })
            .OrderByDescending(o => o.Amount)];
}
