using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Holds the most recent Product Status run's table so the browser can ask for it after the run, and
/// again after a reload.
///
/// <para>The scrape is the expensive part — several minutes of real browser pages — and the progress
/// stream carries log lines, not data, so without this the only copy of the result would be whatever
/// the tab that started the run happened to still have. A reload would then mean scraping Mirakl again
/// to see a table that was already built.</para>
///
/// <para>In memory and not on disk, like <see cref="OfferBatchStore"/>: the figures are a snapshot of a
/// moving catalogue, so one that outlived a restart would be quietly stale rather than useful.</para>
/// </summary>
public sealed class ProductStatusStore
{
    readonly object _sync = new();
    ProductStatusResult? _current;

    /// <summary>Replaces the held result with the one this run produced.</summary>
    public void Put(ProductStatusResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_sync)
            _current = result;
    }

    /// <summary>The last run's result, or <c>null</c> when nothing has run since startup.</summary>
    public ProductStatusResult? Current
    {
        get { lock (_sync) return _current; }
    }
}
