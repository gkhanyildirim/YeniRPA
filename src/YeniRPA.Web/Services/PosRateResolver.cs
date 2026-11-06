using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// The commission rate the bank-rate table says a Bulut Tahsilat row should have been charged.
///
/// <para>Looked up by the row's POS bank, then: a foreign card takes the bank's foreign rate at any
/// installment count; a single payment (Taksit 0 or 1) takes the on us / not on us × debit / credit
/// cell; two or more installments take that installment's rate, which the table does not split by card
/// type. A card of unknown type, a bank or installment count the table lacks, and a blank cell all
/// give <c>null</c> — never 0, which is a real rate (HALKBANK's foreign card rate).</para>
/// </summary>
public sealed class PosRateResolver
{
    readonly Dictionary<string, PosBankRate> _banks;
    readonly PosCardClassifier _classifier;

    public PosRateResolver(PosRateSettings settings, PosCardClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(classifier);

        _classifier = classifier;
        _banks = new Dictionary<string, PosBankRate>(StringComparer.Ordinal);
        foreach (var bank in settings.Banks ?? [])
            _banks.TryAdd(PosCardClassifier.Norm(bank.PosBank), bank);
    }

    public decimal? Resolve(PosReconciliationRow row)
    {
        if (!_banks.TryGetValue(PosCardClassifier.Norm(row.PosBanka), out var bank))
            return null;

        var kind = _classifier.Kind(row);
        if (kind == PosCardKind.Foreign)
            return bank.ForeignRate;
        if (kind == PosCardKind.Other)
            return null;

        if (row.Taksit <= 1)
        {
            var onUs = _classifier.IsOnUs(row);
            return (onUs, kind) switch
            {
                (true, PosCardKind.Debit) => bank.OneOnUsDebit,
                (true, _) => bank.OneOnUsCredit,
                (false, PosCardKind.Debit) => bank.OneNotOnUsDebit,
                _ => bank.OneNotOnUsCredit,
            };
        }

        return bank.Installments?.FirstOrDefault(i => i.Installment == row.Taksit)?.Rate;
    }
}
