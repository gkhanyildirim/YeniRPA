using System.Collections.Concurrent;

namespace YeniRPA.Web.Services.SalesAnalysis;

/// <summary>
/// One filter combination's prepared periods plus the sections already computed for it. Sections
/// are filled lazily as the page's tabs ask for them, so a tab opened twice is computed once.
/// </summary>
public sealed class SalesAnalysisRun(SalesAnalysisContext context)
{
    public SalesAnalysisContext Context { get; } = context;

    public ConcurrentDictionary<string, Lazy<InsightResult>> Sections { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Holds the most recently loaded orders export for the Sales Analysis, and a small cache of
/// analysis runs on it keyed by the full filter combination.
///
/// <para>In memory only, like <see cref="PosReconciliationStore"/>: the parsed lines are worthless
/// after a restart (the operator re-uploads), so they are deliberately not written to LiteDB. One
/// dataset is kept; loading a new file drops the previous one and every cached run with it. A token
/// naming a replaced dataset is refused rather than answered from stale data.</para>
/// </summary>
public sealed class SalesAnalysisStore
{
    const int MaxCachedRuns = 32;

    readonly object _sync = new();
    (string Token, SalesDataset Dataset)? _current;
    readonly Dictionary<string, SalesAnalysisRun> _runs = new(StringComparer.Ordinal);
    readonly Queue<string> _runOrder = new();

    public string Put(SalesDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var token = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _current = (token, dataset);
            _runs.Clear();
            _runOrder.Clear();
        }
        return token;
    }

    public SalesDataset? Get(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        lock (_sync)
            return _current is { } current && current.Token == token.Trim() ? current.Dataset : null;
    }

    /// <summary>
    /// The cached run for <paramref name="key"/>, or a new one built by <paramref name="create"/>.
    /// The oldest run is evicted past <see cref="MaxCachedRuns"/>. <paramref name="create"/> runs
    /// inside the lock: it is a filter over in-memory lines, and running it twice for the same key
    /// would be the bigger waste.
    /// </summary>
    public SalesAnalysisRun GetOrAddRun(string key, Func<SalesAnalysisRun> create)
    {
        lock (_sync)
        {
            if (_runs.TryGetValue(key, out var run))
                return run;

            run = create();
            _runs[key] = run;
            _runOrder.Enqueue(key);
            while (_runOrder.Count > MaxCachedRuns)
                _runs.Remove(_runOrder.Dequeue());
            return run;
        }
    }
}
