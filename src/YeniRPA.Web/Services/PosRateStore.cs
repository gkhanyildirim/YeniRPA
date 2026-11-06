using LiteDB;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>The instance surface <see cref="PosRateStore"/> exposes through DI.</summary>
public interface IPosRateStore
{
    PosRateSettings Load();
    void Save(PosRateSettings settings);
}

/// <summary>
/// Owns the POS bank rate table and the "on us" family rules, in the <c>posRates</c> collection of the
/// shared LiteDB database (<see cref="ILiteDbContext"/>). One document holding the whole set, like
/// <see cref="SellerNotificationStore"/> — the editor always posts the full table back.
///
/// <para>Until the first save the document does not exist and the values from the operator's
/// "gökhan banka oranlar" workbook are returned, so the report has rates on a fresh install.</para>
/// </summary>
public sealed class PosRateStore : IPosRateStore
{
    const int DocumentId = 1;

    public sealed class Document
    {
        public int Id { get; set; }
        public PosRateSettings Data { get; set; } = null!;
    }

    readonly ILiteCollection<Document> _collection;
    readonly object _sync = new();

    public PosRateStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _collection = context.GetCollection<Document>("posRates");
        _collection.EnsureIndex(x => x.Id, unique: true);
    }

    public PosRateSettings Load()
    {
        var data = _collection.FindById(DocumentId)?.Data;
        return data is null ? Defaults() : Normalize(data);
    }

    public void Save(PosRateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
            _collection.Upsert(new Document { Id = DocumentId, Data = Normalize(settings) });
    }

    /// <summary>Trims names, drops blank/duplicate entries and orders installments, so every consumer
    /// (sheet builder, editor, Excel export) can rely on a clean shape.</summary>
    public static PosRateSettings Normalize(PosRateSettings settings)
    {
        var banks = new List<PosBankRate>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var bank in settings.Banks ?? [])
        {
            var name = (bank.PosBank ?? "").Trim();
            if (name.Length == 0 || !seen.Add(name))
                continue;

            var installments = (bank.Installments ?? [])
                .Where(i => i.Installment > 0)
                .GroupBy(i => i.Installment)
                .Select(g => g.First())
                .OrderBy(i => i.Installment)
                .ToList();

            banks.Add(bank with { PosBank = name, Installments = installments });
        }

        var rules = new List<PosOnUsRule>();
        var seenRules = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var rule in settings.OnUsRules ?? [])
        {
            var name = (rule.PosBank ?? "").Trim();
            if (name.Length == 0 || !seenRules.Add(name))
                continue;

            var cardBanks = (rule.CardBanks ?? [])
                .Select(c => (c ?? "").Trim())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            rules.Add(new PosOnUsRule(name, cardBanks));
        }

        return new PosRateSettings(banks, rules);
    }

    /// <summary>Validation messages for an edited set (empty = valid). Rates are fractions 0..1.</summary>
    public static List<string> Validate(PosRateSettings settings)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var bank in settings.Banks ?? [])
        {
            var name = (bank.PosBank ?? "").Trim();
            if (name.Length == 0)
            {
                errors.Add("Every row needs a POS bank name.");
                continue;
            }
            if (!names.Add(name))
                errors.Add($"'{name}' is listed more than once.");

            void Check(string label, decimal? rate)
            {
                if (rate is < 0 or > 1)
                    errors.Add($"{name}: {label} must be between 0 and 1 (0.009 = 0.9%).");
            }

            Check("foreign card rate", bank.ForeignRate);
            Check("1 installment on us debit", bank.OneOnUsDebit);
            Check("1 installment on us credit", bank.OneOnUsCredit);
            Check("1 installment not on us debit", bank.OneNotOnUsDebit);
            Check("1 installment not on us credit", bank.OneNotOnUsCredit);

            var seenInstallments = new HashSet<int>();
            foreach (var i in bank.Installments ?? [])
            {
                if (i.Installment <= 0)
                    errors.Add($"{name}: installment count must be positive.");
                else if (!seenInstallments.Add(i.Installment))
                    errors.Add($"{name}: {i.Installment} installment is listed more than once.");
                Check($"{i.Installment} installment rate", i.Rate);
            }
        }

        return errors;
    }

    /// <summary>Stock values: the operator's "gökhan banka oranlar" workbook, plus the Bonus family rule.</summary>
    static PosRateSettings Defaults()
    {
        static PosBankRate Bank(string name, decimal foreign, decimal oneOnDebit, decimal oneOnCredit, decimal oneNotDebit,
            params (int Installment, decimal? Rate)[] installments) =>
            new(name, foreign, oneOnDebit, oneOnCredit, oneNotDebit,
                [.. installments.Select(i => new PosInstallmentRate(i.Installment, i.Rate))]);

        return new PosRateSettings(
            [
                Bank("İŞ BANKASI", 0.009m, 0.0095m, 0.0095m, 0.010m, (2, 0.022m), (3, 0.0355m), (4, 0.042m), (5, 0.05m), (9, 0.127m)),
                Bank("YAPI KREDİ", 0.02m, 0.0057m, 0.0096m, 0.0156m, (2, 0.0121m), (3, 0.0349m), (4, 0.0349m), (5, 0.04m), (9, 0.107m)),
                Bank("AKBANK", 0.009m, 0.01m, 0.01m, 0.0164m, (2, 0.0346m), (3, 0.05m), (4, null), (5, null), (9, null)),
                Bank("GARANTİ BBVA", 0.02m, 0.009m, 0.009m, 0.0111m, (2, 0.0285m), (3, 0.045m), (4, null), (5, null), (9, null)),
                Bank("QNB", 0.0159m, 0.00814m, 0.00814m, 0.0169m, (2, 0.034545m), (3, 0.05m), (4, null), (5, null), (9, null)),
                Bank("HALKBANK", 0m, 0.006m, 0.006m, 0.0159m, (2, 0.0245m), (3, 0.0435m), (4, null), (5, null), (9, null)),
                Bank("ZİRAAT BANKASI", 0.0084m, 0.001m, 0.001m, 0.0127m, (2, 0.0259m), (3, 0.0446m), (4, null), (5, null), (9, null)),
            ],
            [new PosOnUsRule("GARANTİ BBVA", ["DENİZBANK", "ING"])]);
    }
}
