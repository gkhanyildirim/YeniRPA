using System.Globalization;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

public enum PosCardKind
{
    Debit,
    Credit,
    Foreign,
    /// <summary>A non-blank POS Kart Tipi this app does not know — counted in the total, in no bucket.</summary>
    Other
}

/// <summary>
/// Decides, per Bulut Tahsilat row, whether the card is debit / credit / foreign and whether it is
/// "on us" for the POS it was charged on.
///
/// <para><b>Card kind.</b> Foreign when "Kart Banka" is YABANCI BANKA, or when "POS Kart Tipi" is blank
/// (such rows have no usable type — they are counted with foreign). Otherwise KREDİ KARTI is credit,
/// and BANKA KARTI and ÖN ÖDEMELİ KART are debit (a prepaid card behaves as debit).</para>
///
/// <para><b>On us.</b> The card bank is the POS bank itself, or is listed under that POS bank's
/// <see cref="PosOnUsRule"/> (the Bonus family: on a Garanti POS, ING and DENİZBANK cards are on us).
/// A foreign card is never on us.</para>
/// </summary>
public sealed class PosCardClassifier
{
    public const string ForeignBank = "YABANCI BANKA";

    static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    readonly Dictionary<string, HashSet<string>> _onUs;

    public PosCardClassifier(PosRateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _onUs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var rule in settings.OnUsRules ?? [])
            _onUs[Norm(rule.PosBank)] = [.. (rule.CardBanks ?? []).Select(Norm)];
    }

    /// <summary>Upper-cased in Turkish rules and trimmed, so "İş Bankası" and "İŞ BANKASI" are one name.</summary>
    public static string Norm(string? text) => (text ?? "").Trim().ToUpper(Tr);

    public PosCardKind Kind(PosReconciliationRow row)
    {
        var type = Norm(row.CardType);
        if (Norm(row.KartBanka) == ForeignBank || type.Length == 0)
            return PosCardKind.Foreign;

        if (type.Contains("KREDİ", StringComparison.Ordinal))
            return PosCardKind.Credit;
        if (type.Contains("BANKA", StringComparison.Ordinal) || type.Contains("ÖDEMELİ", StringComparison.Ordinal))
            return PosCardKind.Debit;

        return PosCardKind.Other;
    }

    public bool IsOnUs(PosReconciliationRow row)
    {
        var pos = Norm(row.PosBanka);
        var card = Norm(row.KartBanka);
        if (pos.Length == 0 || card.Length == 0 || card == ForeignBank)
            return false;

        return card == pos || (_onUs.TryGetValue(pos, out var family) && family.Contains(card));
    }
}
