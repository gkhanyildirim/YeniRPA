using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Remembers the most recently generated SKU Offer &amp; Traffic Report so the table can page, filter and
/// download without the browser re-uploading ~50 MB each time. Exactly one batch is kept; asking for a
/// replaced batch id is refused. In memory only, like <see cref="CargoSellerReportStore"/> — the result
/// is meant to be gone after a restart.
/// </summary>
public sealed class SkuOfferReportStore
{
    readonly object _sync = new();
    (string BatchId, SkuOfferReport Report)? _current;

    public string Put(SkuOfferReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var batchId = Guid.NewGuid().ToString("N");
        lock (_sync)
            _current = (batchId, report);

        return batchId;
    }

    public SkuOfferReport? Get(string? batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            return null;

        lock (_sync)
            return _current is { } current && current.BatchId == batchId.Trim() ? current.Report : null;
    }
}
