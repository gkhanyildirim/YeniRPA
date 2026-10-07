using LiteDB;

namespace YeniRPA.Web.Services.GmvNotification;

public interface IGmvHistoryStore
{
    void Add(GmvHistoryEntry entry);

    /// <summary>Newest first.</summary>
    IReadOnlyList<GmvHistoryEntry> Recent(int count);

    /// <summary>True once any entry (sent, failed, skipped or missed) exists for the scheduled time.
    /// This is what keeps one slot from producing two messages.</summary>
    bool HasSlot(string slotKey);

    /// <summary>
    /// Status of the latest entry that says whether the login worked: <see cref="GmvStatus.Sent"/> or
    /// <see cref="GmvStatus.Checked"/> (it worked) or <see cref="GmvStatus.Session"/> (it had expired
    /// and the operator was told). Null when there is none.
    /// </summary>
    string? LastLoginSignal();
}

/// <summary>
/// The send history, one document per attempt in the <c>gmvHistory</c> collection. Capped like
/// <see cref="SystemLogStore"/>: a rolling record of recent notifications, not an archive.
/// </summary>
public sealed class GmvHistoryStore : IGmvHistoryStore
{
    const int MaxEntries = 1000;

    readonly ILiteCollection<GmvHistoryEntry> _collection;

    public GmvHistoryStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _collection = context.GetCollection<GmvHistoryEntry>("gmvHistory");
        _collection.EnsureIndex(x => x.TimestampUtc);
        _collection.EnsureIndex(x => x.SlotKey);
    }

    public void Add(GmvHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _collection.Insert(entry);

        var over = _collection.Count() - MaxEntries;
        if (over <= 0)
            return;

        foreach (var id in _collection.FindAll().OrderBy(x => x.TimestampUtc).ThenBy(x => x.Id).Take(over).Select(x => x.Id).ToList())
            _collection.Delete(id);
    }

    // Id breaks a tie between two entries stamped in the same clock tick: it only ever grows, so it is
    // the real order of writing.
    public IReadOnlyList<GmvHistoryEntry> Recent(int count) =>
        [.. _collection.FindAll().OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Id).Take(Math.Clamp(count, 1, 200))];

    public bool HasSlot(string slotKey) => _collection.Find(x => x.SlotKey == slotKey).Any();

    public string? LastLoginSignal() =>
        _collection.FindAll()
            .Where(x => x.Status is GmvStatus.Sent or GmvStatus.Checked or GmvStatus.Session)
            .OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Id)
            .FirstOrDefault()?.Status;
}
