using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>Holds the most recent Track 17 run's delivered-rows table, the same way
/// <see cref="ProductStatusStore"/> holds Product Status's — the scrape is minutes of real browser
/// pages, so the result is kept here rather than only in whichever tab started the run. In memory and
/// not LiteDB: a delivery snapshot that outlived a restart would be quietly stale.</summary>
public sealed class Track17Store
{
    readonly object _sync = new();
    Track17RunResult? _current;

    public void Put(Track17RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_sync)
            _current = result;
    }

    public Track17RunResult? Current
    {
        get { lock (_sync) return _current; }
    }
}
