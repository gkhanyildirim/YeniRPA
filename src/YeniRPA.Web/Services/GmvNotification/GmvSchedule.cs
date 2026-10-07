using System.Globalization;

namespace YeniRPA.Web.Services.GmvNotification;

/// <summary>
/// Which notification time is due now. Pure: it takes the clock as an argument so the rules can be
/// tested without waiting for one.
///
/// <para>A time counts as due from its minute until <see cref="GraceMinutes"/> later. The worker ticks
/// every 30 seconds, so the grace only has to cover a busy tick or a slow read; it is not a way to
/// catch up on a time the app was closed for — those are recorded as missed instead, because a message
/// that arrives an hour late under a "14:00" heading would say something untrue.</para>
/// </summary>
public static class GmvSchedule
{
    public const int GraceMinutes = 10;

    /// <summary>The configured times of day, ascending. Empty for manual mode.</summary>
    public static IReadOnlyList<TimeOnly> SlotTimes(GmvSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        switch (settings.Mode)
        {
            case GmvMode.Hourly:
                return [.. Enumerable.Range(settings.WindowStartHour, Math.Max(0, settings.WindowEndHour - settings.WindowStartHour + 1))
                    .Where(h => h is >= 0 and <= 23)
                    .Select(h => new TimeOnly(h, 0))];

            case GmvMode.Times:
                return [.. settings.Times
                    .Select(t => TimeOnly.TryParseExact(t, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? (TimeOnly?)time : null)
                    .Where(t => t is not null)
                    .Select(t => t!.Value)
                    .Distinct()
                    .Order()];

            default:
                return [];
        }
    }

    /// <summary>The slot whose time has come and whose grace has not run out, or null.</summary>
    public static GmvSlot? Due(GmvSettings settings, DateTime now)
    {
        var time = TimeOnly.FromDateTime(now);

        foreach (var slot in SlotTimes(settings).Reverse())
        {
            if (time >= slot && (time - slot).TotalMinutes < GraceMinutes)
                return MakeSlot(DateOnly.FromDateTime(now), slot);
        }

        return null;
    }

    /// <summary>
    /// Today's specific times that passed without a chance to run, up to <paramref name="lookbackHours"/>
    /// ago. Only for "specific times" mode: in hourly mode a closed app would produce a long list of
    /// missed hours that nobody was waiting for.
    /// </summary>
    public static IReadOnlyList<GmvSlot> PassedToday(GmvSettings settings, DateTime now, int lookbackHours)
    {
        if (settings.Mode != GmvMode.Times)
            return [];

        var today = DateOnly.FromDateTime(now);
        var time = TimeOnly.FromDateTime(now);

        return [.. SlotTimes(settings)
            .Where(slot => (time - slot).TotalMinutes >= GraceMinutes && time >= slot
                           && (time - slot).TotalHours <= lookbackHours)
            .Select(slot => MakeSlot(today, slot))];
    }

    static GmvSlot MakeSlot(DateOnly day, TimeOnly time) => new(
        $"{day:yyyy-MM-dd} {time.ToString("HH:mm", CultureInfo.InvariantCulture)}",
        time.ToString("HH:mm", CultureInfo.InvariantCulture),
        day.ToDateTime(time));
}
