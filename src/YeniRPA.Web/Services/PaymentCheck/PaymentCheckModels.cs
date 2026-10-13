namespace YeniRPA.Web.Services.PaymentCheck;

public static class PaymentCheckTrigger
{
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";
}

public static class PaymentCheckStatus
{
    /// <summary>The card-entry page on odeme.mediamarkt.com.tr opened.</summary>
    public const string Ok = "ok";
    /// <summary>Logged in with items in the cart, but the payment page did not open.</summary>
    public const string Unreachable = "unreachable";
    /// <summary>No saved session, or the storefront asked for a login. Not a payment problem.</summary>
    public const string NotLoggedIn = "not-logged-in";
    /// <summary>Nothing in the cart, so the payment page cannot be reached. Not a payment problem.</summary>
    public const string CartEmpty = "cart-empty";
    /// <summary>The check itself could not run (browser could not start, and so on).</summary>
    public const string Error = "error";
}

public static class PaymentCheckLimits
{
    public const int DefaultIntervalMinutes = 30;
    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 240;
}

/// <summary>One document in the <c>paymentCheckSettings</c> collection. Telegram token and chat are
/// shared with GMV Notification and are not stored here.</summary>
public sealed class PaymentCheckSettings
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = PaymentCheckLimits.DefaultIntervalMinutes;

    /// <summary>First and last hour (0-23, both inclusive) in which scheduled checks run.</summary>
    public int WindowStartHour { get; set; } = 9;
    public int WindowEndHour { get; set; } = 23;
}

public sealed record PaymentCheckSettingsUpdate(
    bool? Enabled = null,
    int? IntervalMinutes = null,
    int? WindowStartHour = null,
    int? WindowEndHour = null);

public sealed record PaymentCheckResult(string Status, string? Step, string? Detail, long DurationMs)
{
    public bool IsOk => Status == PaymentCheckStatus.Ok;
}

public sealed class PaymentCheckHistoryEntry
{
    public int Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Trigger { get; set; } = PaymentCheckTrigger.Scheduled;
    public string Status { get; set; } = PaymentCheckStatus.Error;
    public string? Step { get; set; }
    public string? Detail { get; set; }
    public long DurationMs { get; set; }

    /// <summary>Whether the Telegram message went out. False with <see cref="NotifyNote"/> saying why.</summary>
    public bool Notified { get; set; }
    public string? NotifyNote { get; set; }
}

/// <summary>When the next scheduled check is due. Pure, so the clock is an argument.</summary>
public static class PaymentCheckSchedule
{
    public static bool InWindow(PaymentCheckSettings settings, DateTime local) =>
        local.Hour >= settings.WindowStartHour && local.Hour <= settings.WindowEndHour;

    public static bool IsDue(PaymentCheckSettings settings, DateTime? lastLocal, DateTime nowLocal) =>
        settings.Enabled
        && InWindow(settings, nowLocal)
        && (lastLocal is null || nowLocal >= lastLocal.Value.AddMinutes(settings.IntervalMinutes));

    /// <summary>The next time a check will run, or null when the job is off.</summary>
    public static DateTime? Next(PaymentCheckSettings settings, DateTime? lastLocal, DateTime nowLocal)
    {
        if (!settings.Enabled)
            return null;

        var candidate = lastLocal is null ? nowLocal : lastLocal.Value.AddMinutes(settings.IntervalMinutes);
        if (candidate < nowLocal)
            candidate = nowLocal;

        if (InWindow(settings, candidate))
            return candidate;

        return candidate.Hour < settings.WindowStartHour
            ? candidate.Date.AddHours(settings.WindowStartHour)
            : candidate.Date.AddDays(1).AddHours(settings.WindowStartHour);
    }
}
