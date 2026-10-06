namespace YeniRPA.Web.Services.SalesAnalysis.Providers;

public sealed record PvmData(
    double SalesA,
    double SalesB,
    double Delta,
    double Volume,
    double Price,
    double Mix,
    double MixFromNewAndLost,
    double? AvgPriceA,
    double? AvgPriceB,
    double? AvgPriceDelta,
    double? AvgPriceFromPrice,
    double? AvgPriceFromMix,
    IReadOnlyList<PvmItemRow> TopItems);

public sealed record PvmItemRow(string Key, string Label, double Delta, double Volume, double Price, double Mix);

/// <summary>
/// (B) Price–volume–mix at product (Product SKU) level — see <see cref="PvmDecomposition"/> for the
/// formulas. The three effects add up to the change in gross sales exactly.
/// </summary>
public sealed class PvmProvider : IInsightProvider
{
    const int TopItemCount = 30;

    public string Key => "pvm";
    public string Title => "Satış farkı nereden geliyor?";

    public InsightResult Compute(SalesAnalysisContext ctx)
    {
        var pvm = PvmDecomposition.Compute(ctx.SalesA, ctx.SalesB, l => l.ProductKey);
        var titles = ProductTitles(ctx);

        var top = pvm.Items
            .OrderByDescending(i => Math.Abs(i.Delta))
            .Take(TopItemCount)
            .Select(i => new PvmItemRow(i.Key, titles.GetValueOrDefault(i.Key, i.Key), i.Delta, i.Volume, i.Price, i.Mix))
            .ToList();

        var summary = new List<string>();
        var findings = new List<Finding>();

        static string Signed(double v) => (v > 0 ? "+" : v < 0 ? "−" : "") + SalesTextTr.Tl(v);

        if (pvm.Delta != 0)
        {
            summary.Add($"Satışlar toplam {SalesTextTr.Tl(pvm.Delta)} {SalesTextTr.Moved(pvm.Delta)}. " +
                        $"Bu farkın kaynakları: satılan adet {Signed(pvm.Volume)}, ürün dağılımı {Signed(pvm.Mix)}, " +
                        $"fiyat değişimi {Signed(pvm.Price)}.");
        }

        if (pvm.QtyA > 0 && pvm.Volume != 0)
        {
            var qtyPct = (pvm.QtyB - pvm.QtyA) / pvm.QtyA;
            findings.Add(new Finding(
                pvm.Volume < 0
                    ? $"Daha az ürün satıldı: satılan adet {SalesTextTr.Pct(qtyPct)} azaldı. Bu tek başına satışları {SalesTextTr.Tl(pvm.Volume)} düşürdü."
                    : $"Daha çok ürün satıldı: satılan adet {SalesTextTr.Pct(qtyPct)} arttı. Bu tek başına satışları {SalesTextTr.Tl(pvm.Volume)} artırdı.",
                pvm.Volume, Key));
        }

        // With nothing sold in A there is no earlier product mix to shift away from; the whole change
        // is "new sales", which the headline already says.
        if (pvm.Mix != 0 && pvm.QtyA > 0)
        {
            var text = pvm.Mix < 0
                ? $"Müşteriler daha ucuz ürünlere yöneldi. Ürün dağılımındaki bu değişim satışları {SalesTextTr.Tl(pvm.Mix)} düşürdü."
                : $"Müşteriler daha pahalı ürünlere yöneldi. Ürün dağılımındaki bu değişim satışları {SalesTextTr.Tl(pvm.Mix)} artırdı.";
            if (Math.Abs(pvm.MixFromNewAndLost) >= Math.Abs(pvm.Mix) * 0.1)
            {
                text += $" Bu etkinin {SalesTextTr.Tl(pvm.MixFromNewAndLost)}'lik kısmı, yalnızca bir dönemde satılan ürünlerden geliyor.";
            }
            findings.Add(new Finding(text, pvm.Mix, Key));
        }

        if (pvm.Price != 0)
        {
            findings.Add(new Finding(
                pvm.Price > 0
                    ? $"Aynı ürünler ortalamada daha pahalıya satıldı. Fiyat artışı satışlara {SalesTextTr.Tl(pvm.Price)} ekledi."
                    : $"Aynı ürünler ortalamada daha ucuza satıldı. Fiyat düşüşü satışları {SalesTextTr.Tl(pvm.Price)} azalttı.",
                pvm.Price, Key));
        }

        if (pvm.AvgPriceDelta is { } avgDelta && avgDelta != 0 && pvm.AvgPriceA is > 0 &&
            pvm.AvgPriceFromMix is { } fromMix && pvm.AvgPriceFromPrice is { } fromPrice)
        {
            var mixDriven = Math.Abs(fromMix) >= Math.Abs(fromPrice);
            var reason = mixDriven
                ? (fromMix < 0
                    ? "Bunun asıl nedeni fiyatların değişmesi değil, daha ucuz ürünlerin daha çok satılması."
                    : "Bunun asıl nedeni fiyatların değişmesi değil, daha pahalı ürünlerin daha çok satılması.")
                : (fromPrice < 0
                    ? "Bunun asıl nedeni aynı ürünlerin daha ucuza satılması."
                    : "Bunun asıl nedeni aynı ürünlerin daha pahalıya satılması.");
            summary.Add($"Ürün başına ortalama fiyat {SalesTextTr.Pct(avgDelta / pvm.AvgPriceA.Value)} {SalesTextTr.Rose(avgDelta)} " +
                        $"({SalesTextTr.Number(pvm.AvgPriceA.Value)} TL'den {SalesTextTr.Number(pvm.AvgPriceB ?? 0)} TL'ye). {reason} " +
                        $"Ürün başına etki: ürün dağılımından {Signed(fromMix)}, fiyattan {Signed(fromPrice)}.");
        }

        var data = new PvmData(
            pvm.SalesA, pvm.SalesB, pvm.Delta, pvm.Volume, pvm.Price, pvm.Mix, pvm.MixFromNewAndLost,
            pvm.AvgPriceA, pvm.AvgPriceB, pvm.AvgPriceDelta, pvm.AvgPriceFromPrice, pvm.AvgPriceFromMix, top);

        return new InsightResult(Key, Title, summary, findings, data);
    }

    internal static Dictionary<string, string> ProductTitles(SalesAnalysisContext ctx) =>
        ctx.AllA.Concat(ctx.AllB)
            .GroupBy(l => l.ProductKey, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(l => l.Title).FirstOrDefault(t => t.Length > 0) ?? g.Key,
                StringComparer.Ordinal);
}
