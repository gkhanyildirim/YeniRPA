using System.Globalization;

namespace YeniRPA.Web.Services.SalesAnalysis;

public sealed record PeriodRange(DateOnly From, DateOnly To);

public sealed record SalesAnalysisRequest(
    string? Token,
    PeriodRange? PeriodA,
    PeriodRange? PeriodB,
    string[]? Statuses,
    string[]? Categories,
    string[]? Brands,
    string[]? Sellers,
    string? OutlierMode,
    double? OtherThresholdPct,
    string[]? Sections);

public sealed record FilterOption(string Value, string Label, int Lines);

public sealed record LoadResult(
    string Token,
    string MinDate,
    string MaxDate,
    int Lines,
    int Orders,
    ImportReport Report,
    PeriodRange DefaultA,
    PeriodRange DefaultB,
    IReadOnlyList<FilterOption> Statuses,
    IReadOnlyList<string> DefaultStatuses,
    IReadOnlyList<FilterOption> Categories,
    IReadOnlyList<FilterOption> Brands,
    IReadOnlyList<FilterOption> Sellers);

public sealed record PeriodInfo(string From, string To, int Days, int Orders, int Lines);

public sealed record AnalysisResponse(
    PeriodInfo PeriodA,
    PeriodInfo PeriodB,
    bool WeakData,
    IReadOnlyList<string> Warnings,
    OutlierExclusion? Exclusion,
    IReadOnlyDictionary<string, InsightResult> Sections);

/// <summary>
/// Runs the Sales Analysis: loads an export into <see cref="SalesAnalysisStore"/>, then for each
/// request slices both periods, applies the dimension filters and the outlier switch, and computes
/// only the sections the page asked for (tabs load lazily). Results are cached per filter
/// combination, so switching back to a tab or re-applying the same filter costs nothing.
/// </summary>
public sealed class SalesAnalysisService
{
    /// <summary>Fewer orders than this in either period and the page warns the comparison is statistically weak.</summary>
    public const int MinOrdersForConfidence = 30;

    public const double DefaultOtherThresholdPct = 1.0;

    static readonly string[] DefaultSections = ["kpi", ReasonsComposer.Key, "pvm", "outlier"];

    readonly SalesAnalysisStore _store;
    readonly Dictionary<string, IInsightProvider> _providers;

    public SalesAnalysisService(SalesAnalysisStore store, IEnumerable<IInsightProvider> providers)
    {
        _store = store;
        _providers = providers.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IInsightProvider> Providers => _providers.Values;

    public LoadResult Load(Stream stream, string fileName)
    {
        var dataset = SalesOrderReader.Read(stream, fileName);
        if (dataset.Lines.Count == 0)
            throw new InvalidOperationException("Dosyada tarihi okunabilen sipariş satırı yok.");

        var token = _store.Put(dataset);
        var lines = dataset.Lines;
        var min = lines.Min(l => l.Day);
        var max = lines.Max(l => l.Day);

        // Default comparison: the file's span cut in two — the later half against the equally long
        // half before it. On the sample week (28.09–05.10) that is 02–05.10 against 28.09–01.10.
        var span = max.DayNumber - min.DayNumber + 1;
        var half = Math.Max(1, span / 2);
        var bFrom = max.AddDays(-(half - 1));
        var defaultB = new PeriodRange(bFrom, max);
        var defaultA = new PeriodRange(bFrom.AddDays(-half), bFrom.AddDays(-1));

        static List<FilterOption> Options(IEnumerable<SalesLine> source, Func<SalesLine, string> value, Func<string, string> label) =>
            [.. source.GroupBy(value, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Key.Length > 0)
                .Select(g => new FilterOption(g.Key, label(g.Key), g.Count()))
                .OrderByDescending(o => o.Lines)
                .ThenBy(o => o.Label, StringComparer.Create(SalesTextTr.Tr, true))];

        var statuses = Options(lines, l => l.Status, SalesStatuses.Label);

        return new LoadResult(
            token,
            Iso(min),
            Iso(max),
            lines.Count,
            SalesMetrics.CountOrders(lines),
            dataset.Report,
            defaultA,
            defaultB,
            statuses,
            [.. SalesMetrics.DefaultSalesStatuses(statuses.Select(s => s.Value))],
            Options(lines, l => l.CategoryLabel, SalesTextTr.TitleCase),
            Options(lines, l => l.Brand, SalesTextTr.TitleCase),
            Options(lines, l => l.Seller, v => v));
    }

    public AnalysisResponse Analyze(SalesAnalysisRequest request)
    {
        var dataset = _store.Get(request.Token)
            ?? throw new InvalidOperationException("Yüklenen veri artık bellekte değil (uygulama yeniden başlamış ya da başka bir dosya yüklenmiş). Dosyayı yeniden yükleyin.");

        var a = Validate(request.PeriodA, "Dönem A");
        var b = Validate(request.PeriodB, "Dönem B");

        var statusSet = request.Statuses is { Length: > 0 }
            ? new HashSet<string>(request.Statuses, StringComparer.OrdinalIgnoreCase)
            : SalesMetrics.DefaultSalesStatuses(dataset.Lines.Select(l => l.Status).Distinct(StringComparer.OrdinalIgnoreCase));

        var categories = SetOf(request.Categories);
        var brands = SetOf(request.Brands);
        var sellers = SetOf(request.Sellers);
        var outlierMode = (request.OutlierMode ?? OutlierFilter.None).Trim().ToLowerInvariant();
        var threshold = Math.Clamp(request.OtherThresholdPct ?? DefaultOtherThresholdPct, 0, 50);

        var cacheKey = string.Join('|',
            request.Token, Iso(a.From), Iso(a.To), Iso(b.From), Iso(b.To),
            Key(statusSet), Key(categories), Key(brands), Key(sellers), outlierMode,
            threshold.ToString(CultureInfo.InvariantCulture));

        var run = _store.GetOrAddRun(cacheKey, () =>
        {
            bool Passes(SalesLine l) =>
                (categories is null || categories.Contains(l.CategoryLabel)) &&
                (brands is null || brands.Contains(l.Brand)) &&
                (sellers is null || sellers.Contains(l.Seller));

            var allA = dataset.Lines.Where(l => l.Day >= a.From && l.Day <= a.To && Passes(l)).ToList();
            var allB = dataset.Lines.Where(l => l.Day >= b.From && l.Day <= b.To && Passes(l)).ToList();
            var (filteredA, filteredB, exclusion) = OutlierFilter.Apply(allA, allB, statusSet, outlierMode);

            var options = new SalesAnalysisOptions(a.From, a.To, b.From, b.To, statusSet, threshold, outlierMode);
            return new SalesAnalysisRun(new SalesAnalysisContext(options, filteredA, filteredB, exclusion));
        });

        var ctx = run.Context;
        var requested = request.Sections is { Length: > 0 } ? request.Sections : DefaultSections;
        var sections = new Dictionary<string, InsightResult>(StringComparer.Ordinal);
        foreach (var key in requested.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(key, ReasonsComposer.Key, StringComparison.OrdinalIgnoreCase))
                sections[ReasonsComposer.Key] = Section(run, ReasonsComposer.Key, () =>
                    ReasonsComposer.Compose(ctx, _providers.Values.Select(p => Section(run, p.Key, () => p.Compute(ctx)))));
            else if (_providers.TryGetValue(key, out var provider))
                sections[provider.Key] = Section(run, provider.Key, () => provider.Compute(ctx));
        }

        var warnings = new List<string>();
        var ordersA = ctx.MetricsA.Orders;
        var ordersB = ctx.MetricsB.Orders;
        if (ordersA == 0)
            warnings.Add("Dönem A'da hiç satış yok, bu yüzden yüzde değişim hesaplanamıyor.");
        if (ordersB == 0)
            warnings.Add("Dönem B'de hiç satış yok.");
        var weak = ordersA < MinOrdersForConfidence || ordersB < MinOrdersForConfidence;
        if (weak && ordersA > 0 && ordersB > 0)
        {
            warnings.Add($"Dönemlerden en az birinde {MinOrdersForConfidence} siparişten az veri var " +
                         $"(Dönem A: {ordersA}, Dönem B: {ordersB}). Bu kadar az veriyle çıkan sonuçlar yanıltıcı olabilir.");
        }
        var daysA = a.To.DayNumber - a.From.DayNumber + 1;
        var daysB = b.To.DayNumber - b.From.DayNumber + 1;
        if (daysA != daysB)
            warnings.Add($"İki dönemin uzunluğu farklı (Dönem A: {daysA} gün, Dönem B: {daysB} gün). Toplam tutarları doğrudan karşılaştırmak yanıltıcı olur.");
        warnings.AddRange(PartialDayWarnings(dataset.Lines, a, b));

        return new AnalysisResponse(
            new PeriodInfo(Tr(a.From), Tr(a.To), daysA, ordersA, ctx.AllA.Count),
            new PeriodInfo(Tr(b.From), Tr(b.To), daysB, ordersB, ctx.AllB.Count),
            weak,
            warnings,
            ctx.Exclusion,
            sections);
    }

    /// <summary>
    /// An export is cut at the moment it was taken, so its first or last day is often only partly in
    /// the file (the sample's 05.10 holds 26 lines against ~500 on every other day). A period that
    /// contains such an edge day reads as a collapse that never happened, so it is called out. An
    /// edge day counts as partial when it has under half the median line count of the other days.
    /// </summary>
    internal static IEnumerable<string> PartialDayWarnings(IReadOnlyList<SalesLine> lines, PeriodRange a, PeriodRange b)
    {
        var perDay = lines.GroupBy(l => l.Day).ToDictionary(g => g.Key, g => g.Count());
        if (perDay.Count < 3)
            yield break;

        var first = perDay.Keys.Min();
        var last = perDay.Keys.Max();
        foreach (var edge in new[] { first, last }.Distinct())
        {
            var others = perDay.Where(kv => kv.Key != edge).Select(kv => (double)kv.Value).Order().ToArray();
            var median = OutlierFilter.Quantile(others, 0.5);
            if (perDay[edge] >= median * 0.5)
                continue;

            var period = edge >= b.From && edge <= b.To ? "Dönem B" : edge >= a.From && edge <= a.To ? "Dönem A" : null;
            if (period is null)
                continue;

            yield return $"{Tr(edge)} günü dosyada eksik görünüyor: bu günde {perDay[edge]} satır var, diğer günlerde ise " +
                         $"genellikle {median:0} civarında. Dosya muhtemelen gün bitmeden alınmış. Bu gün {period} içinde " +
                         "olduğu için o dönemin satışı gerçekte olduğundan düşük görünüyor.";
        }
    }

    static InsightResult Section(SalesAnalysisRun run, string key, Func<InsightResult> compute) =>
        run.Sections.GetOrAdd(key, _ => new Lazy<InsightResult>(compute)).Value;

    static PeriodRange Validate(PeriodRange? range, string name)
    {
        if (range is null)
            throw new InvalidOperationException($"{name} seçilmedi.");
        if (range.To < range.From)
            throw new InvalidOperationException($"{name}: bitiş tarihi başlangıçtan önce olamaz.");
        return range;
    }

    static HashSet<string>? SetOf(string[]? values) =>
        values is { Length: > 0 } ? new HashSet<string>(values, StringComparer.OrdinalIgnoreCase) : null;

    static string Key(IEnumerable<string>? values) =>
        values is null ? "*" : string.Join(',', values.Select(v => v.ToUpperInvariant()).Order(StringComparer.Ordinal));

    static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static string Tr(DateOnly d) => d.ToString("dd.MM.yyyy", SalesTextTr.Tr);
}
