namespace YeniRPA.Web.Services.GmvNotification;

public static class GmvMode
{
    public const string Hourly = "hourly";
    public const string Times = "times";
    public const string Manual = "manual";

    /// <summary>A message every N minutes inside the hourly window.</summary>
    public const string Interval = "interval";

    public static bool IsValid(string? mode) => mode is Hourly or Times or Manual or Interval;
}

public static class GmvTrigger
{
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";
    public const string Test = "test";
}

public static class GmvStatus
{
    /// <summary>The GMV was read and the Telegram message went out.</summary>
    public const string Sent = "sent";
    /// <summary>The GMV was read for the operator's eyes only; nothing was sent.</summary>
    public const string Checked = "checked";
    public const string Test = "test";
    /// <summary>Reading or sending failed. Nothing wrong or empty was sent to Telegram.</summary>
    public const string Failed = "failed";
    /// <summary>The login had expired and the "sign in again" warning was sent instead.</summary>
    public const string Session = "session";
    public const string Skipped = "skipped";
    /// <summary>A scheduled time passed while the app was not running.</summary>
    public const string Missed = "missed";
}

/// <summary>
/// One document in the <c>gmvSettings</c> collection. The bot token is only ever stored encrypted
/// (<see cref="ProtectedToken"/>) and is never sent back to the browser.
/// </summary>
public sealed class GmvSettings
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = GmvMode.Times;

    /// <summary>"HH:mm", local time of this PC, for <see cref="GmvMode.Times"/>.</summary>
    public List<string> Times { get; set; } = [];

    /// <summary>For <see cref="GmvMode.Hourly"/>: a message at every full hour from start to end, inclusive.</summary>
    public int WindowStartHour { get; set; } = 9;
    public int WindowEndHour { get; set; } = 19;

    /// <summary>For <see cref="GmvMode.Interval"/>: minutes between messages inside the window.</summary>
    public int IntervalMinutes { get; set; } = GmvLimits.DefaultIntervalMinutes;

    /// <summary>Minutes between the light requests that keep the Marketplace login alive.</summary>
    public int KeepAliveMinutes { get; set; } = GmvLimits.DefaultKeepAliveMinutes;

    public string ChatId { get; set; } = "";
    public string? ProtectedToken { get; set; }
    public double? DailyTarget { get; set; }

    /// <summary>Adds "yesterday at this time" to every message. Documents saved before this existed read as on.</summary>
    public bool IncludeComparison { get; set; } = true;

    /// <summary>Also posts every message to a Teams chat through a Power Automate workflow's webhook.</summary>
    public bool TeamsEnabled { get; set; }

    /// <summary>The workflow's webhook address, encrypted. Whoever holds it can post to the chat, so it is a secret like the bot token.</summary>
    public string? ProtectedTeamsUrl { get; set; }
}

public static class GmvLimits
{
    public const int DefaultIntervalMinutes = 15;
    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 120;

    public const int DefaultKeepAliveMinutes = 10;
    public const int MinKeepAliveMinutes = 1;

    /// <summary>The Marketplace ends a login after 30 idle minutes; the keep-alive has to stay under it.</summary>
    public const int MaxKeepAliveMinutes = 25;
}

public sealed record GmvSettingsUpdate(
    bool Enabled,
    string? Mode,
    IReadOnlyList<string>? Times,
    int WindowStartHour,
    int WindowEndHour,
    string? ChatId,
    string? Token,
    double? DailyTarget,
    int? IntervalMinutes = null,
    int? KeepAliveMinutes = null,
    bool? IncludeComparison = null,
    string? TeamsUrl = null,
    bool? TeamsEnabled = null);

/// <summary>The schedule-only part of the settings, edited from the Jobs screen. Null keeps the stored value.</summary>
public sealed record GmvScheduleUpdate(
    bool? Enabled = null,
    string? Mode = null,
    int? IntervalMinutes = null,
    int? WindowStartHour = null,
    int? WindowEndHour = null,
    int? KeepAliveMinutes = null);

public sealed class GmvHistoryEntry
{
    public int Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Trigger { get; set; } = GmvTrigger.Scheduled;

    /// <summary>"yyyy-MM-dd HH:mm" of the scheduled time this entry answers. Null for manual entries.</summary>
    public string? SlotKey { get; set; }

    public double? Gmv { get; set; }
    public string Status { get; set; } = GmvStatus.Failed;
    public string? Error { get; set; }
}

public sealed record GmvSlot(string Key, string Label, DateTime At);

public sealed record GmvReading(double Gmv, DateTimeOffset At);

/// <summary>
/// Today's GMV next to the GMV of the same stretch of the day yesterday (<see cref="DayAgo"/>) and on
/// the same weekday last week (<see cref="WeekAgo"/>). Either is null when it could not be read.
/// </summary>
public sealed record GmvComparison(double Current, double? DayAgo, double? WeekAgo);

/// <summary>"none" (no saved session), "unknown" (not checked yet), "valid" or "expired".</summary>
public sealed record GmvSessionStatus(string State, DateTime? CheckedUtc);

/// <summary>The saved Marketplace login no longer works (or there is none). The operator has to sign in again.</summary>
public sealed class GmvSessionExpiredException(string message) : Exception(message);

/// <summary>The dashboard answered, but no usable GMV came out of it. Nothing may be sent for this.</summary>
public sealed class GmvUnavailableException : Exception
{
    public GmvUnavailableException(string message) : base(message) { }
    public GmvUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Telegram refused or could not be reached. The message never contains the bot token.</summary>
public sealed class GmvSendException(string message) : Exception(message);
