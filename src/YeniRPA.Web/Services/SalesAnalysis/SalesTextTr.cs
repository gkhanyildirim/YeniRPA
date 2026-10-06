using System.Globalization;

namespace YeniRPA.Web.Services.SalesAnalysis;

/// <summary>
/// Turkish number formatting and sentence building blocks for the generated "why" text. The Sales
/// Analysis is the one Turkish-language screen in the Order Report (an explicit exception to the
/// English-UI rule), so its sentences are composed here from numbers, never from an LLM.
///
/// <para>Sentences write amounts as "7,6 milyon TL" rather than "7.556.305 ₺": a reader takes in the
/// rounded figure at a glance (the tables carry the exact one), and "TL" takes Turkish case suffixes
/// predictably ("TL'den", "TL'ye", "TL'si") where the ₺ sign does not.</para>
/// </summary>
public static class SalesTextTr
{
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>"1.234.567 ₺" — exact whole lira, for places that show a figure rather than a sentence.</summary>
    public static string Money(double value) => value.ToString("#,##0", Tr) + " ₺";

    /// <summary>
    /// "1.234.567 EUR" — exact whole amount in a named currency, for the country comparison where the
    /// two files need not share one. TRY reads as "TL"; an unknown currency gets no unit at all rather
    /// than a guessed one.
    /// </summary>
    public static string Money(double value, string? currency)
    {
        var unit = CurrencyLabel(currency);
        return value.ToString("#,##0", Tr) + (unit.Length > 0 ? " " + unit : "");
    }

    public static string CurrencyLabel(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "" :
        currency.Trim().ToUpperInvariant() is "TRY" or "TL" ? "TL" : currency.Trim().ToUpperInvariant();

    /// <summary>Signed exact money: "+12.345 ₺" / "−12.345 ₺".</summary>
    public static string SignedMoney(double value) => (value >= 0 ? "+" : "−") + Money(Math.Abs(value));

    /// <summary>Readable rounded amount for sentences: "7,6 milyon TL", "423 bin TL", "850 TL". Always unsigned.</summary>
    public static string Tl(double value)
    {
        var v = Math.Abs(value);
        if (v >= 1_000_000) return (v / 1_000_000).ToString("0.#", Tr) + " milyon TL";
        if (v >= 10_000) return (v / 1_000).ToString("0", Tr) + " bin TL";
        if (v >= 1_000) return (v / 1_000).ToString("0.#", Tr) + " bin TL";
        return v.ToString("0", Tr) + " TL";
    }

    /// <summary>"%18,2" for 0.182 (unsigned). Turkish puts the percent sign first.</summary>
    public static string Pct(double ratio, int decimals = 1) =>
        "%" + Math.Abs(ratio * 100).ToString("N" + decimals, Tr);

    /// <summary>"%33'ü", "%30,1'i", "%5,0'ı" — the percent with its possessive suffix ("…'ü / …'i").</summary>
    public static string PctOf(double ratio, int decimals = 0)
    {
        var text = Pct(ratio, decimals);
        return text + "'" + Possessive(text);
    }

    public static string Number(double value, int decimals = 0) => value.ToString("N" + decimals, Tr);

    /// <summary>"arttı" / "azaldı" / "değişmedi".</summary>
    public static string Moved(double change) => change > 0 ? "arttı" : change < 0 ? "azaldı" : "değişmedi";

    /// <summary>"yükseldi" / "düştü" / "değişmedi" — for prices and rates.</summary>
    public static string Rose(double change) => change > 0 ? "yükseldi" : change < 0 ? "düştü" : "değişmedi";

    /// <summary>
    /// The third-person possessive suffix a number takes when read aloud ("33" → "otuz üç" → "ü",
    /// "30,1" → "… bir" → "i", "5,0" → "… sıfır" → "ı"). Decided by the last number word spoken,
    /// which for a decimal is the part after the comma.
    /// </summary>
    public static string Possessive(string number)
    {
        var digits = new string(number.Reverse().TakeWhile(c => c != ',' && c != '%').Reverse().Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0 || !long.TryParse(digits, out var n))
            return "i";

        if (n == 0) return "ı";                         // sıfır
        if (n % 1000 == 0) return "i";                  // bin
        if (n % 100 == 0) return "ü";                   // yüz
        var unit = n % 10;
        if (unit != 0)
            return unit switch { 1 => "i", 2 => "si", 3 => "ü", 4 => "ü", 5 => "i", 6 => "sı", 7 => "si", 8 => "i", _ => "u" };
        return (n % 100 / 10) switch { 1 => "u", 2 => "si", 3 => "u", 4 => "ı", 5 => "si", 6 => "ı", 7 => "i", 8 => "i", _ => "ı" };
    }

    /// <summary>
    /// "SMARTPHONES" → "Smartphones". The labels are English, so they are lower-cased with the
    /// invariant culture: Turkish casing would turn "MOBILE" into "Mobıle".
    /// </summary>
    public static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(Bilinmiyor)";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Trim().ToLowerInvariant());
    }

    public static string OrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "(Bilinmiyor)" : value.Trim();
}
