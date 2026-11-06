using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// POS Reconciliation — editable bank rate table and the "on us" rule set.
//
// Rates are stored as fractions (0.009 = 0.9 %), exactly as the operator's "banka oranlar"
// workbook writes them. The card-type split (debit / credit / foreign, on us / not on us) is NOT
// stored — it is computed from each report's rows; only the rates and the on-us family rules,
// which change by hand and by contract, are persisted.
// ---------------------------------------------------------------------------

/// <summary>One installment column's rate for a bank ("2 taksit" -> 0.022). Null = not agreed / blank.</summary>
public sealed record PosInstallmentRate(
    [property: JsonPropertyName("installment")] int Installment,
    [property: JsonPropertyName("rate")] decimal? Rate);

/// <summary>One POS bank's row of the rate table.</summary>
public sealed record PosBankRate(
    [property: JsonPropertyName("posBank")] string PosBank,
    [property: JsonPropertyName("foreignRate")] decimal? ForeignRate,
    [property: JsonPropertyName("oneOnUsDebit")] decimal? OneOnUsDebit,
    [property: JsonPropertyName("oneOnUsCredit")] decimal? OneOnUsCredit,
    [property: JsonPropertyName("oneNotOnUsDebit")] decimal? OneNotOnUsDebit,
    [property: JsonPropertyName("installments")] List<PosInstallmentRate> Installments,
    // Added after the first release: defaulted so a stored document / backup without it still loads.
    [property: JsonPropertyName("oneNotOnUsCredit")] decimal? OneNotOnUsCredit = null);

/// <summary>
/// Card banks that count as "on us" for a POS bank besides the bank itself — e.g. on a GARANTİ BBVA
/// POS, ING and DENİZBANK cards belong to the Bonus family and are on us, not "not on us".
/// </summary>
public sealed record PosOnUsRule(
    [property: JsonPropertyName("posBank")] string PosBank,
    [property: JsonPropertyName("cardBanks")] List<string> CardBanks);

/// <summary>The single document <c>PosRateStore</c> owns.</summary>
public sealed record PosRateSettings(
    [property: JsonPropertyName("banks")] List<PosBankRate> Banks,
    [property: JsonPropertyName("onUsRules")] List<PosOnUsRule> OnUsRules);
