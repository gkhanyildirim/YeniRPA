using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Remembers the most recently generated POS reconciliation batch so <c>download</c> can rebuild the
/// workbook without asking the browser to re-upload and re-merge both files a second time.
///
/// <para>Exactly one batch is kept: the most recent <c>generate</c>. A download naming an older batch
/// id is refused rather than served from a stale merge — generate the report again and download it.
/// In memory and not on disk, the same way <see cref="CargoSellerReportStore"/> is: the result is
/// worthless after a restart anyway (the operator would re-upload), so it is not part of
/// <see cref="JsonToLiteDbMigrator"/>.</para>
/// </summary>
public sealed class PosReconciliationStore
{
    readonly object _sync = new();
    (string BatchId, PosReconciliationBatch Batch)? _current;

    /// <summary>Replaces the held batch with a new one and returns its id.</summary>
    public string Put(PosReconciliationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var batchId = Guid.NewGuid().ToString("N");
        lock (_sync)
            _current = (batchId, batch);

        return batchId;
    }

    /// <summary>The held batch when <paramref name="batchId"/> names it, <c>null</c> otherwise —
    /// including when it names a batch that has since been replaced.</summary>
    public PosReconciliationBatch? Get(string? batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            return null;

        lock (_sync)
            return _current is { } current && current.BatchId == batchId.Trim() ? current.Batch : null;
    }
}
