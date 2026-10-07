using System.Globalization;
using System.Text.RegularExpressions;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Renders one seller's monthly target progress into the mail and WhatsApp texts.
///
/// <para>Plain <c>{placeholder}</c> substitution, the same as the other message builders: an unknown
/// token is reported and left in the text rather than silently removed. Amounts and the percentage
/// are written the Turkish way (<c>15.000.000</c>, <c>3,3</c>) because the recipients are Turkish
/// sellers. Templates are Turkish for the same reason and carry no emoticon sequences — WhatsApp's
/// composer converts them as they are typed, which would fail the runner's read-back check.</para>
/// </summary>
public static partial class SellerTargetMessageBuilder
{
    public const string DefaultMailSubject = "{month} Satış Hedefi Bilgilendirmesi";

    /// <summary>The projected month-end completion, in percent, a seller has to reach to get the
    /// "on track" text. 100 = on course to hit the target exactly.</summary>
    public const double DefaultThreshold = 100;

    // One text per outcome, shared by both channels: the seller reads the same message in their inbox
    // and in the group. "%{percent} kadarı" rather than "%3,3'ü": a Turkish suffix follows the last
    // vowel of the spoken number, which a placeholder cannot know, so the sentence needs none.
    public const string DefaultBelowBody =
        """
        Sayın {seller} Yetkilisi,

        {monthName} ayı için belirlenen {target} TL’lik satış hedefinizin ilk {days} gününde {current} TL ciroya ulaşılmış olup, hedefin %{percent} kadarı tamamlanmıştır. Mevcut gidişatla ay sonunda hedefinizin %{forecastPercent} kadarını gerçekleştireceksiniz.

        Hedefi yakalayabilmeniz için tüm mağazanıza tanımlanan komisyon oranına göre fiyatlarınızı kontrol etmenizi rica ederiz.

        İyi çalışmalar, bol satışlar dileriz.

        Saygılarımızla,
        """;

    public const string DefaultAboveBody =
        """
        Sayın {seller} Yetkilisi,

        {monthName} ayı için belirlenen {target} TL’lik satış hedefinizin ilk {days} gününde {current} TL ciroya ulaşılmış olup, hedefin %{percent} kadarı tamamlanmıştır. Mevcut gidişatla ay sonunda hedefinizin %{forecastPercent} kadarını gerçekleştireceksiniz.

        Satışlarınız çok iyi gidiyor, bu tempoyu koruyarak devam etmenizi rica ederiz.

        İyi çalışmalar, bol satışlar dileriz.

        Saygılarımızla,
        """;

    public static readonly string[] KnownPlaceholders =
        ["{seller}", "{month}", "{monthName}", "{target}", "{current}", "{percent}", "{remaining}", "{days}", "{forecastPercent}"];
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    [GeneratedRegex(@"\{[A-Za-z][A-Za-z0-9]*\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>The completion percentage, uncapped — a seller above target reads 112,4.</summary>
    public static double Percent(double target, double current) => target > 0 ? current / target * 100 : 0;

    /// <summary>Month-end projection as a percentage of the target: the daily run rate so far carried
    /// over the whole month, the same arithmetic as the workbook's own (E/5)*31 column.</summary>
    public static double ForecastPercent(SellerTargetRow row)
    {
        var days = row.Days is > 0 ? row.Days.Value : 1;
        return row.Target > 0 && row.Current.HasValue ? row.Current.Value / days * row.DaysInMonth / row.Target * 100 : 0;
    }

    /// <summary>Whether a seller's projected month-end completion falls short of the threshold. Compared
    /// at the one decimal the seller reads, so "%99,9" is never sent the on-track text because of a
    /// hidden 99,96.</summary>
    public static bool IsBelowThreshold(SellerTargetRow row, double threshold) =>
        Math.Round(ForecastPercent(row), 1, MidpointRounding.AwayFromZero) < threshold;

    public static SellerTargetMessage Render(
        SellerTargetRow row,
        string month,
        string? subjectTemplate,
        string? belowBody,
        string? aboveBody,
        double threshold)
    {
        ArgumentNullException.ThrowIfNull(row);

        var current = row.Current ?? 0;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{seller}"] = row.SellerName.Trim(),
            ["{month}"] = month,
            ["{monthName}"] = month.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? month,
            ["{days}"] = (row.Days ?? 0).ToString(Turkish),
            ["{forecastPercent}"] = ForecastPercent(row).ToString("0.#", Turkish),
            ["{target}"] = row.Target.ToString("N0", Turkish),
            ["{current}"] = current.ToString("N0", Turkish),
            ["{percent}"] = Percent(row.Target, current).ToString("0.#", Turkish),
            ["{remaining}"] = Math.Max(0, row.Target - current).ToString("N0", Turkish),
        };

        var below = IsBelowThreshold(row, threshold);
        var subject = Fill(Pick(subjectTemplate, DefaultMailSubject), values);
        var body = below
            ? Fill(Pick(belowBody, DefaultBelowBody), values)
            : Fill(Pick(aboveBody, DefaultAboveBody), values);

        var unknown = PlaceholderPattern().Matches(subject + "\n" + body)
            .Select(m => m.Value)
            .Where(token => !values.ContainsKey(token))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new SellerTargetMessage(row.SellerId, row.SellerName, subject, body, body, unknown, below);
    }
    static string Pick(string? template, string fallback) =>
        string.IsNullOrWhiteSpace(template) ? fallback : template.Replace("\r\n", "\n").Replace("\r", "\n");

    static string Fill(string template, Dictionary<string, string> values)
    {
        foreach (var (token, value) in values)
            template = template.Replace(token, value, StringComparison.Ordinal);

        return template.Trim();
    }
}
