namespace YeniRPA.Web.Services.SalesAnalysis;

public sealed record Reason(string Text, double Impact, string Source);

public sealed record ReasonsData(string Headline, IReadOnlyList<Reason> Reasons);

/// <summary>
/// (K) The "Sebepler" panel: a headline sentence for the overall move, then the 3–5 findings with
/// the largest effect in lira across every provider. Purely rule-based — the sentences are the
/// providers' own, built from numbers; nothing here calls an LLM. A future optional summariser could
/// take <see cref="ReasonsData"/> as its input without the providers changing.
/// </summary>
public static class ReasonsComposer
{
    public const string Key = "reasons";
    const int MaxReasons = 5;

    /// <summary>At most this many reasons from one provider, so one breakdown cannot fill the panel.</summary>
    const int MaxPerSource = 2;

    public static InsightResult Compose(SalesAnalysisContext ctx, IEnumerable<InsightResult> results)
    {
        var a = ctx.MetricsA;
        var b = ctx.MetricsB;
        var headline = Headline(a, b);

        var reasons = results
            .SelectMany(r => r.Findings)
            .Where(f => f.Impact != 0 && double.IsFinite(f.Impact))
            .OrderByDescending(f => Math.Abs(f.Impact))
            .GroupBy(f => f.Source)
            .SelectMany(g => g.Take(MaxPerSource))
            .OrderByDescending(f => Math.Abs(f.Impact))
            .Take(MaxReasons)
            .Select(f => new Reason(f.Text, f.Impact, f.Source))
            .ToList();

        return new InsightResult(Key, "Sebepler", [headline], [], new ReasonsData(headline, reasons));
    }

    /// <summary>
    /// "Satışlar %30,1 düştü: 26,4 milyon TL'den 18,5 milyon TL'ye indi. Sipariş sayısı %27,7 azaldı,
    /// sipariş başına ortalama tutar ise %3,4 düştü."
    /// </summary>
    internal static string Headline(PeriodMetrics a, PeriodMetrics b)
    {
        if (a.GrossSales == 0 && b.GrossSales == 0)
            return "Seçilen iki dönemde de satış yok.";
        if (a.GrossSales == 0)
            return $"Dönem A'da hiç satış yok; Dönem B'de {SalesTextTr.Tl(b.GrossSales)} satış yapıldı.";

        var change = (b.GrossSales - a.GrossSales) / a.GrossSales;
        var text = change == 0
            ? $"Satışlar değişmedi ({SalesTextTr.Tl(b.GrossSales)})."
            : $"Satışlar {SalesTextTr.Pct(change)} {(change > 0 ? "arttı" : "düştü")}: {SalesTextTr.Tl(a.GrossSales)}'den " +
              $"{SalesTextTr.Tl(b.GrossSales)}'ye {(change > 0 ? "çıktı" : "indi")}.";

        var parts = new List<string>();
        if (a.Orders > 0)
        {
            var orders = (double)(b.Orders - a.Orders) / a.Orders;
            parts.Add($"Sipariş sayısı {SalesTextTr.Pct(orders)} {SalesTextTr.Moved(orders)}");
        }
        if (a.AverageOrderValue is { } aovA && b.AverageOrderValue is { } aovB && aovA != 0)
        {
            var aov = (aovB - aovA) / aovA;
            parts.Add($"{(parts.Count > 0 ? "sipariş başına ortalama tutar ise" : "Sipariş başına ortalama tutar")} " +
                      $"{SalesTextTr.Pct(aov)} {SalesTextTr.Rose(aov)}");
        }
        return parts.Count == 0 ? text : text + " " + string.Join(", ", parts) + ".";
    }
}
