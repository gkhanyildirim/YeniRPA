using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Remembers the most recently generated Cargo Seller Report — one result per seller — so
/// <c>download</c> can read the list back and zip one workbook per seller, instead of asking the
/// browser to re-upload and re-match all three files a second time.
///
/// <para>Exactly one batch is kept: the most recent <c>generate</c>. A download naming an older batch
/// id is refused rather than served from a stale match — generate the report again and download it.
/// In memory and not on disk, the same way <see cref="Track17BatchStore"/> and
/// <see cref="OfferBatchStore"/> are: the result is worthless after a restart anyway (the operator
/// would re-upload), so it is not part of <see cref="JsonToLiteDbMigrator"/>.</para>
/// </summary>
public sealed class CargoSellerReportStore
{
    readonly object _sync = new();
    (string BatchId, IReadOnlyList<CargoSellerReportResult> Results)? _current;

    /// <summary>Replaces the held batch with a new one and returns its id.</summary>
    public string Put(IReadOnlyList<CargoSellerReportResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var batchId = Guid.NewGuid().ToString("N");
        lock (_sync)
            _current = (batchId, results);

        return batchId;
    }

    /// <summary>The held batch when <paramref name="batchId"/> names it, <c>null</c> otherwise —
    /// including when it names a batch that has since been replaced.</summary>
    public IReadOnlyList<CargoSellerReportResult>? Get(string? batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            return null;

        lock (_sync)
            return _current is { } current && current.BatchId == batchId.Trim() ? current.Results : null;
    }
}
