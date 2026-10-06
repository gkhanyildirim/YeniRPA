namespace YeniRPA.Web.Services.SalesAnalysis;

/// <summary>One item's share of the price–volume–mix split. <c>Volume + Price + Mix == SalesB − SalesA</c>.</summary>
public sealed record PvmItem(
    string Key,
    double QtyA,
    double QtyB,
    double SalesA,
    double SalesB,
    double? PriceA,
    double? PriceB,
    double Volume,
    double Price,
    double Mix)
{
    public double Delta => SalesB - SalesA;
    public bool IsNew => QtyA == 0 && SalesA == 0 && (QtyB != 0 || SalesB != 0);
    public bool IsLost => QtyB == 0 && SalesB == 0 && (QtyA != 0 || SalesA != 0);
}

public sealed record PvmResult(
    double SalesA,
    double SalesB,
    double Delta,
    double Volume,
    double Price,
    double Mix,
    double MixFromNewAndLost,
    double QtyA,
    double QtyB,
    double? AvgPriceA,
    double? AvgPriceB,
    double? AvgPriceDelta,
    double? AvgPriceFromPrice,
    double? AvgPriceFromMix,
    IReadOnlyList<PvmItem> Items);

/// <summary>
/// Splits the change in sales between two periods into volume, price and mix — exactly, with no
/// residual term.
///
/// <para>With P̄A = SalesA / QtyA (the period-A average price; 0 when A sold nothing):</para>
/// <list type="bullet">
///   <item><b>Volume</b> = (QtyB − QtyA) · P̄A — the change had every unit sold at A's average price.</item>
///   <item><b>Price</b> = Σ over items sold in both periods of qB · (pB − pA) — the same item now
///   sells for a different price.</item>
///   <item><b>Mix</b> — for an item sold in both periods (qB − qA) · (pA − P̄A): volume moving toward
///   items priced above or below the average. For an item new in B, qB · (pB − P̄A); for an item
///   lost from A, −qA · (pA − P̄A).</item>
/// </list>
/// <para>Per item these three add up to sB − sA identically, so the totals add up to the change in
/// sales identically. An item with sales but zero quantity (a data anomaly) has no price; its whole
/// change beyond the volume term lands in mix, which keeps the identity intact.</para>
///
/// <para>The average-price change splits the same way: P̄B − P̄A = Price / QtyB + Mix / QtyB, which
/// is what answers "did prices actually rise, or did we just sell more expensive things?".</para>
/// </summary>
public static class PvmDecomposition
{
    public static PvmResult Compute(
        IEnumerable<SalesLine> periodA, IEnumerable<SalesLine> periodB, Func<SalesLine, string> key)
    {
        var a = Aggregate(periodA, key);
        var b = Aggregate(periodB, key);

        var salesA = a.Values.Sum(x => x.Sales);
        var salesB = b.Values.Sum(x => x.Sales);
        var qtyA = a.Values.Sum(x => x.Qty);
        var qtyB = b.Values.Sum(x => x.Qty);
        var avgA = SalesMetrics.Ratio(salesA, qtyA);
        var avgB = SalesMetrics.Ratio(salesB, qtyB);
        var basePrice = avgA ?? 0;

        var items = new List<PvmItem>(a.Count + b.Count);
        foreach (var k in a.Keys.Union(b.Keys, StringComparer.Ordinal))
        {
            var (qA, sA) = a.TryGetValue(k, out var va) ? va : (0, 0);
            var (qB, sB) = b.TryGetValue(k, out var vb) ? vb : (0, 0);
            var pA = qA > 0 ? sA / qA : (double?)null;
            var pB = qB > 0 ? sB / qB : (double?)null;

            var volume = (qB - qA) * basePrice;
            double price, mix;
            if (pA is not null && pB is not null)
            {
                price = sB - qB * pA.Value;
                mix = (qB - qA) * (pA.Value - basePrice);
            }
            else
            {
                price = 0;
                mix = (sB - sA) - volume;
            }

            items.Add(new PvmItem(k, qA, qB, sA, sB, pA, pB, volume, price, mix));
        }

        var volumeTotal = items.Sum(i => i.Volume);
        var priceTotal = items.Sum(i => i.Price);
        var mixTotal = items.Sum(i => i.Mix);
        var newLostMix = items.Where(i => i.PriceA is null || i.PriceB is null).Sum(i => i.Mix);

        // The split of the average-price move only exists when both periods sold something.
        double? fromPrice = null, fromMix = null;
        if (avgA is not null && avgB is not null && qtyB > 0)
        {
            fromPrice = priceTotal / qtyB;
            fromMix = mixTotal / qtyB;
        }

        return new PvmResult(
            SalesA: salesA,
            SalesB: salesB,
            Delta: salesB - salesA,
            Volume: volumeTotal,
            Price: priceTotal,
            Mix: mixTotal,
            MixFromNewAndLost: newLostMix,
            QtyA: qtyA,
            QtyB: qtyB,
            AvgPriceA: avgA,
            AvgPriceB: avgB,
            AvgPriceDelta: avgA is not null && avgB is not null ? avgB - avgA : null,
            AvgPriceFromPrice: fromPrice,
            AvgPriceFromMix: fromMix,
            Items: items);
    }

    static Dictionary<string, (double Qty, double Sales)> Aggregate(IEnumerable<SalesLine> lines, Func<SalesLine, string> key)
    {
        var result = new Dictionary<string, (double Qty, double Sales)>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var k = key(line);
            var current = result.TryGetValue(k, out var v) ? v : (0, 0);
            result[k] = (current.Qty + line.Quantity, current.Sales + line.Amount);
        }
        return result;
    }
}
