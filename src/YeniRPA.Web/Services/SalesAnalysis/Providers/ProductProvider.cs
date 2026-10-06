namespace YeniRPA.Web.Services.SalesAnalysis.Providers;

public sealed record ProductRow(
    string Sku,
    string Title,
    string Brand,
    string Category,
    double QtyA,
    double QtyB,
    double SalesA,
    double SalesB,
    double Delta,
    double? Pct,
    double? PriceA,
    double? PriceB,
    double? PricePct,
    double PriceEffect);

public sealed record ProductGroupTotal(int Count, double Sales, double Qty);

public sealed record ProductData(
    IReadOnlyList<ProductRow> TopByUnits,
    IReadOnlyList<ProductRow> TopBySales,
    IReadOnlyList<ProductRow> Gainers,
    IReadOnlyList<ProductRow> Losers,
    IReadOnlyList<ProductRow> NewProducts,
    ProductGroupTotal NewTotal,
    IReadOnlyList<ProductRow> LostProducts,
    ProductGroupTotal LostTotal,
    IReadOnlyList<ProductRow> PriceChanged,
    int DistinctProducts);

/// <summary>
/// (D) Product view, keyed by Product SKU and labelled with the localized title: best sellers by
/// units and by sales, biggest movers, products new in B or gone since A, and same-SKU price moves.
/// </summary>
public sealed class ProductProvider : IInsightProvider
{
    const int TopCount = 20;
    const int ListCount = 50;

    /// <summary>A same-SKU price move smaller than this is noise (rounding, a coupon), not a price change.</summary>
    const double MinPriceMove = 0.01;

    public string Key => "product";
    public string Title => "Ürün analizi";

    public InsightResult Compute(SalesAnalysisContext ctx)
    {
        var meta = ctx.AllA.Concat(ctx.AllB)
            .GroupBy(l => l.ProductKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (
                Title: g.Select(l => l.Title).FirstOrDefault(t => t.Length > 0) ?? g.Key,
                Brand: g.Select(l => l.Brand).FirstOrDefault(t => t.Length > 0) ?? "",
                Category: SalesTextTr.TitleCase(g.Select(l => l.CategoryLabel).FirstOrDefault(t => t.Length > 0) ?? "")),
                StringComparer.Ordinal);

        var pvm = PvmDecomposition.Compute(ctx.SalesA, ctx.SalesB, l => l.ProductKey);
        var rows = pvm.Items.Select(i =>
        {
            var m = meta.TryGetValue(i.Key, out var found) ? found : (Title: i.Key, Brand: "", Category: "");
            double? pricePct = i.PriceA is > 0 && i.PriceB is not null ? (i.PriceB - i.PriceA) / i.PriceA : null;
            return new ProductRow(
                i.Key, m.Title, m.Brand, m.Category, i.QtyA, i.QtyB, i.SalesA, i.SalesB, i.Delta,
                i.SalesA != 0 ? i.Delta / Math.Abs(i.SalesA) : null,
                i.PriceA, i.PriceB, pricePct, i.Price);
        }).ToList();

        var isNew = pvm.Items.Where(i => i.IsNew).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var isLost = pvm.Items.Where(i => i.IsLost).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var newRows = rows.Where(r => isNew.Contains(r.Sku)).OrderByDescending(r => r.SalesB).ToList();
        var lostRows = rows.Where(r => isLost.Contains(r.Sku)).OrderByDescending(r => r.SalesA).ToList();

        var data = new ProductData(
            TopByUnits: [.. rows.Where(r => r.QtyB > 0).OrderByDescending(r => r.QtyB).ThenByDescending(r => r.SalesB).Take(TopCount)],
            TopBySales: [.. rows.Where(r => r.SalesB > 0).OrderByDescending(r => r.SalesB).Take(TopCount)],
            Gainers: [.. rows.Where(r => r.Delta > 0).OrderByDescending(r => r.Delta).Take(TopCount)],
            Losers: [.. rows.Where(r => r.Delta < 0).OrderBy(r => r.Delta).Take(TopCount)],
            NewProducts: [.. newRows.Take(ListCount)],
            NewTotal: new ProductGroupTotal(newRows.Count, newRows.Sum(r => r.SalesB), newRows.Sum(r => r.QtyB)),
            LostProducts: [.. lostRows.Take(ListCount)],
            LostTotal: new ProductGroupTotal(lostRows.Count, lostRows.Sum(r => r.SalesA), lostRows.Sum(r => r.QtyA)),
            PriceChanged: [.. rows.Where(r => r.PricePct is { } p && Math.Abs(p) >= MinPriceMove)
                .OrderByDescending(r => Math.Abs(r.PriceEffect)).Take(ListCount)],
            DistinctProducts: rows.Count);

        var findings = new List<Finding>();
        var delta = ctx.SalesDelta;

        var top = DimensionBreakdown.Drivers(
            rows.Select(r => new DimensionRow(r.Sku, r.Title, r.SalesA, r.SalesB, r.Delta, r.Pct,
                SalesMetrics.Ratio(r.Delta, delta), r.QtyA, r.QtyB, 0, 0, r.PriceA, r.PriceB, null, null)),
            delta).FirstOrDefault();
        if (top is not null && delta != 0)
        {
            var share = top.Contribution ?? 0;
            findings.Add(new Finding(
                $"Farkı en çok etkileyen ürün \"{Short(top.Label)}\" oldu: satışı {SalesTextTr.Tl(top.Delta)} " +
                $"{SalesTextTr.Moved(top.Delta)}" +
                (share is > 0 and <= 1 ? $"; bu, toplam farkın {SalesTextTr.PctOf(share)}." : "."),
                top.Delta, Key));
        }

        if (data.NewTotal.Count > 0 || data.LostTotal.Count > 0)
        {
            var net = data.NewTotal.Sales - data.LostTotal.Sales;
            findings.Add(new Finding(
                $"Dönem A'da hiç satılmayan {SalesTextTr.Number(data.NewTotal.Count)} ürün, Dönem B'de " +
                $"{SalesTextTr.Tl(data.NewTotal.Sales)} satış yaptı. Buna karşılık Dönem A'da satılan " +
                $"{SalesTextTr.Number(data.LostTotal.Count)} ürün Dönem B'de hiç satılmadı; bu ürünler Dönem A'da " +
                $"{SalesTextTr.Tl(data.LostTotal.Sales)} satış yapmıştı. Ürün yelpazesindeki bu değişim satışları net " +
                $"{SalesTextTr.Tl(net)} {(net >= 0 ? "artırdı" : "düşürdü")}.",
                net, Key));
        }

        return new InsightResult(Key, Title, [.. findings.Select(f => f.Text)], findings, data);
    }

    static string Short(string title) => title.Length <= 60 ? title : title[..57] + "…";
}
