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

    /// <summary>
    /// Changes only the schedule fields that are set (the Jobs screen), leaving the token, chat and
    /// target alone. Returns the settings as saved.
    /// </summary>
    /// <exception cref="InvalidOperationException">The message is meant for the operator.</exception>
    GmvSettings UpdateSchedule(GmvScheduleUpdate update);

    /// <summary>The decrypted bot token, or null when none is saved or it can no longer be decrypted.</summary>
    string? ReadToken();

    void ClearToken();

    /// <summary>The decrypted Teams webhook address, or null when none is saved or it can no longer be decrypted.</summary>
    string? ReadTeamsUrl();

    /// <summary>Forgets the Teams webhook address and switches the Teams copy off.</summary>
    void ClearTeamsUrl();
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
    readonly IDataProtector _teamsProtector;

    /// <summary>Serialises load-modify-save, which LiteDB does not make atomic across two calls.</summary>
    readonly object _sync = new();

    public GmvSettingsStore(ILiteDbContext context, IDataProtectionProvider dataProtection)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dataProtection);

        _collection = context.GetCollection<Document>("gmvSettings");
        _collection.EnsureIndex(x => x.Id, unique: true);
        _protector = dataProtection.CreateProtector("YeniRPA.Gmv.TelegramToken");
        _teamsProtector = dataProtection.CreateProtector("YeniRPA.Gmv.TeamsWebhook");
    }

    public GmvSettings Load() => _collection.FindById(DocumentId)?.Data ?? new GmvSettings();

    public void Save(GmvSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var mode = update.Mode?.Trim().ToLowerInvariant();
        if (!GmvMode.IsValid(mode))
            throw new InvalidOperationException("Choose how often to notify: every N minutes, hourly, at specific times, or manual only.");

        var times = NormalizeTimes(update.Times ?? []);
        if (mode == GmvMode.Times && update.Enabled && times.Count == 0)
            throw new InvalidOperationException("Enter at least one notification time, for example 14:00.");

        if (mode is GmvMode.Hourly or GmvMode.Interval)
            ValidateWindow(update.WindowStartHour, update.WindowEndHour);

        if (update.IntervalMinutes is { } interval)
            ValidateInterval(interval);

        if (update.KeepAliveMinutes is { } keepAlive)
            ValidateKeepAlive(keepAlive);

        if (update.DailyTarget is <= 0 || update.DailyTarget is { } t && !double.IsFinite(t))
            throw new InvalidOperationException("The daily GMV target must be a positive number.");

        var newTeamsUrl = update.TeamsUrl?.Trim();
        if (!string.IsNullOrEmpty(newTeamsUrl) && TeamsSender.Validate(newTeamsUrl) is { } teamsProblem)
            throw new InvalidOperationException(teamsProblem);

        lock (_sync)
        {
            var current = Load();
            var newToken = update.Token?.Trim();
            var protectedToken = string.IsNullOrEmpty(newToken) ? current.ProtectedToken : _protector.Protect(newToken);
            var chatId = update.ChatId?.Trim() ?? "";

            if (update.Enabled && (chatId.Length == 0 || protectedToken is null))
                throw new InvalidOperationException("Enter the bot token and the chat ID before turning notifications on.");

            var protectedTeamsUrl = string.IsNullOrEmpty(newTeamsUrl) ? current.ProtectedTeamsUrl : _teamsProtector.Protect(newTeamsUrl);
            var teamsEnabled = update.TeamsEnabled ?? current.TeamsEnabled;
            if (teamsEnabled && protectedTeamsUrl is null)
                throw new InvalidOperationException("Enter the Teams webhook address before turning the Teams copy on.");

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
                    IntervalMinutes = update.IntervalMinutes ?? current.IntervalMinutes,
                    KeepAliveMinutes = update.KeepAliveMinutes ?? current.KeepAliveMinutes,
                    ChatId = chatId,
                    ProtectedToken = protectedToken,
                    DailyTarget = update.DailyTarget,
                    IncludeComparison = update.IncludeComparison ?? current.IncludeComparison,
                    TeamsEnabled = teamsEnabled,
                    ProtectedTeamsUrl = protectedTeamsUrl,
                },
            });
        }
    }

    public GmvSettings UpdateSchedule(GmvScheduleUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var mode = update.Mode?.Trim().ToLowerInvariant();
        if (mode is not null && !GmvMode.IsValid(mode))
            throw new InvalidOperationException("Choose how often to notify: every N minutes, hourly, at specific times, or manual only.");

        if (update.IntervalMinutes is { } interval)
            ValidateInterval(interval);

        if (update.KeepAliveMinutes is { } keepAlive)
            ValidateKeepAlive(keepAlive);

        lock (_sync)
        {
            var current = Load();

            var next = new GmvSettings
            {
                Enabled = update.Enabled ?? current.Enabled,
                Mode = mode ?? current.Mode,
                Times = current.Times,
                WindowStartHour = update.WindowStartHour ?? current.WindowStartHour,
                WindowEndHour = update.WindowEndHour ?? current.WindowEndHour,
                IntervalMinutes = update.IntervalMinutes ?? current.IntervalMinutes,
                KeepAliveMinutes = update.KeepAliveMinutes ?? current.KeepAliveMinutes,
                ChatId = current.ChatId,
                ProtectedToken = current.ProtectedToken,
                DailyTarget = current.DailyTarget,
                IncludeComparison = current.IncludeComparison,
                TeamsEnabled = current.TeamsEnabled,
                ProtectedTeamsUrl = current.ProtectedTeamsUrl,
            };

            if (next.Mode is GmvMode.Hourly or GmvMode.Interval)
                ValidateWindow(next.WindowStartHour, next.WindowEndHour);

            if (next.Enabled && (next.ChatId.Length == 0 || next.ProtectedToken is null))
                throw new InvalidOperationException("Enter the bot token and the chat ID in GMV Notification before turning this job on.");

            if (next.Enabled && next.Mode == GmvMode.Times && next.Times.Count == 0)
                throw new InvalidOperationException("Enter at least one notification time in GMV Notification, or pick another frequency.");

            _collection.Upsert(new Document { Id = DocumentId, Data = next });
            return next;
        }
    }

    static void ValidateWindow(int start, int end)
    {
        if (start is < 0 or > 23 || end is < 0 or > 23 || start > end)
            throw new InvalidOperationException("The window must run from an earlier hour to a later one (0-23).");
    }

    static void ValidateInterval(int minutes)
    {
        if (minutes is < GmvLimits.MinIntervalMinutes or > GmvLimits.MaxIntervalMinutes)
            throw new InvalidOperationException(
                $"The interval must be between {GmvLimits.MinIntervalMinutes} and {GmvLimits.MaxIntervalMinutes} minutes.");
    }

    static void ValidateKeepAlive(int minutes)
    {
        if (minutes is < GmvLimits.MinKeepAliveMinutes or > GmvLimits.MaxKeepAliveMinutes)
            throw new InvalidOperationException(
                $"The keep-alive interval must be between {GmvLimits.MinKeepAliveMinutes} and {GmvLimits.MaxKeepAliveMinutes} minutes, because the Marketplace ends a login after 30 idle minutes.");
    }

    public string? ReadToken()
    {
        var stored = Load().ProtectedToken;
        if (stored is null)
            return null;

        try { return _protector.Unprotect(stored); }
        catch (CryptographicException) { return null; }
    }

    public string? ReadTeamsUrl()
    {
        var stored = Load().ProtectedTeamsUrl;
        if (stored is null)
            return null;

        try { return _teamsProtector.Unprotect(stored); }
        catch (CryptographicException) { return null; }
    }

    public void ClearTeamsUrl()
    {
        lock (_sync)
        {
            var current = Load();
            current.ProtectedTeamsUrl = null;
            current.TeamsEnabled = false;
            _collection.Upsert(new Document { Id = DocumentId, Data = current });
        }
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
