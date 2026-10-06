namespace YeniRPA.Web.Services.SalesAnalysis;

/// <summary>
/// One "why" analysis — a category breakdown, the PVM split, the outlier check, … Each provider is
/// independent: it reads the two periods off <see cref="SalesAnalysisContext"/> and returns a title,
/// generated Turkish sentences, candidate reasons for the Reasons panel and the chart/table data.
///
/// <para><b>Adding a new reason</b> is one class implementing this interface plus one
/// <c>AddSingleton&lt;IInsightProvider, …&gt;</c> line in Program.cs. The service picks it up by
/// <see cref="Key"/>; its <see cref="InsightResult.Findings"/> automatically compete for a place in
/// the Reasons panel; the page renders it once a tab asks for that key.</para>
///
/// <para>Providers must be stateless and thread-safe: they are singletons shared by every request.</para>
/// </summary>
public interface IInsightProvider
{
    /// <summary>Stable id the page requests the section by ("category", "pvm", …).</summary>
    string Key { get; }

    string Title { get; }

    InsightResult Compute(SalesAnalysisContext context);
}

/// <summary>
/// A candidate sentence for the Reasons panel. <see cref="Impact"/> is the size of the effect in
/// lira (absolute value is what ranks it) — the composer has no other way to compare a category's
/// contribution with a cancellation-rate jump, so every provider expresses its finding in money.
/// </summary>
public sealed record Finding(string Text, double Impact, string Source);

public sealed record InsightResult(
    string Key,
    string Title,
    IReadOnlyList<string> Summary,
    IReadOnlyList<Finding> Findings,
    object? Data);

/// <summary>Operator-chosen settings that shape every provider's output.</summary>
public sealed record SalesAnalysisOptions(
    DateOnly AFrom,
    DateOnly ATo,
    DateOnly BFrom,
    DateOnly BTo,
    IReadOnlySet<string> StatusSet,
    double OtherThresholdPct,
    string OutlierMode);

/// <summary>Orders the outlier switch removed from both periods before any provider ran.</summary>
public sealed record OutlierExclusion(string Mode, int Orders, double Amount, double? Threshold);

/// <summary>
/// What every provider receives: both periods after the dimension filters and the outlier switch.
/// <c>All*</c> carries every status (cancellation figures need canceled lines); <c>Sales*</c> only
/// the statuses in the operator's sales set. Metrics are computed once here and shared.
/// </summary>
public sealed class SalesAnalysisContext
{
    public SalesAnalysisContext(
        SalesAnalysisOptions options,
        IReadOnlyList<SalesLine> allA,
        IReadOnlyList<SalesLine> allB,
        OutlierExclusion? exclusion = null)
    {
        Options = options;
        AllA = allA;
        AllB = allB;
        SalesA = [.. allA.Where(l => options.StatusSet.Contains(l.Status))];
        SalesB = [.. allB.Where(l => options.StatusSet.Contains(l.Status))];
        MetricsA = SalesMetrics.Compute(AllA, SalesA);
        MetricsB = SalesMetrics.Compute(AllB, SalesB);
        Exclusion = exclusion;
    }

    public SalesAnalysisOptions Options { get; }
    public IReadOnlyList<SalesLine> AllA { get; }
    public IReadOnlyList<SalesLine> AllB { get; }
    public IReadOnlyList<SalesLine> SalesA { get; }
    public IReadOnlyList<SalesLine> SalesB { get; }
    public PeriodMetrics MetricsA { get; }
    public PeriodMetrics MetricsB { get; }
    public OutlierExclusion? Exclusion { get; }

    public double SalesDelta => MetricsB.GrossSales - MetricsA.GrossSales;
}
