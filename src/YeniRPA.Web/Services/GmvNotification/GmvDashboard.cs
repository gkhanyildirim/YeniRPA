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
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); }

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var start = new DateTimeOffset(now.Date, zone.GetUtcOffset(now.Date));
        var nextDay = now.Date.AddDays(1);
        return (start, new DateTimeOffset(nextDay, zone.GetUtcOffset(nextDay)));
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

        static string Stamp(DateTimeOffset value) =>
            Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));

        return $"{Host}/marketplace-dashboard/private/sales?endDate={Stamp(to)}" +
               "&includeShippingCharges=true&includeTaxes=true&platformModel=MARKETPLACE" +
               $"&showDebited=ALL&showShipped=ALL&startDate={Stamp(from)}";
    }
}
