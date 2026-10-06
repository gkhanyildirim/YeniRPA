namespace YeniRPA.Web.Services.SalesAnalysis.Providers;

public sealed record DimensionData(
    IReadOnlyList<DimensionRow> Rows,
    IReadOnlyList<DimensionRow> Gainers,
    IReadOnlyList<DimensionRow> Losers,
    Concentration? Concentration,
    int DistinctCount);

/// <summary>
/// The shared shape of the category / brand / seller / city breakdowns: A vs B per value, its
/// contribution to the total change, the top gainers and losers, and optionally the top-5
/// concentration and the "Diğer" fold. A concrete provider only names the field and the wording.
/// </summary>
public abstract class DimensionProvider : IInsightProvider
{
    const int TopMovers = 10;
    const int MaxRows = 500;

    public abstract string Key { get; }
    public abstract string Title { get; }

    /// <summary>The ablative noun used in sentences: "kategorisinden", "markasından", …</summary>
    protected abstract string FromNoun { get; }

    protected abstract string KeyOf(SalesLine line);

    protected virtual string LabelOf(string key) => SalesTextTr.OrUnknown(key);

    /// <summary>Whether small values are folded into "Diğer" (only the 257-value category list needs it).</summary>
    protected virtual bool FoldSmall => false;

    protected virtual bool ReportConcentration => false;

    /// <summary>"En büyük 5 kategorinin" — the subject of the concentration sentence.</summary>
    protected virtual string TopFiveGenitive => "En büyük 5 değerin";

    protected virtual int FindingCount => 2;

    public InsightResult Compute(SalesAnalysisContext ctx)
    {
        var rows = DimensionBreakdown.Build(ctx.SalesA, ctx.SalesB, KeyOf, LabelOf);
        var delta = ctx.SalesDelta;

        var gainers = rows.Where(r => r.Delta > 0).OrderByDescending(r => r.Delta).Take(TopMovers).ToList();
        var losers = rows.Where(r => r.Delta < 0).OrderBy(r => r.Delta).Take(TopMovers).ToList();
        // With five values or fewer (a narrow filter) "the top 5 hold 100%" says nothing.
        var concentration = ReportConcentration && rows.Count > 5 ? DimensionBreakdown.TopShare(rows, 5) : null;

        var tableRows = FoldSmall ? DimensionBreakdown.GroupSmall(rows, ctx.Options.OtherThresholdPct) : rows;
        tableRows = DimensionBreakdown.FoldTail(tableRows, MaxRows);

        var findings = DimensionBreakdown.DriverFindings(rows, delta, FromNoun, Key, FindingCount).ToList();
        var summary = findings.Select(f => f.Text).ToList();

        if (concentration is { ShareA: { } shareA, ShareB: { } shareB })
        {
            var moved = Math.Abs(shareB - shareA) < 0.0005
                ? "değişmedi"
                : $"{SalesTextTr.Number(Math.Abs(shareB - shareA) * 100, 1)} puan {(shareB > shareA ? "arttı" : "azaldı")}";
            summary.Add($"{TopFiveGenitive} toplam satıştaki payı Dönem A'da {SalesTextTr.Pct(shareA)}, " +
                        $"Dönem B'de {SalesTextTr.Pct(shareB)} ({moved}).");
        }

        return new InsightResult(Key, Title, summary, findings,
            new DimensionData(tableRows, gainers, losers, concentration, rows.Count));
    }
}

/// <summary>(C) Category breakdown. Labels are shown in Title Case; the raw label stays the key.</summary>
public sealed class CategoryProvider : DimensionProvider
{
    public override string Key => "category";
    public override string Title => "Kategori analizi";
    protected override string FromNoun => "kategorisinden";
    protected override string KeyOf(SalesLine line) => line.CategoryLabel;
    protected override string LabelOf(string key) => SalesTextTr.TitleCase(key);
    protected override bool FoldSmall => true;
    protected override bool ReportConcentration => true;
    protected override string TopFiveGenitive => "En büyük 5 kategorinin";
}

/// <summary>(E) Brand breakdown with top-5 concentration.</summary>
public sealed class BrandProvider : DimensionProvider
{
    public override string Key => "brand";
    public override string Title => "Marka analizi";
    protected override string FromNoun => "markasından";
    protected override string KeyOf(SalesLine line) => line.Brand.ToUpperInvariant();
    protected override string LabelOf(string key) => SalesTextTr.TitleCase(key);
    protected override bool ReportConcentration => true;
    protected override string TopFiveGenitive => "En büyük 5 markanın";
    protected override int FindingCount => 1;
}

/// <summary>(E) Seller breakdown with top-5 concentration.</summary>
public sealed class SellerProvider : DimensionProvider
{
    public override string Key => "seller";
    public override string Title => "Satıcı analizi";
    protected override string FromNoun => "satıcısından";
    protected override string KeyOf(SalesLine line) => line.Seller;
    protected override bool ReportConcentration => true;
    protected override string TopFiveGenitive => "En büyük 5 satıcının";
    protected override int FindingCount => 1;
}

/// <summary>(H) City breakdown; the page shows the top 10.</summary>
public sealed class CityProvider : DimensionProvider
{
    public override string Key => "city";
    public override string Title => "Şehir analizi";
    protected override string FromNoun => "şehrinden";
    protected override string KeyOf(SalesLine line) => SalesTextTr.Tr.TextInfo.ToTitleCase(line.City.Trim().ToLower(SalesTextTr.Tr));
    protected override int FindingCount => 1;
}
