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

    public const string Test = "Test bildirimi: GMV bildirimleri bu sohbete gönderilecek.";

    /// <summary>"€58.790" — rounded to whole euros, which is how the dashboard shows it.</summary>
    public static string Money(double gmv) => "€" + Math.Round(gmv).ToString("N0", Turkish);

    public static string Report(double gmv, DateTime at, double? dailyTarget)
    {
        var text = $"{at.ToString("dd MMMM yyyy", Turkish)}, {at.ToString("HH:mm", CultureInfo.InvariantCulture)} " +
                   $"itibarıyla güncel GMV: {Money(gmv)}.";

        if (dailyTarget is > 0)
        {
            var percent = Math.Round(gmv / dailyTarget.Value * 100);
            text += $" Günlük hedefe göre %{percent.ToString("0", Turkish)} seviyesinde.";
        }

        return text;
    }
}
