using System.Globalization;

namespace YeniRPA.Web.Services.GmvNotification;

/// <summary>
/// The Telegram texts. Short, plain Turkish. No Turkish suffix is attached to a number, because the
/// right suffix depends on the digits ("%72'si", "%100'ü") and a wrong one reads worse than a wording
/// that needs none.
/// </summary>
public static class GmvMessageComposer
{
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public const string SessionExpired =
        "GMV bildirimi gönderilemedi. Marketplace paneli oturumunuz sona ermiş olabilir. Lütfen yeniden giriş yapın.";

    public const string UnknownCommand = "Bilinmeyen komut. Güncel GMV için /gmv, dün ve geçen haftayla kıyas için /weekgmv yazın.";

    /// <summary>The answer to "/gmv" when no figure could be read. The reason is the operator-facing one from the reader.</summary>
    public static string CommandFailed(string reason) => $"GMV şu an okunamadı: {reason}";

    public const string Test ="Test bildirimi: GMV bildirimleri bu sohbete gönderilecek.";

    /// <summary>"€18,233" — rounded to whole euros, which is how the dashboard shows it.</summary>
    public static string Money(double gmv) => "€" + Math.Round(gmv).ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// How <paramref name="current"/> stands against an earlier figure: "▲ +7%", "▼ -3%" or "0%". An
    /// earlier figure that could not be read says so and is never shown as zero; an earlier zero has no
    /// percentage ("—"), because a percentage of nothing says nothing.
    /// </summary>
    public static string Change(double current, double? earlier, string earlierLabel)
    {
        if (earlier is not { } past)
            return "veri yok";

        // "+€1,193" / "-€1,267": the change in euros, so the size of the move is visible next to its percentage.
        var difference = Math.Round(current - past);
        var amount = difference == 0 ? Money(0) : (difference > 0 ? "+" : "-") + Money(Math.Abs(difference));
        var detail = $"({earlierLabel} {Money(past)}, {amount})";

        if (past <= 0)
            return $"— {detail}";

        var percent = Math.Round((current - past) / past * 100);
        if (percent == 0)
            return $"0% {detail}";

        var text = Math.Abs(percent).ToString("0", CultureInfo.InvariantCulture);
        return (percent > 0 ? $"▲ +{text}%" : $"▼ -{text}%") + " " + detail;
    }

    /// <summary>
    /// <code>
    /// 12 Ekim 2026;
    /// GMV (11:45): €18,233 | DoD ▲ +7% (Y €17,040, +€1,193) | WoW ▼ -3% (LW €18,800, -€567) | Hedef %72
    /// </code>
    /// DoD is against yesterday at this time, WoW against the same weekday last week at this time; each
    /// shows the earlier GMV and the change in euros. The comparison and the target part are left out
    /// when there is nothing to say.
    /// </summary>
    public static string Report(double gmv, DateTime at, double? dailyTarget, GmvComparison? comparison = null)
    {
        var text = $"{at.ToString("d MMMM yyyy", Turkish)};\n" +
                   $"GMV ({at.ToString("HH:mm", CultureInfo.InvariantCulture)}): {Money(gmv)}";

        if (comparison is not null)
            text += $" | DoD {Change(gmv, comparison.DayAgo, "Y")} | WoW {Change(gmv, comparison.WeekAgo, "LW")}";

        if (dailyTarget is > 0)
        {
            var percent = Math.Round(gmv / dailyTarget.Value * 100);
            text += $" | Hedef %{percent.ToString("0", CultureInfo.InvariantCulture)}";
        }

        return text;
    }
}
