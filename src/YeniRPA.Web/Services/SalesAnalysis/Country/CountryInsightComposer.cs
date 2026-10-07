using System.Globalization;

namespace YeniRPA.Web.Services.SalesAnalysis.Country;

public sealed record CountryInsightInput(
    CountrySide A,
    CountrySide B,
    bool MoneyComparable,
    IReadOnlyList<CountryKpi> Kpis,
    CountryDimension Category,
    CountryDimension Brand,
    CountryProducts Products,
    CountryBehaviour Behaviour);

public sealed record CountryInsightResult(
    IReadOnlyList<CountryInsight> Insights,
    IReadOnlyList<string> StrengthsA,
    IReadOnlyList<string> StrengthsB);

/// <summary>
/// Turns the computed comparison into the "Öne Çıkan İçgörüler" list and the per-country strength
/// lists. Every sentence is assembled from numbers already in the response — nothing is inferred
/// that the files do not show. A difference is only mentioned when it is material (at least 5%
/// relative, or 1–3 share points depending on the metric), and a metric that cannot be compared is
/// never mentioned at all.
///
/// <para>Country names are never given a Turkish case suffix ("…'da", "…'den"): the operator types
/// them freely, and a wrongly harmonised suffix reads worse than a construction that needs none.</para>
/// </summary>
public static class CountryInsightComposer
{
    public const int MaxInsights = 8;

    /// <summary>A relative gap under this is not called a difference.</summary>
    public const double MinRelativeGap = 0.05;

    /// <summary>A category/brand share gap under this (3 points) does not make the highlights.</summary>
    public const double MinHighlightShareGap = 0.03;

    static readonly HashSet<string> Primary = ["gross", "orders", "dailySales", "dailyOrders", "transferred"];

    public static CountryInsightResult Compose(CountryInsightInput input)
    {
        var nameA = input.A.Name;
        var nameB = input.B.Name;
        var insights = new List<CountryInsight>();
        var strengthsA = new List<string>();
        var strengthsB = new List<string>();
        void Strength(string side, string text) => (side == "a" ? strengthsA : strengthsB).Add(text);
        string NameOf(string side) => side == "a" ? nameA : nameB;

        // ----- headline metrics -----
        // With windows of different length the totals are not comparable; the daily averages are.
        string[] keys = input.A.Days == input.B.Days
            ? ["gross", "orders", "units", "transferred", "buyers", "aov", "avgPrice", "unitsPerOrder", "ordersPerBuyer", "multiItemShare", "commissionRate"]
            : ["dailySales", "dailyOrders", "aov", "avgPrice", "unitsPerOrder", "ordersPerBuyer", "multiItemShare", "commissionRate"];

        foreach (var key in keys)
        {
            var k = input.Kpis.FirstOrDefault(x => x.Key == key);
            if (k is not { Comparable: true, A: { } a, B: { } b } || k.Higher is null or "tie")
                continue;

            var rate = k.Kind == "rate";
            double score;
            string gapText;
            if (rate)
            {
                var gap = Math.Abs(a - b);
                if (gap < (key == "multiItemShare" ? 0.02 : 0.01))
                    continue;
                score = gap * 10;
                gapText = $"{SalesTextTr.Number(gap * 100, 1)} puan fark";
            }
            else
            {
                var lo = Math.Min(Math.Abs(a), Math.Abs(b));
                if (lo <= 0)
                    continue;
                var rel = (Math.Max(Math.Abs(a), Math.Abs(b)) - lo) / lo;
                if (rel < MinRelativeGap)
                    continue;
                score = Math.Min(rel, 3);
                gapText = $"{SalesTextTr.Pct(rel)} {(k.GoodWhen == "down" ? "daha düşük" : "daha yüksek")}";
            }
            if (Primary.Contains(key))
                score += 0.3;

            var values = $"{nameA} {Format(k, a, input.A)} · {nameB} {Format(k, b, input.B)}";
            string text;
            if (k.Leader is "a" or "b")
            {
                var winner = NameOf(k.Leader);
                text = $"{k.Label}: {winner} tarafında daha yüksek — {values} ({gapText}).";
                var winnerValue = k.Leader == "a" ? Format(k, a, input.A) : Format(k, b, input.B);
                var loserValue = k.Leader == "a" ? Format(k, b, input.B) : Format(k, a, input.A);
                Strength(k.Leader, $"{k.Label}: {winnerValue} (karşı ülke: {loserValue})");
            }
            else
            {
                text = $"{k.Label}: {NameOf(k.Higher)} tarafında daha yüksek — {values} ({gapText}).";
            }
            insights.Add(new CountryInsight(text, k.Leader is "a" or "b" ? k.Leader : null, "kpi", score));
        }

        // ----- category and brand strengths -----
        DimensionHighlights(input.Category, "kategorisinin", "kategori", 3, 3);
        DimensionHighlights(input.Brand, "markasının", "marka", 1, 2);

        void DimensionHighlights(CountryDimension dim, string genitive, string noun, int insightCount, int strengthCount)
        {
            if (!dim.Available)
                return;
            // When the two files name their categories differently, a value missing on one side says
            // nothing about demand there — only values sold in both countries are compared.
            bool Eligible(CountryDimensionRow r) => dim.Note is null || (r.OrdersA > 0 && r.OrdersB > 0);
            var strongA = dim.StrongA.Where(Eligible).ToList();
            var strongB = dim.StrongB.Where(Eligible).ToList();

            foreach (var r in strongA.Take(strengthCount))
                Strength("a", $"{r.Label} {noun} payı: {SalesTextTr.Pct(r.ShareA ?? 0)} (karşı ülke: {SalesTextTr.Pct(r.ShareB ?? 0)})");
            foreach (var r in strongB.Take(strengthCount))
                Strength("b", $"{r.Label} {noun} payı: {SalesTextTr.Pct(r.ShareB ?? 0)} (karşı ülke: {SalesTextTr.Pct(r.ShareA ?? 0)})");

            foreach (var r in strongA.Concat(strongB).Where(r => Math.Abs(r.Gap ?? 0) >= MinHighlightShareGap)
                         .OrderByDescending(r => Math.Abs(r.Gap ?? 0)).Take(insightCount))
            {
                var side = r.Gap > 0 ? "a" : "b";
                var loserShare = side == "a" ? r.ShareB : r.ShareA;
                var text = $"{r.Label} {genitive} satış payı: {nameA} {SalesTextTr.Pct(r.ShareA ?? 0)} · {nameB} {SalesTextTr.Pct(r.ShareB ?? 0)} " +
                           $"({SalesTextTr.Number(Math.Abs(r.Gap ?? 0) * 100, 1)} puan fark). " +
                           (loserShare is null or 0
                               ? $"Bu {noun} yalnızca {NameOf(side)} tarafında satılıyor."
                               : $"Bu {noun} {NameOf(side)} tarafında öne çıkıyor.");
                insights.Add(new CountryInsight(text, side, noun, Math.Abs(r.Gap ?? 0) * 5));
            }
        }

        if (input.Category is { Available: true, Top5ShareA: { } t5a, Top5ShareB: { } t5b } && Math.Abs(t5a - t5b) >= 0.10)
        {
            var concentrated = t5a > t5b ? nameA : nameB;
            insights.Add(new CountryInsight(
                $"{concentrated} tarafında satışlar daha az kategoride toplanıyor: en büyük 5 kategorinin payı {nameA} {SalesTextTr.Pct(t5a)} · {nameB} {SalesTextTr.Pct(t5b)}.",
                null, "category", Math.Abs(t5a - t5b) * 3));
        }

        // ----- behaviour and timing -----
        var behaviour = input.Behaviour;
        if (behaviour.WeekdaysComparable)
        {
            var dayA = CountryComparisonService.Peak(behaviour.Weekdays, true);
            var dayB = CountryComparisonService.Peak(behaviour.Weekdays, false);
            if (dayA is not null && dayB is not null && dayA.Key != dayB.Key)
                insights.Add(new CountryInsight(
                    $"En yoğun sipariş günü farklı: {nameA} {dayA.Label} ({SalesTextTr.Pct(dayA.ShareA ?? 0)}) · {nameB} {dayB.Label} ({SalesTextTr.Pct(dayB.ShareB ?? 0)}).",
                    null, "time", 0.15));
        }

        var hourA = CountryComparisonService.Peak(behaviour.Hours, true);
        var hourB = CountryComparisonService.Peak(behaviour.Hours, false);
        if (hourA is not null && hourB is not null && Math.Abs(int.Parse(hourA.Key, CultureInfo.InvariantCulture) - int.Parse(hourB.Key, CultureInfo.InvariantCulture)) >= 2)
            insights.Add(new CountryInsight(
                $"En yoğun sipariş saati farklı: {nameA} {hourA.Label} ({SalesTextTr.Pct(hourA.ShareA ?? 0)}) · {nameB} {hourB.Label} ({SalesTextTr.Pct(hourB.ShareB ?? 0)}).",
                null, "time", 0.12));

        if (behaviour is { TrendA: { } ta, TrendB: { } tb } &&
            ((Math.Sign(ta) != Math.Sign(tb) && Math.Abs(ta) >= 0.05 && Math.Abs(tb) >= 0.05) || Math.Abs(ta - tb) >= 0.15))
        {
            insights.Add(new CountryInsight(
                $"Dönem içinde sipariş eğilimi farklı: {nameA} {Trend(ta)} · {nameB} {Trend(tb)} (ikinci yarı, ilk yarıya göre günlük ortalama).",
                ta > tb ? "a" : "b", "trend", Math.Min(Math.Abs(ta - tb), 2)));
        }

        // ----- product overlap -----
        if (input.Products is { Common: > 0, CommonShareA: { } ca, CommonShareB: { } cb })
            insights.Add(new CountryInsight(
                $"{SalesTextTr.Number(input.Products.Common)} ürün iki ülkede de satılıyor; satışlardaki payı {nameA} tarafında {SalesTextTr.Pct(ca)}, {nameB} tarafında {SalesTextTr.Pct(cb)}.",
                null, "product", 0.05));

        return new CountryInsightResult(
            [.. insights.OrderByDescending(i => i.Score).Take(MaxInsights)],
            strengthsA,
            strengthsB);
    }

    static string Trend(double t) =>
        Math.Abs(t) < 0.005 ? "yatay" : $"{SalesTextTr.Pct(t)} {(t > 0 ? "artış" : "düşüş")}";

    static string Format(CountryKpi k, double value, CountrySide side) => k.Kind switch
    {
        "money" => SalesTextTr.Money(value, side.DisplayCurrency),
        "rate" => SalesTextTr.Pct(value),
        "number" => SalesTextTr.Number(value, 2),
        _ => SalesTextTr.Number(value),
    };
}
