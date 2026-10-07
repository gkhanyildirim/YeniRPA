using System.Globalization;
using System.Security.Cryptography;
using LiteDB;
using Microsoft.AspNetCore.DataProtection;

namespace YeniRPA.Web.Services.GmvNotification;

public interface IGmvSettingsStore
{
    GmvSettings Load();

    /// <summary>Validates and saves. An empty <see cref="GmvSettingsUpdate.Token"/> keeps the stored one.</summary>
    /// <exception cref="InvalidOperationException">The message is meant for the operator.</exception>
    void Save(GmvSettingsUpdate update);

    /// <summary>The decrypted bot token, or null when none is saved or it can no longer be decrypted.</summary>
    string? ReadToken();

    void ClearToken();
}

/// <summary>
/// The GMV notification settings: a single document in the <c>gmvSettings</c> collection of the shared
/// LiteDB database, like <see cref="SellerGroupStore"/>.
///
/// <para>The Telegram bot token lets anyone who holds it post as the bot, so it is encrypted with
/// <see cref="IDataProtector"/> (the same mechanism as <c>MiraklBrowser</c>'s <c>auth.dat</c>) and is
/// deliberately <b>not</b> part of <see cref="DatabaseBackupService"/>'s export, which writes plain
/// JSON. After restoring on another machine the token is entered again.</para>
/// </summary>
public sealed class GmvSettingsStore : IGmvSettingsStore
{
    const int DocumentId = 1;

    public sealed class Document
    {
        public int Id { get; set; }
        public GmvSettings Data { get; set; } = new();
    }

    readonly ILiteCollection<Document> _collection;
    readonly IDataProtector _protector;

    /// <summary>Serialises load-modify-save, which LiteDB does not make atomic across two calls.</summary>
    readonly object _sync = new();

    public GmvSettingsStore(ILiteDbContext context, IDataProtectionProvider dataProtection)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dataProtection);

        _collection = context.GetCollection<Document>("gmvSettings");
        _collection.EnsureIndex(x => x.Id, unique: true);
        _protector = dataProtection.CreateProtector("YeniRPA.Gmv.TelegramToken");
    }

    public GmvSettings Load() => _collection.FindById(DocumentId)?.Data ?? new GmvSettings();

    public void Save(GmvSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var mode = update.Mode?.Trim().ToLowerInvariant();
        if (!GmvMode.IsValid(mode))
            throw new InvalidOperationException("Choose how often to notify: hourly, at specific times, or manual only.");

        var times = NormalizeTimes(update.Times ?? []);
        if (mode == GmvMode.Times && update.Enabled && times.Count == 0)
            throw new InvalidOperationException("Enter at least one notification time, for example 14:00.");

        if (mode == GmvMode.Hourly && (update.WindowStartHour is < 0 or > 23 || update.WindowEndHour is < 0 or > 23
                || update.WindowStartHour > update.WindowEndHour))
            throw new InvalidOperationException("The hourly window must run from an earlier hour to a later one (0-23).");

        if (update.DailyTarget is <= 0 || update.DailyTarget is { } t && !double.IsFinite(t))
            throw new InvalidOperationException("The daily GMV target must be a positive number.");

        lock (_sync)
        {
            var current = Load();
            var newToken = update.Token?.Trim();
            var protectedToken = string.IsNullOrEmpty(newToken) ? current.ProtectedToken : _protector.Protect(newToken);
            var chatId = update.ChatId?.Trim() ?? "";

            if (update.Enabled && (chatId.Length == 0 || protectedToken is null))
                throw new InvalidOperationException("Enter the bot token and the chat ID before turning notifications on.");

            _collection.Upsert(new Document
            {
                Id = DocumentId,
                Data = new GmvSettings
                {
                    Enabled = update.Enabled,
                    Mode = mode!,
                    Times = times,
                    WindowStartHour = update.WindowStartHour,
                    WindowEndHour = update.WindowEndHour,
                    ChatId = chatId,
                    ProtectedToken = protectedToken,
                    DailyTarget = update.DailyTarget,
                },
            });
        }
    }

    public string? ReadToken()
    {
        var stored = Load().ProtectedToken;
        if (stored is null)
            return null;

        try { return _protector.Unprotect(stored); }
        catch (CryptographicException) { return null; }
    }

    public void ClearToken()
    {
        lock (_sync)
        {
            var current = Load();
            current.ProtectedToken = null;
            // Notifications cannot run without a token, so they are switched off rather than left
            // "on" and failing at every scheduled time.
            current.Enabled = false;
            _collection.Upsert(new Document { Id = DocumentId, Data = current });
        }
    }

    static List<string> NormalizeTimes(IReadOnlyList<string> raw)
    {
        var parsed = new SortedSet<TimeOnly>();
        foreach (var item in raw)
        {
            var text = item?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            if (!TimeOnly.TryParseExact(text, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new InvalidOperationException($"'{text}' is not a valid time. Use HH:mm, for example 14:00.");

            parsed.Add(time);
        }

        return [.. parsed.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture))];
    }
}
