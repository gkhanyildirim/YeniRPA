using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Remembers the pairing that <c>prepare</c> worked out — which addresses and which file belong to
/// which seller — so <c>send</c> can read it back instead of trusting the browser.
///
/// <para><b>Why this exists at all.</b> Seller Offer Warnings re-derives the recipient and the
/// attachment from its saved mapping table at send time; nothing the browser posts can change either,
/// and a posted value that disagrees is refused by name. This module has no saved table — the pairing
/// is computed from two uploaded files — so if it were not held here, the only copy at send time would
/// be the one in the browser. Taking a client-supplied address or path is how an automation ends up
/// mailing one seller's complete price list to another.</para>
///
/// <para>Exactly one batch is kept: the most recent prepare. A send naming an older batch is refused
/// rather than served from a stale pairing, which is the same answer Offer Warnings gives when the
/// mapping has moved under a preview — build the mails again and read the list.</para>
///
/// <para>In memory and not on disk. The batch is worthless after a restart anyway (the operator would
/// re-prepare), and it holds every seller's address, which is not something to persist for no
/// gain.</para>
/// </summary>
public sealed class VatBatchStore
{
    readonly object _sync = new();
    VatBatch? _current;

    /// <summary>Replaces the held batch with a new one and returns it.</summary>
    public VatBatch Put(string outputFolder, IEnumerable<VatBatchMail> mails)
    {
        ArgumentNullException.ThrowIfNull(mails);

        var bySellerKey = new Dictionary<string, VatBatchMail>(StringComparer.Ordinal);
        foreach (var mail in mails)
        {
            // Last one wins rather than throwing: prepare builds one entry per seller group, so a
            // repeat here would be a bug in the caller, not operator input.
            bySellerKey[mail.SellerKey] = mail;
        }

        var batch = new VatBatch(
            BatchId: Guid.NewGuid().ToString("N"),
            OutputFolder: outputFolder,
            CreatedUtc: DateTimeOffset.UtcNow,
            BySellerKey: bySellerKey);

        lock (_sync)
            _current = batch;

        return batch;
    }

    /// <summary>The held batch when <paramref name="batchId"/> names it, <c>null</c> otherwise —
    /// including when it names a batch that has since been replaced.</summary>
    public VatBatch? Get(string? batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            return null;

        lock (_sync)
            return _current is not null && _current.BatchId == batchId.Trim() ? _current : null;
    }

    /// <summary>The held batch, whatever its id. For the status endpoint.</summary>
    public VatBatch? Current
    {
        get { lock (_sync) return _current; }
    }
}
