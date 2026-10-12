using System.Globalization;

namespace YeniRPA.Web.Services.GmvNotification;

/// <summary>Where the Mirakl dashboard's GMV comes from, and which switches it is read with.</summary>
public static class GmvDashboard
{
    public const string Host = "https://mediamarktsaturn.mirakl.net";

    /// <summary>
    /// "Today" the way the dashboard counts it: midnight to the next midnight in the marketplace's own
    /// time zone (Central European Time), not this PC's. The dashboard shows 06 Oct 23:00 to
    /// 07 Oct 22:59 in Turkish time for the same day.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) PlatformToday()
    {
        var zone = PlatformZone();
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        return FullDay(now, 0, zone);
    }

    static TimeZoneInfo PlatformZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); }
    }

    /// <summary>The current moment, to be passed to <see cref="ComparisonRange"/> for every read of one message.</summary>
    public static DateTimeOffset PlatformNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, PlatformZone());

    /// <summary>
    /// The whole marketplace day <paramref name="daysBack"/> calendar days before <paramref name="now"/>:
    /// midnight to the next midnight. Counted in calendar days, not 24-hour blocks, so a day with a
    /// clock change still starts at its own midnight.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) FullDay(DateTimeOffset now, int daysBack, TimeZoneInfo? zone = null)
    {
        zone ??= PlatformZone();
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var day = local.Date.AddDays(-daysBack);
        var nextDay = day.AddDays(1);
        return (new DateTimeOffset(day, zone.GetUtcOffset(day)), new DateTimeOffset(nextDay, zone.GetUtcOffset(nextDay)));
    }

    /// <summary>
    /// The same stretch of the day <paramref name="daysBack"/> days earlier: that day's midnight up to
    /// the time of day <paramref name="now"/> has reached. This is what "yesterday at this time" and
    /// "last week at this time" are measured over.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) ComparisonRange(DateTimeOffset now, int daysBack, TimeZoneInfo? zone = null)
    {
        zone ??= PlatformZone();
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var day = local.Date.AddDays(-daysBack);

        var until = day + local.TimeOfDay;
        // A time that does not exist on a spring-forward day: use the hour after it.
        if (zone.IsInvalidTime(until))
            until = until.AddHours(1);

        return (new DateTimeOffset(day, zone.GetUtcOffset(day)), new DateTimeOffset(until, zone.GetUtcOffset(until)));
    }

    /// <summary>
    /// The sales endpoint for today with the operator's own GMV settings written out in full: all
    /// orders (paid and unpaid, shipped and unshipped), taxes and shipping included. They are sent
    /// explicitly because the dashboard otherwise applies whatever its browser happens to have saved —
    /// the saved login's browser state showed shipped orders only, without taxes, and read 15.617
    /// where the operator's own screen read 77.074.
    /// </summary>
    public static string SalesUrl()
    {
        var (from, to) = PlatformToday();
        return SalesUrl(from, to);
    }

    /// <summary>The same request over any stretch of time, for the "yesterday / last week at this time" comparisons.</summary>
    public static string SalesUrl(DateTimeOffset from, DateTimeOffset to)
    {
        static string Stamp(DateTimeOffset value) =>
            Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));

        return $"{Host}/marketplace-dashboard/private/sales?endDate={Stamp(to)}" +
               "&includeShippingCharges=true&includeTaxes=true&platformModel=MARKETPLACE" +
               $"&showDebited=ALL&showShipped=ALL&startDate={Stamp(from)}";
    }
}
