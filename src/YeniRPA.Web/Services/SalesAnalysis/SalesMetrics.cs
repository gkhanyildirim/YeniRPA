namespace YeniRPA.Web.Services.SalesAnalysis;

/// <summary>
/// The headline figures of one period. Every ratio is <c>null</c> when its denominator is zero —
/// never 0, NaN or infinity — so the page can print "—" instead of a number that looks real.
/// </summary>
public sealed record PeriodMetrics(
    double GrossSales,
    int Orders,
    double Units,
    double? AvgUnitPrice,
    double? AverageOrderValue,
    int SalesLines,
    int Lines,
    int CanceledLines,
    double CanceledAmount,
    double? CancelRateLines,
    double? CancelRateAmount,
    int RejectedLines,
    double RejectedAmount,
    double Commission,
    double? CommissionRate,
    double ShippingRevenue,
    double TransferredToSeller);

/// <summary>A figure in both periods and how it moved. <see cref="Pct"/> is null when A is zero or missing.</summary>
public sealed record MetricChange(double? A, double? B, double? Abs, double? Pct)
{
    public static MetricChange Of(double? a, double? b)
    {
        double? abs = a is not null && b is not null ? b - a : null;
        double? pct = abs is not null && a is not null && a.Value != 0 ? abs / Math.Abs(a.Value) : null;
        return new MetricChange(a, b, abs, pct);
    }
}

/// <summary>
/// The single definition of every Sales Analysis metric. Nothing else in the module adds up sales,
/// counts orders or derives a rate on its own — the providers call these, so two sections can never
/// disagree about what "sales" means.
///
/// <list type="bullet">
///   <item><b>Gross sales</b> — Σ Amount (VAT incl.) of lines whose status is in the operator's
///   sales set (default: every status except Canceled and Rejected).</item>
///   <item><b>Orders</b> — distinct Order number among those lines; <b>Units</b> — Σ Quantity.</item>
///   <item><b>Average unit price</b> — Gross / Units, i.e. Amount-based, so it moves exactly with the
///   PVM decomposition (Unit price excludes withholding tax and is not used).</item>
///   <item><b>AOV</b> — Gross / Orders.</item>
///   <item><b>Cancellation rate</b> — over <em>all</em> lines of the period regardless of the
///   status set: canceled lines / lines, and canceled amount / (Σ Amount + canceled amount).
///   Rejected lines are reported separately.</item>
///   <item><b>Commission rate</b> — Σ Commission (excl. taxes) / Gross.</item>
///   <item><b>Shipping revenue</b> — order-level, so taken once per Order number, never per line.</item>
/// </list>
/// </summary>
public static class SalesMetrics
{
    public static HashSet<string> DefaultSalesStatuses(IEnumerable<string> allStatuses) =>
        new(allStatuses.Where(s => !SalesStatuses.ExcludedByDefault.Contains(s, StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

    public static PeriodMetrics Compute(IReadOnlyCollection<SalesLine> allLines, IReadOnlyCollection<SalesLine> salesLines)
    {
        var gross = salesLines.Sum(l => l.Amount);
        var units = salesLines.Sum(l => l.Quantity);
        var orders = CountOrders(salesLines);

        var lines = allLines.Count;
        var canceledLines = allLines.Count(l => l.IsCanceled);
        var canceledAmount = allLines.Sum(l => l.CanceledAmount);
        var requestedAmount = allLines.Sum(l => l.Amount) + canceledAmount;
        var rejected = allLines.Where(l => l.IsRejected).ToList();
        var commission = salesLines.Sum(l => l.Commission);

        // Order-level: one value per order, read from its first line. Summing it per line would
        // count a three-line order's shipping three times.
        var shipping = salesLines
            .GroupBy(l => l.OrderNumber, StringComparer.Ordinal)
            .Sum(g => g.First().ShippingPrice);

        return new PeriodMetrics(
            GrossSales: gross,
            Orders: orders,
            Units: units,
            AvgUnitPrice: Ratio(gross, units),
            AverageOrderValue: Ratio(gross, orders),
            SalesLines: salesLines.Count,
            Lines: lines,
            CanceledLines: canceledLines,
            CanceledAmount: canceledAmount,
            CancelRateLines: Ratio(canceledLines, lines),
            CancelRateAmount: Ratio(canceledAmount, requestedAmount),
            RejectedLines: rejected.Count,
            RejectedAmount: rejected.Sum(l => l.Amount),
            Commission: commission,
            CommissionRate: Ratio(commission, gross),
            ShippingRevenue: shipping,
            TransferredToSeller: salesLines.Sum(l => l.TransferredToSeller));
    }

    public static int CountOrders(IEnumerable<SalesLine> lines) =>
        lines.Select(l => l.OrderNumber).Distinct(StringComparer.Ordinal).Count();

    /// <summary>a / b, or null when b is zero (or either side is not a finite number).</summary>
    public static double? Ratio(double a, double b)
    {
        if (b == 0 || !double.IsFinite(a) || !double.IsFinite(b))
            return null;
        var value = a / b;
        return double.IsFinite(value) ? value : null;
    }
}
