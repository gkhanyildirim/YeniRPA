using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Remembers the rows <c>prepare</c> filtered out of the uploaded file, so <c>start</c> can read them
/// back instead of trusting the browser. Same shape and reasoning as <see cref="OfferBatchStore"/>:
/// exactly one batch is kept, the most recent prepare, and a start naming an older batch id is
/// refused rather than served from a stale pairing.
///
/// <para>In memory and not LiteDB — the batch is worthless after a restart (the operator would
/// re-upload), same as <see cref="OfferBatchStore"/> and <see cref="ProductStatusStore"/>.</para>
/// </summary>
public sealed class Track17BatchStore
{
    readonly object _sync = new();
    Track17Batch? _current;

    public Track17Batch Put(IReadOnlyList<Track17Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var batch = new Track17Batch(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, rows);

        lock (_sync)
            _current = batch;

        return batch;
    }

    /// <summary>The held batch when <paramref name="batchId"/> names it, <c>null</c> otherwise —
    /// including when it names a batch that has since been replaced.</summary>
    public Track17Batch? Get(string? batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            return null;

        lock (_sync)
            return _current is not null && _current.BatchId == batchId.Trim() ? _current : null;
    }
}
