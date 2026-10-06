namespace YeniRPA.Web.Services.SalesAnalysis;

/// <summary>
/// The "exclude outlier orders" switch. Order totals are Σ line Amount over the sales statuses,
/// pooled across both periods so the same cut-off applies to A and B; every line of an excluded
/// order (all statuses) is dropped before any provider runs.
/// <list type="bullet">
///   <item><c>none</c> — nothing removed.</item>
///   <item><c>top1pct</c> — the largest 1% of orders (at least one).</item>
///   <item><c>iqr</c> — orders above Q3 + 1.5 × IQR (Tukey's fence). On a long-tailed basket
///   distribution this removes noticeably more than 1%; the page states how many.</item>
/// </list>
/// </summary>
public static class OutlierFilter
{
    public const string None = "none";
    public const string TopOnePercent = "top1pct";
    public const string Iqr = "iqr";

    public static (List<SalesLine> A, List<SalesLine> B, OutlierExclusion? Exclusion) Apply(
        IReadOnlyList<SalesLine> allA, IReadOnlyList<SalesLine> allB, IReadOnlySet<string> statusSet, string? mode)
    {
        mode = (mode ?? None).Trim().ToLowerInvariant();
        if (mode is not (TopOnePercent or Iqr))
            return ([.. allA], [.. allB], null);

        var totals = allA.Concat(allB)
            .Where(l => statusSet.Contains(l.Status))
            .GroupBy(l => l.OrderNumber, StringComparer.Ordinal)
            .Select(g => (Order: g.Key, Amount: g.Sum(l => l.Amount)))
            .OrderByDescending(t => t.Amount)
            .ToList();

        if (totals.Count == 0)
            return ([.. allA], [.. allB], new OutlierExclusion(mode, 0, 0, null));

        List<(string Order, double Amount)> excluded;
        double? threshold;
        if (mode == TopOnePercent)
        {
            var n = Math.Max(1, (int)Math.Ceiling(totals.Count * 0.01));
            excluded = totals.Take(n).ToList();
            threshold = excluded[^1].Amount;
        }
        else
        {
            var sorted = totals.Select(t => t.Amount).Order().ToArray();
            var q1 = Quantile(sorted, 0.25);
            var q3 = Quantile(sorted, 0.75);
            threshold = q3 + 1.5 * (q3 - q1);
            var fence = threshold.Value;
            excluded = totals.Where(t => t.Amount > fence).ToList();
        }

        var drop = excluded.Select(e => e.Order).ToHashSet(StringComparer.Ordinal);
        return (
            [.. allA.Where(l => !drop.Contains(l.OrderNumber))],
            [.. allB.Where(l => !drop.Contains(l.OrderNumber))],
            new OutlierExclusion(mode, excluded.Count, excluded.Sum(e => e.Amount), threshold));
    }

    /// <summary>Linear-interpolated quantile of an ascending array (Excel's PERCENTILE.INC).</summary>
    internal static double Quantile(double[] ascending, double q)
    {
        if (ascending.Length == 0)
            return 0;
        var position = (ascending.Length - 1) * q;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return ascending[lower] + (ascending[upper] - ascending[lower]) * (position - lower);
    }
}
