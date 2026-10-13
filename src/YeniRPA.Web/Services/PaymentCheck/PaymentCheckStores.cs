using LiteDB;

namespace YeniRPA.Web.Services.PaymentCheck;

public interface IPaymentCheckSettingsStore
{
    PaymentCheckSettings Load();

    /// <summary>Changes only the fields that are set and returns the settings as saved.</summary>
    /// <exception cref="InvalidOperationException">The message is meant for the operator.</exception>
    PaymentCheckSettings Update(PaymentCheckSettingsUpdate update);
}

/// <summary>The payment-check settings: a single document in the <c>paymentCheckSettings</c> collection.</summary>
public sealed class PaymentCheckSettingsStore : IPaymentCheckSettingsStore
{
    const int DocumentId = 1;

    public sealed class Document
    {
        public int Id { get; set; }
        public PaymentCheckSettings Data { get; set; } = new();
    }

    readonly ILiteCollection<Document> _collection;

    /// <summary>Serialises load-modify-save, which LiteDB does not make atomic across two calls.</summary>
    readonly object _sync = new();

    public PaymentCheckSettingsStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _collection = context.GetCollection<Document>("paymentCheckSettings");
        _collection.EnsureIndex(x => x.Id, unique: true);
    }

    public PaymentCheckSettings Load() => _collection.FindById(DocumentId)?.Data ?? new PaymentCheckSettings();

    public PaymentCheckSettings Update(PaymentCheckSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_sync)
        {
            var current = Load();
            var next = new PaymentCheckSettings
            {
                Enabled = update.Enabled ?? current.Enabled,
                IntervalMinutes = update.IntervalMinutes ?? current.IntervalMinutes,
                WindowStartHour = update.WindowStartHour ?? current.WindowStartHour,
                WindowEndHour = update.WindowEndHour ?? current.WindowEndHour,
            };

            if (next.IntervalMinutes is < PaymentCheckLimits.MinIntervalMinutes or > PaymentCheckLimits.MaxIntervalMinutes)
                throw new InvalidOperationException(
                    $"The interval must be between {PaymentCheckLimits.MinIntervalMinutes} and {PaymentCheckLimits.MaxIntervalMinutes} minutes.");

            if (next.WindowStartHour is < 0 or > 23 || next.WindowEndHour is < 0 or > 23 || next.WindowStartHour > next.WindowEndHour)
                throw new InvalidOperationException("The window must run from an earlier hour to a later one (0-23).");

            _collection.Upsert(new Document { Id = DocumentId, Data = next });
            return next;
        }
    }
}

public interface IPaymentCheckHistoryStore
{
    void Add(PaymentCheckHistoryEntry entry);

    /// <summary>Newest first.</summary>
    IReadOnlyList<PaymentCheckHistoryEntry> Recent(int count);

    PaymentCheckHistoryEntry? Last();
}

/// <summary>One document per check in <c>paymentCheckHistory</c>, capped like <see cref="GmvNotification.GmvHistoryStore"/>.</summary>
public sealed class PaymentCheckHistoryStore : IPaymentCheckHistoryStore
{
    const int MaxEntries = 1000;

    readonly ILiteCollection<PaymentCheckHistoryEntry> _collection;

    public PaymentCheckHistoryStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _collection = context.GetCollection<PaymentCheckHistoryEntry>("paymentCheckHistory");
        _collection.EnsureIndex(x => x.TimestampUtc);
    }

    public void Add(PaymentCheckHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _collection.Insert(entry);

        var over = _collection.Count() - MaxEntries;
        if (over <= 0)
            return;

        foreach (var id in _collection.FindAll().OrderBy(x => x.TimestampUtc).ThenBy(x => x.Id).Take(over).Select(x => x.Id).ToList())
            _collection.Delete(id);
    }

    public IReadOnlyList<PaymentCheckHistoryEntry> Recent(int count) =>
        [.. _collection.FindAll().OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Id).Take(Math.Clamp(count, 1, 200))];

    public PaymentCheckHistoryEntry? Last() =>
        _collection.FindAll().OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Id).FirstOrDefault();
}
