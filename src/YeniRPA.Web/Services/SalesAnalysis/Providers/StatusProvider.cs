namespace YeniRPA.Web.Services.SalesAnalysis.Providers;

public sealed record StatusRow(string Status, string Label, bool InSales, int LinesA, int LinesB, double AmountA, double AmountB, double? ShareA, double? ShareB);

public sealed record CancelRateRow(
    string Label, int LinesA, int LinesB, int CanceledA, int CanceledB, double? RateA, double? RateB, double? ChangePp, double CanceledAmountB);

public sealed record CountRow(string Label, int A, int B);

public sealed record StatusData(
    IReadOnlyList<StatusRow> Statuses,
    IReadOnlyList<CancelRateRow> ByCategory,
    IReadOnlyList<CancelRateRow> BySeller,
    IReadOnlyList<CountRow> CancellationRequests);

/// <summary>
/// (F) Status mix and cancellations: the status distribution in both periods, where the cancellation
/// rate rose (by category and by seller), and the Cancellation Request Status breakdown. Reads every
/// status, not only the operator's sales set — canceled lines are what it is about.
/// </summary>
public sealed class StatusProvider : IInsightProvider
{
    /// <summary>A category or seller with fewer lines than this in B is too small for its rate to mean anything.</summary>
    const int MinLines = 5;
    const int TopCount = 15;

    public string Key => "status";
    public string Title => "Statü ve iptal analizi";

    public InsightResult Compute(SalesAnalysisContext ctx)
    {
        var totalA = ctx.AllA.Count;
        var totalB = ctx.AllB.Count;
        var statuses = ctx.AllA.Concat(ctx.AllB).Select(l => l.Status).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                var a = ctx.AllA.Where(l => string.Equals(l.Status, s, StringComparison.OrdinalIgnoreCase)).ToList();
                var b = ctx.AllB.Where(l => string.Equals(l.Status, s, StringComparison.OrdinalIgnoreCase)).ToList();
                return new StatusRow(s, SalesStatuses.Label(s), ctx.Options.StatusSet.Contains(s),
                    a.Count, b.Count, a.Sum(Value), b.Sum(Value),
                    SalesMetrics.Ratio(a.Count, totalA), SalesMetrics.Ratio(b.Count, totalB));
            })
            .OrderByDescending(r => r.LinesB + r.LinesA)
            .ToList();

        var byCategory = CancelRates(ctx, l => SalesTextTr.TitleCase(l.CategoryLabel));
        var bySeller = CancelRates(ctx, l => SalesTextTr.OrUnknown(l.Seller));

        var requests = ctx.AllA.Concat(ctx.AllB)
            .Select(l => l.CancellationRequestStatus).Where(s => s.Length > 0).Distinct()
            .Select(s => new CountRow(s,
                ctx.AllA.Count(l => l.CancellationRequestStatus == s),
                ctx.AllB.Count(l => l.CancellationRequestStatus == s)))
            .OrderByDescending(r => r.A + r.B)
            .ToList();

        var findings = new List<Finding>();
        var summary = new List<string>();
        var a = ctx.MetricsA;
        var b = ctx.MetricsB;
        if (a.CancelRateLines is { } ra && b.CancelRateLines is { } rb)
        {
            var verb = rb > ra ? "yükseldi" : rb < ra ? "düştü" : "değişmedi";
            var text = $"İptal oranı {verb}: Dönem A'da satırların {SalesTextTr.Pct(ra)} kadarı iptal edilmişti, " +
                       $"Dönem B'de {SalesTextTr.Pct(rb)} kadarı. İptal edilen tutar {SalesTextTr.Tl(a.CanceledAmount)}'den " +
                       $"{SalesTextTr.Tl(b.CanceledAmount)}'ye {(b.CanceledAmount >= a.CanceledAmount ? "çıktı" : "indi")}.";
            summary.Add(text);

            // In money, so it can compete with the other reasons: the canceled amount the rate move
            // alone accounts for at B's volume.
            var impact = -(b.CanceledAmount - a.CanceledAmount);
            if (Math.Abs(rb - ra) >= 0.005)
            {
                var worst = byCategory.Where(r => r.ChangePp > 0).MaxBy(r => r.CanceledAmountB);
                findings.Add(new Finding(
                    text + (worst is not null && rb > ra
                        ? $" İptallerin en çok arttığı kategori {worst.Label}: oran {SalesTextTr.Pct(worst.RateA ?? 0)} iken {SalesTextTr.Pct(worst.RateB ?? 0)} oldu."
                        : ""),
                    impact, Key));
            }
        }

        return new InsightResult(Key, Title, summary, findings,
            new StatusData(statuses, byCategory, bySeller, requests));
    }

    /// <summary>A canceled line carries 0 in Amount; its value is in the canceled-amount column.</summary>
    static double Value(SalesLine l) => l.IsCanceled ? l.CanceledAmount : l.Amount;

    static List<CancelRateRow> CancelRates(SalesAnalysisContext ctx, Func<SalesLine, string> key)
    {
        var a = ctx.AllA.GroupBy(key).ToDictionary(g => g.Key, g => (Lines: g.Count(), Canceled: g.Count(l => l.IsCanceled), Amount: g.Sum(l => l.CanceledAmount)));
        var b = ctx.AllB.GroupBy(key).ToDictionary(g => g.Key, g => (Lines: g.Count(), Canceled: g.Count(l => l.IsCanceled), Amount: g.Sum(l => l.CanceledAmount)));

        return [.. b.Where(kv => kv.Value.Lines >= MinLines)
            .Select(kv =>
            {
                var va = a.GetValueOrDefault(kv.Key);
                var rateA = SalesMetrics.Ratio(va.Canceled, va.Lines);
                var rateB = SalesMetrics.Ratio(kv.Value.Canceled, kv.Value.Lines);
                return new CancelRateRow(kv.Key, va.Lines, kv.Value.Lines, va.Canceled, kv.Value.Canceled,
                    rateA, rateB, rateA is not null && rateB is not null ? (rateB - rateA) * 100 : null, kv.Value.Amount);
            })
            .Where(r => r.CanceledA + r.CanceledB > 0)
            .OrderByDescending(r => r.ChangePp ?? double.MinValue)
            .ThenByDescending(r => r.CanceledB)
            .Take(TopCount)];
    }
}
