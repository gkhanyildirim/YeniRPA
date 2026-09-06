using LiteDB;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>The instance surface <see cref="SystemLogStore"/> exposes through DI.</summary>
public interface ISystemLogStore
{
    /// <summary>Appends one entry. Called from <see cref="Infrastructure.SystemLogActionFilter"/> for
    /// every POST action (report generation, settings saves, import/export) and from
    /// <see cref="Automation.AutomationJobBus"/> once a Playwright/Outlook run actually finishes —
    /// see those classes for why the two together are what "automation and Excel processing" means
    /// in practice.</summary>
    void Record(string category, string operation, string status, long durationMs, string? detail);

    SystemLogPage Query(string? category, string? status, string? search, int page, int pageSize);
    SystemLogSummary Summary();
    IReadOnlyList<string> Categories();
}

/// <summary>
/// Owns the <c>systemLogs</c> collection of the shared LiteDB database (<see cref="ILiteDbContext"/>).
///
/// <para>Unlike the settings stores, this is a genuine many-row collection — one document per
/// recorded operation — so it is queried in memory via <see cref="ILiteCollection{T}.FindAll"/>
/// rather than through LiteDB's own query builder. At the size this collection is capped to
/// (<see cref="MaxEntries"/>), that costs nothing and sidesteps translating filter predicates into
/// LiteDB's expression language for a feature that is read far more rarely than it is written.</para>
///
/// <para>Capped rather than kept forever: this is a rolling view of recent activity for an operator
/// glancing at "is anything broken right now", not an audit trail. The oldest rows are dropped once
/// the collection passes the cap.</para>
/// </summary>
public sealed class SystemLogStore : ISystemLogStore
{
    const int MaxEntries = 2000;

    readonly ILiteCollection<SystemLog> _collection;

    public SystemLogStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _collection = context.GetCollection<SystemLog>("systemLogs");
        _collection.EnsureIndex(x => x.TimestampUtc);
    }

    public void Record(string category, string operation, string status, long durationMs, string? detail)
    {
        _collection.Insert(new SystemLog
        {
            TimestampUtc = DateTime.UtcNow,
            Category = category,
            Operation = operation,
            Status = status,
            DurationMs = durationMs,
            Detail = detail,
        });

        Trim();
    }

    /// <summary>Drops the oldest rows once the collection passes <see cref="MaxEntries"/>. Run on every
    /// insert rather than on a timer — the collection is small enough that this costs nothing, and a
    /// background sweep would be one more thing to keep alive for no benefit here.</summary>
    void Trim()
    {
        var all = _collection.FindAll().ToList();
        var over = all.Count - MaxEntries;
        if (over <= 0)
            return;

        foreach (var id in all.OrderBy(x => x.TimestampUtc).Take(over).Select(x => x.Id))
            _collection.Delete(id);
    }

    public SystemLogPage Query(string? category, string? status, string? search, int page, int pageSize)
    {
        IEnumerable<SystemLog> rows = _collection.FindAll();

        if (!string.IsNullOrWhiteSpace(category))
            rows = rows.Where(x => string.Equals(x.Category, category, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(status))
            rows = rows.Where(x => string.Equals(x.Status, status, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            rows = rows.Where(x =>
                x.Operation.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                (x.Detail ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = rows.OrderByDescending(x => x.TimestampUtc).ToList();
        var pageSafe = Math.Max(1, page);
        var sizeSafe = Math.Clamp(pageSize, 1, 200);

        var items = ordered.Skip((pageSafe - 1) * sizeSafe).Take(sizeSafe).ToList();
        return new SystemLogPage(items, ordered.Count);
    }

    public SystemLogSummary Summary()
    {
        var all = _collection.FindAll().ToList();
        var success = all.Count(x => x.Status == SystemLogStatus.Success);
        var error = all.Count - success;
        var rate = all.Count > 0 ? Math.Round(success * 100.0 / all.Count, 1) : 0;

        return new SystemLogSummary(all.Count, success, error, rate);
    }

    public IReadOnlyList<string> Categories() =>
        _collection.FindAll()
            .Select(x => x.Category)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
}
