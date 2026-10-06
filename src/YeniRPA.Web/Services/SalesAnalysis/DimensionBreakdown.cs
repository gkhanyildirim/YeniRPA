namespace YeniRPA.Web.Services.SalesAnalysis;

/// <summary>
/// One value of a dimension (a category, brand, seller, city) in both periods.
/// <see cref="Contribution"/> is this row's share of the total change in sales — the rows' shares
/// add up to 1 — and is null when the total did not move.
/// </summary>
public sealed record DimensionRow(
    string Key,
    string Label,
    double SalesA,
    double SalesB,
    double Delta,
    double? Pct,
    double? Contribution,
    double QtyA,
    double QtyB,
    int OrdersA,
    int OrdersB,
    double? PriceA,
    double? PriceB,
    double? ShareA,
    double? ShareB,
    int Members = 1);

/// <summary>Share of each period's sales held by its top N rows, and how that moved.</summary>
public sealed record Concentration(int TopN, double? ShareA, double? ShareB, IReadOnlyList<string> TopB);

/// <summary>
/// The per-dimension comparison every breakdown provider is built on, so category, brand, seller and
/// city are guaranteed to compute shares and contributions the same way.
/// </summary>
public static class DimensionBreakdown
{
    public const string OtherKey = "__other__";
    public const string OtherLabel = "Diğer";

    public static List<DimensionRow> Build(
        IEnumerable<SalesLine> salesA,
        IEnumerable<SalesLine> salesB,
        Func<SalesLine, string> key,
        Func<string, string> label)
    {
        var a = Aggregate(salesA, key);
        var b = Aggregate(salesB, key);
        var totalA = a.Values.Sum(x => x.Sales);
        var totalB = b.Values.Sum(x => x.Sales);
        var totalDelta = totalB - totalA;

        return [.. a.Keys.Union(b.Keys, StringComparer.Ordinal)
            .Select(k =>
            {
                var va = a.GetValueOrDefault(k);
                var vb = b.GetValueOrDefault(k);
                return Row(k, label(k), va, vb, totalA, totalB, totalDelta, 1);
            })
            .OrderByDescending(r => r.SalesB)
            .ThenByDescending(r => r.SalesA)];
    }

    /// <summary>
    /// Folds every row whose share is under <paramref name="thresholdPct"/> percent in <em>both</em>
    /// periods into one "Diğer" row. Totals and contributions are unchanged by the fold — it only
    /// shortens the list.
    /// </summary>
    public static List<DimensionRow> GroupSmall(List<DimensionRow> rows, double thresholdPct)
    {
        if (thresholdPct <= 0)
            return rows;

        var limit = thresholdPct / 100.0;
        bool IsSmall(DimensionRow r) => (r.ShareA ?? 0) < limit && (r.ShareB ?? 0) < limit;

        var small = rows.Where(IsSmall).ToList();
        if (small.Count < 2)
            return rows;

        return [.. rows.Where(r => !IsSmall(r)), Fold(rows, small)];
    }

    /// <summary>Caps a list at <paramref name="max"/> rows, folding the tail into "Diğer" so no sales go missing.</summary>
    public static List<DimensionRow> FoldTail(List<DimensionRow> rows, int max)
    {
        if (rows.Count <= max || max < 2)
            return rows;

        var tail = rows.Skip(max - 1).ToList();
        return [.. rows.Take(max - 1), Fold(rows, tail)];
    }

    /// <summary>
    /// One "Diğer" row for <paramref name="members"/>. Its order counts are the members' sum, so an
    /// order spanning two folded values counts twice there — the row is a bucket, not a metric.
    /// </summary>
    static DimensionRow Fold(List<DimensionRow> all, List<DimensionRow> members)
    {
        var totalA = all.Sum(r => r.SalesA);
        var totalB = all.Sum(r => r.SalesB);
        return Row(OtherKey, $"{OtherLabel} ({members.Sum(r => r.Members)})",
            new Agg(members.Sum(r => r.QtyA), members.Sum(r => r.SalesA), members.Sum(r => r.OrdersA)),
            new Agg(members.Sum(r => r.QtyB), members.Sum(r => r.SalesB), members.Sum(r => r.OrdersB)),
            totalA, totalB, totalB - totalA, members.Sum(r => r.Members));
    }

    public static Concentration TopShare(IReadOnlyList<DimensionRow> rows, int topN)
    {
        var totalA = rows.Sum(r => r.SalesA);
        var totalB = rows.Sum(r => r.SalesB);
        var topA = rows.OrderByDescending(r => r.SalesA).Take(topN).Sum(r => r.SalesA);
        var topBRows = rows.OrderByDescending(r => r.SalesB).Take(topN).ToList();

        return new Concentration(
            topN,
            SalesMetrics.Ratio(topA, totalA),
            SalesMetrics.Ratio(topBRows.Sum(r => r.SalesB), totalB),
            [.. topBRows.Where(r => r.SalesB > 0).Select(r => r.Label)]);
    }

    /// <summary>Rows that moved the same way as the total, largest first — the "where did it come from" list.</summary>
    public static IEnumerable<DimensionRow> Drivers(IEnumerable<DimensionRow> rows, double totalDelta) =>
        totalDelta >= 0
            ? rows.Where(r => r.Delta > 0).OrderByDescending(r => r.Delta)
            : rows.Where(r => r.Delta < 0).OrderBy(r => r.Delta);

    /// <summary>
    /// The standard finding for a dimension, e.g. "Düşüşün %26'sı Notebooks kategorisinden geldi:
    /// bu kategoride satış 2 milyon TL azaldı. Satılan adet %31 düştü, ürün başına ortalama fiyat
    /// %8 düştü." Only rows that moved with the total are cited.
    /// </summary>
    public static IEnumerable<Finding> DriverFindings(
        IEnumerable<DimensionRow> rows, double totalDelta, string noun, string source, int take)
    {
        if (totalDelta == 0)
            yield break;

        var rising = totalDelta > 0;
        foreach (var row in Drivers(rows.Where(r => r.Key != OtherKey), totalDelta).Take(take))
        {
            var share = row.Contribution ?? 0;
            var priceChange = row.PriceA is > 0 && row.PriceB is not null ? (row.PriceB - row.PriceA) / row.PriceA : null;
            var qtyChange = row.QtyA > 0 ? (row.QtyB - row.QtyA) / row.QtyA : (double?)null;

            // Over 100% happens when other values moved the other way and took part of it back;
            // "%115'i" reads as an error, so that case says it in words.
            var head = share > 1
                ? $"{row.Label} {noun} gelen {(rising ? "artış" : "düşüş")}, toplam {(rising ? "artıştan" : "düşüşten")} bile büyük " +
                  $"(diğerleri ters yönde hareket etti): satış {SalesTextTr.Tl(row.Delta)} {SalesTextTr.Moved(row.Delta)}."
                : $"{(rising ? "Artışın" : "Düşüşün")} {SalesTextTr.PctOf(share)} {row.Label} {noun} geldi: " +
                  $"satış {SalesTextTr.Tl(row.Delta)} {SalesTextTr.Moved(row.Delta)}.";

            var detail = new List<string>();
            if (qtyChange is not null && Math.Abs(qtyChange.Value) >= 0.005)
                detail.Add($"satılan adet {SalesTextTr.Pct(qtyChange.Value, 0)} {SalesTextTr.Rose(qtyChange.Value)}");
            else if (qtyChange is null && row.QtyB > 0)
                detail.Add("önceki dönemde hiç satış yoktu");
            if (priceChange is not null && Math.Abs(priceChange.Value) >= 0.005)
                detail.Add($"ürün başına ortalama fiyat {SalesTextTr.Pct(priceChange.Value, 0)} {SalesTextTr.Rose(priceChange.Value)}");

            var text = head + (detail.Count > 0 ? " " + Capitalize(string.Join(", ", detail)) + "." : "");
            yield return new Finding(text, row.Delta, source);
        }
    }

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], SalesTextTr.Tr) + s[1..];

    readonly record struct Agg(double Qty, double Sales, int Orders);

    static Dictionary<string, Agg> Aggregate(IEnumerable<SalesLine> lines, Func<SalesLine, string> key) =>
        lines.GroupBy(key, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => new Agg(g.Sum(l => l.Quantity), g.Sum(l => l.Amount), SalesMetrics.CountOrders(g)),
            StringComparer.Ordinal);

    static DimensionRow Row(string key, string label, Agg a, Agg b, double totalA, double totalB, double totalDelta, int members)
    {
        var delta = b.Sales - a.Sales;
        return new DimensionRow(
            key, label, a.Sales, b.Sales, delta,
            Pct: a.Sales != 0 ? delta / Math.Abs(a.Sales) : null,
            Contribution: SalesMetrics.Ratio(delta, totalDelta),
            QtyA: a.Qty, QtyB: b.Qty, OrdersA: a.Orders, OrdersB: b.Orders,
            PriceA: SalesMetrics.Ratio(a.Sales, a.Qty),
            PriceB: SalesMetrics.Ratio(b.Sales, b.Qty),
            ShareA: SalesMetrics.Ratio(a.Sales, totalA),
            ShareB: SalesMetrics.Ratio(b.Sales, totalB),
            Members: members);
    }
}
