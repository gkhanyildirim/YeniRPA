using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Remembers the recipient list <c>prepare</c> merged out of the two uploads, so <c>send</c> can read
/// it back instead of trusting the browser. Same shape and reasoning as <see cref="OfferBatchStore"/>
/// and <see cref="Track17BatchStore"/>: exactly one batch is kept, the most recent prepare, and a send
/// naming an older batch id is refused rather than served from a stale list.
///
/// <para>Also owns the folder the one shared attachment is saved into for the duration of a run —
/// <c>%LOCALAPPDATA%\YeniRPA\CustomMail</c>, resolved fresh from the current process's own environment
/// and never wwwroot, which is served to the browser. A fresh timestamped subfolder is created per send
/// rather than reused, so a second run's attachment can never overwrite the first while it is still
/// mid-flight across several paced passes.</para>
///
/// <para>In memory and not LiteDB — the batch is worthless after a restart anyway (the operator would
/// re-upload), same as <see cref="OfferBatchStore"/>.</para>
/// </summary>
public sealed class CustomMailBatchStore
{
    readonly object _sync = new();
    CustomMailBatch? _current;

    public string AttachmentRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YeniRPA", "CustomMail");

    /// <summary>Replaces the held batch with a new one and returns it.</summary>
    public CustomMailBatch Put(IEnumerable<CustomMailRecipient> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        var byEmailKey = new Dictionary<string, CustomMailRecipient>(StringComparer.Ordinal);
        foreach (var recipient in recipients)
            byEmailKey[SellerMailStore.NormalizeEmail(recipient.Email)] = recipient;

        var batch = new CustomMailBatch(
            BatchId: Guid.NewGuid().ToString("N"),
            CreatedUtc: DateTimeOffset.UtcNow,
            ByEmailKey: byEmailKey);

        lock (_sync)
            _current = batch;

        return batch;
    }

    /// <summary>The held batch when <paramref name="batchId"/> names it, <c>null</c> otherwise —
    /// including when it names a batch that has since been replaced.</summary>
    public CustomMailBatch? Get(string? batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            return null;

        lock (_sync)
            return _current is not null && _current.BatchId == batchId.Trim() ? _current : null;
    }
}
