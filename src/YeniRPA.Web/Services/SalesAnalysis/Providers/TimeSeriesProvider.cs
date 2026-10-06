namespace YeniRPA.Web.Services.SalesAnalysis.Providers;

/// <summary>One bucket of the overlaid series. <see cref="Index"/> aligns A and B: day 0 of A sits on day 0 of B.</summary>
public sealed record SeriesPoint(int Index, string? LabelA, string? LabelB, double SalesA, double SalesB, int OrdersA, int OrdersB);

public sealed record DayExtreme(string Date, double Sales, int Orders);

public sealed record TimeSeriesData(
    string Granularity,
    IReadOnlyList<SeriesPoint> Points,
    DayExtreme? PeakB,
    DayExtreme? LowB,
    DayExtreme? PeakA,
    DayExtreme? LowA,
    double[][] WeekdayHourB,
    double[][] WeekdayHourA);

/// <summary>
/// (G) Sales over time, both periods overlaid on a shared day (or hour) index, the best and worst
/// day of each period, and a weekday × hour heat grid.
/// </summary>
public sealed class TimeSeriesProvider : IInsightProvider
{
    /// <summary>Periods up to this many days are drawn hour by hour; longer ones day by day.</summary>
    const int HourlyUpToDays = 3;

    public string Key => "time";
    public string Title => "Zaman serisi";

    public InsightResult Compute(SalesAnalysisContext ctx)
    {
        var o = ctx.Options;
        var daysA = o.ATo.DayNumber - o.AFrom.DayNumber + 1;
        var daysB = o.BTo.DayNumber - o.BFrom.DayNumber + 1;
        var hourly = Math.Max(daysA, daysB) <= HourlyUpToDays;
        var granularity = hourly ? "hour" : "day";

        int Bucket(SalesLine l, DateOnly from) => hourly
            ? (int)(l.Created - from.ToDateTime(TimeOnly.MinValue)).TotalHours
            : l.Day.DayNumber - from.DayNumber;
        string Label(DateOnly from, int index) => hourly
            ? from.ToDateTime(TimeOnly.MinValue).AddHours(index).ToString("dd.MM HH:00", SalesTextTr.Tr)
            : from.AddDays(index).ToString("dd.MM.yyyy", SalesTextTr.Tr);

        var count = hourly ? Math.Max(daysA, daysB) * 24 : Math.Max(daysA, daysB);
        var lenA = hourly ? daysA * 24 : daysA;
        var lenB = hourly ? daysB * 24 : daysB;

        var aByIndex = ctx.SalesA.GroupBy(l => Bucket(l, o.AFrom)).ToDictionary(g => g.Key, g => (Sales: g.Sum(x => x.Amount), Orders: SalesMetrics.CountOrders(g)));
        var bByIndex = ctx.SalesB.GroupBy(l => Bucket(l, o.BFrom)).ToDictionary(g => g.Key, g => (Sales: g.Sum(x => x.Amount), Orders: SalesMetrics.CountOrders(g)));

        var points = Enumerable.Range(0, count).Select(i =>
        {
            var va = aByIndex.GetValueOrDefault(i);
            var vb = bByIndex.GetValueOrDefault(i);
            return new SeriesPoint(i,
                i < lenA ? Label(o.AFrom, i) : null,
                i < lenB ? Label(o.BFrom, i) : null,
                va.Sales, vb.Sales, va.Orders, vb.Orders);
        }).ToList();

        var (peakB, lowB) = Extremes(ctx.SalesB, o.BFrom, o.BTo);
        var (peakA, lowA) = Extremes(ctx.SalesA, o.AFrom, o.ATo);

        var summary = new List<string>();
        if (peakB is not null && lowB is not null)
        {
            summary.Add($"Dönem B'de en çok satış yapılan gün {peakB.Date} ({SalesTextTr.Tl(peakB.Sales)}), " +
                        $"en az satış yapılan gün {lowB.Date} ({SalesTextTr.Tl(lowB.Sales)}).");
        }

        return new InsightResult(Key, Title, summary, [],
            new TimeSeriesData(granularity, points, peakB, lowB, peakA, lowA, Grid(ctx.SalesB), Grid(ctx.SalesA)));
    }

    /// <summary>Best and worst calendar day of a period. Days with no sales count — a zero day is the low.</summary>
    static (DayExtreme? Peak, DayExtreme? Low) Extremes(IReadOnlyList<SalesLine> lines, DateOnly from, DateOnly to)
    {
        if (to < from)
            return (null, null);

        var byDay = lines.GroupBy(l => l.Day).ToDictionary(g => g.Key, g => (Sales: g.Sum(x => x.Amount), Orders: SalesMetrics.CountOrders(g)));
        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(i => from.AddDays(i))
            .Select(d => new DayExtreme(d.ToString("dd.MM.yyyy", SalesTextTr.Tr), byDay.GetValueOrDefault(d).Sales, byDay.GetValueOrDefault(d).Orders))
            .ToList();

        return days.Count == 0 ? (null, null) : (days.MaxBy(d => d.Sales), days.MinBy(d => d.Sales));
    }

    /// <summary>Sales by weekday (0 = Monday) × hour (0–23).</summary>
    static double[][] Grid(IReadOnlyList<SalesLine> lines)
    {
        var grid = Enumerable.Range(0, 7).Select(_ => new double[24]).ToArray();
        foreach (var l in lines)
        {
            var weekday = ((int)l.Created.DayOfWeek + 6) % 7;
            grid[weekday][l.Created.Hour] += l.Amount;
        }
        return grid;
    }
}
