using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Remembers the pairing that <c>prepare</c> worked out — which addresses and which file belong to
/// which seller — so <c>send</c> can read it back instead of trusting the browser.
///
/// <para><b>Why this exists at all.</b> The pairing is computed from two uploaded files and is written
/// down nowhere else, so without this the only copy at send time would be the one in the browser.
/// Taking a client-supplied address or path is how an automation ends up mailing one seller's complete
/// offer list to another. The twin of <see cref="VatBatchStore"/>, kept separate for the same reason
/// the two split builders are.</para>
///
/// <para>Exactly one batch is kept: the most recent prepare. A send naming an older batch is refused
/// rather than served from a stale pairing — build the mails again and read the list.</para>
///
/// <para>In memory and not on disk. The batch is worthless after a restart anyway (the operator would
/// re-prepare), and it holds every seller's address, which is not something to persist for no
/// gain.</para>
/// </summary>
public sealed class OfferBatchStore
{
    readonly object _sync = new();
    OfferBatch? _current;

    /// <summary>Replaces the held batch with a new one and returns it.</summary>
    public OfferBatch Put(string outputFolder, string? cc, bool includeSignature, IEnumerable<OfferBatchMail> mails)
    {
        ArgumentNullException.ThrowIfNull(mails);

        var bySellerKey = new Dictionary<string, OfferBatchMail>(StringComparer.Ordinal);
        foreach (var mail in mails)
        {
            // Last one wins rather than throwing: prepare builds one entry per seller group, so a
            // repeat here would be a bug in the caller, not operator input.
            bySellerKey[mail.SellerKey] = mail;
        }

        var batch = new OfferBatch(
            BatchId: Guid.NewGuid().ToString("N"),
            OutputFolder: outputFolder,
            CreatedUtc: DateTimeOffset.UtcNow,
            Cc: cc,
            IncludeSignature: includeSignature,
            BySellerKey: bySellerKey);

        lock (_sync)
            _current = batch;

        return batch;
    }

    /// <summary>The held batch when <paramref name="batchId"/> names it, <c>null</c> otherwise —
    /// including when it names a batch that has since been replaced.</summary>
    public OfferBatch? Get(string? batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            return null;

        lock (_sync)
            return _current is not null && _current.BatchId == batchId.Trim() ? _current : null;
    }

    /// <summary>The held batch, whatever its id. For the status endpoint.</summary>
    public OfferBatch? Current
    {
        get { lock (_sync) return _current; }
    }
}
