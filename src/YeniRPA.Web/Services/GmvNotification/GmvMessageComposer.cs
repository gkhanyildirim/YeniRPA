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

    public const string UnknownCommand = "Bilinmeyen komut. Güncel GMV için /gmv, geçen haftayla kıyas için /weekgmv yazın.";

    /// <summary>The answer to "/gmv" when no figure could be read. The reason is the operator-facing one from the reader.</summary>
    public static string CommandFailed(string reason) => $"GMV şu an okunamadı: {reason}";

    public const string Test ="Test bildirimi: GMV bildirimleri bu sohbete gönderilecek.";

    /// <summary>"€58.790" — rounded to whole euros, which is how the dashboard shows it.</summary>
    public static string Money(double gmv) => "€" + Math.Round(gmv).ToString("N0", Turkish);

    /// <summary>
    /// "Dün bu saatte: €54.200 (▲ €4.590, %8)." — the comparison line. A past figure that could not be
    /// read says so; it is never shown as zero. The percentage is left out when the earlier figure is
    /// zero, because a percentage of nothing says nothing.
    /// </summary>
    public static string ComparisonLine(GmvComparison comparison)
    {
        var label = comparison.DaysBack switch
        {
            1 => "Dün bu saatte",
            7 => "Geçen hafta bu saatte",
            var days => $"{days} gün önce bu saatte",
        };

        if (comparison.Past is not { } past)
            return $"{label}: veri alınamadı.";

        var difference = comparison.Current - past;
        if (Math.Round(difference) == 0)
            return $"{label}: {Money(past)} (değişim yok).";

        var arrow = difference > 0 ? "▲" : "▼";
        var detail = $"{arrow} {Money(Math.Abs(difference))}";
        if (past > 0)
            detail += $", %{Math.Round(Math.Abs(difference) / past * 100).ToString("0", Turkish)}";

        return $"{label}: {Money(past)} ({detail}).";
    }

    public static string Report(double gmv, DateTime at, double? dailyTarget, GmvComparison? comparison = null)
    {
        var text = $"{at.ToString("dd MMMM yyyy", Turkish)}, {at.ToString("HH:mm", CultureInfo.InvariantCulture)} " +
                   $"itibarıyla güncel GMV: {Money(gmv)}.";

        if (comparison is not null)
            text += " " + ComparisonLine(comparison);

        if (dailyTarget is > 0)
        {
            var percent = Math.Round(gmv / dailyTarget.Value * 100);
            text += $" Günlük hedefe göre %{percent.ToString("0", Turkish)} seviyesinde.";
        }

        return text;
    }
}
