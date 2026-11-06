using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Excel round trip for <see cref="PosRateSettings"/>: the exported file has the layout of the
/// operator's "banka oranlar" workbook (POS bank in column A, then the rate columns), plus an
/// "On Us Kuralları" sheet. The reader finds columns by header text, so it reads the original
/// workbook (which also has empty on us / debit / credit / foreign share columns) and its own export.
///
/// <para>Read straight through ClosedXML rather than <see cref="TabularFile"/>: rate cells are numbers
/// with a percent format, and <c>GetString</c> would render them with the machine's culture.</para>
/// </summary>
public static class PosRateWorkbook
{
    public const string RatesSheetName = "Oranlar";
    public const string RulesSheetName = "On Us Kuralları";

    const string OneOnUsDebitHeader = "1 taksit on us debit";
    const string OneOnUsCreditHeader = "1 taksit on us credit";
    const string OneNotOnUsDebitHeader = "1 taksit not on us debit";
    const string OneNotOnUsCreditHeader = "1 taksit not on us credit";
    const string ForeignRateHeader = "Foreign Card Oranı";

    static readonly Regex InstallmentHeader = new(@"^\s*(\d+)\s*TAKS[İI]T", RegexOptions.Compiled);

    public static byte[] Build(PosRateSettings settings)
    {
        using var workbook = new XLWorkbook();

        var installments = settings.Banks.SelectMany(b => b.Installments.Select(i => i.Installment))
            .Distinct().OrderBy(i => i).ToList();

        var sheet = workbook.Worksheets.Add(RatesSheetName);
        XlsxStyles.ApplyBaseFont(sheet);

        string[] fixedHeaders = ["POS Banka", ForeignRateHeader, OneOnUsDebitHeader, OneOnUsCreditHeader, OneNotOnUsDebitHeader, OneNotOnUsCreditHeader];
        for (var c = 0; c < fixedHeaders.Length; c++)
            sheet.Cell(1, c + 1).SetValue(fixedHeaders[c]);
        for (var c = 0; c < installments.Count; c++)
            sheet.Cell(1, fixedHeaders.Length + 1 + c).SetValue($"{installments[c]} taksit oranı");
        var lastCol = fixedHeaders.Length + installments.Count;
        XlsxStyles.StyleHeaderRow(sheet.Range(1, 1, 1, lastCol));

        for (var r = 0; r < settings.Banks.Count; r++)
        {
            var bank = settings.Banks[r];
            var row = r + 2;
            sheet.Cell(row, 1).SetValue(bank.PosBank);
            WriteRate(sheet.Cell(row, 2), bank.ForeignRate);
            WriteRate(sheet.Cell(row, 3), bank.OneOnUsDebit);
            WriteRate(sheet.Cell(row, 4), bank.OneOnUsCredit);
            WriteRate(sheet.Cell(row, 5), bank.OneNotOnUsDebit);
            WriteRate(sheet.Cell(row, 6), bank.OneNotOnUsCredit);
            for (var c = 0; c < installments.Count; c++)
            {
                var rate = bank.Installments.FirstOrDefault(i => i.Installment == installments[c])?.Rate;
                WriteRate(sheet.Cell(row, fixedHeaders.Length + 1 + c), rate);
            }
        }

        if (settings.Banks.Count > 0)
            XlsxStyles.ApplyThinBorders(sheet.Range(1, 1, settings.Banks.Count + 1, lastCol));
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();

        var rules = workbook.Worksheets.Add(RulesSheetName);
        XlsxStyles.ApplyBaseFont(rules);
        rules.Cell(1, 1).SetValue("POS Banka");
        rules.Cell(1, 2).SetValue("On Us Sayılan Kart Bankaları (; ile ayır)");
        XlsxStyles.StyleHeaderRow(rules.Range(1, 1, 1, 2));
        for (var r = 0; r < settings.OnUsRules.Count; r++)
        {
            rules.Cell(r + 2, 1).SetValue(settings.OnUsRules[r].PosBank);
            rules.Cell(r + 2, 2).SetValue(string.Join("; ", settings.OnUsRules[r].CardBanks));
        }
        rules.SheetView.FreezeRows(1);
        rules.Columns().AdjustToContents();

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    /// <summary>
    /// Parses an uploaded rate workbook. The on-us rules are taken from the "On Us Kuralları" sheet when
    /// present, otherwise <paramref name="currentRules"/> is kept (the original workbook has none).
    /// </summary>
    public static PosRateSettings Read(Stream stream, IReadOnlyList<PosOnUsRule> currentRules)
    {
        using var workbook = new XLWorkbook(stream);

        var sheet = workbook.Worksheets.FirstOrDefault(w => w.Name != RulesSheetName)
            ?? throw new InvalidOperationException("The workbook has no rate sheet.");

        var headerRow = sheet.Row(1);
        var lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastCol == 0)
            throw new InvalidOperationException("The rate sheet is empty.");

        int? cForeign = null, cOnDebit = null, cOnCredit = null, cNotDebit = null, cNotCredit = null;
        var installmentCols = new Dictionary<int, int>();
        for (var c = 2; c <= lastCol; c++)
        {
            // Invariant, not Turkish: "debit" must become DEBIT, not DEBİT.
            var header = headerRow.Cell(c).GetString().Trim().ToUpperInvariant();
            if (header.Length == 0)
                continue;

            var m = InstallmentHeader.Match(header);
            if (header.Contains("FOREIGN", StringComparison.Ordinal) && header.Contains("ORAN", StringComparison.Ordinal))
                cForeign = c;
            else if (m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == 1)
            {
                if (header.Contains("NOT", StringComparison.Ordinal))
                {
                    if (header.Contains("CREDIT", StringComparison.Ordinal)) cNotCredit = c;
                    else cNotDebit = c;
                }
                else if (header.Contains("CREDIT", StringComparison.Ordinal)) cOnCredit = c;
                else if (header.Contains("DEBIT", StringComparison.Ordinal)) cOnDebit = c;
            }
            else if (m.Success)
                installmentCols[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = c;
        }

        if (cForeign is null && cOnDebit is null && cOnCredit is null && cNotDebit is null && cNotCredit is null
            && installmentCols.Count == 0)
            throw new InvalidOperationException(
                "No rate columns were found. Expected headers like 'Foreign Card Oranı', '1 taksit on us debit', '2 taksit oranı'.");

        var banks = new List<PosBankRate>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= lastRow; r++)
        {
            var name = sheet.Cell(r, 1).GetString().Trim();
            if (name.Length == 0)
                continue;

            banks.Add(new PosBankRate(
                name,
                ReadRate(sheet, r, cForeign),
                ReadRate(sheet, r, cOnDebit),
                ReadRate(sheet, r, cOnCredit),
                ReadRate(sheet, r, cNotDebit),
                [.. installmentCols.OrderBy(kv => kv.Key).Select(kv => new PosInstallmentRate(kv.Key, ReadRate(sheet, r, kv.Value)))],
                ReadRate(sheet, r, cNotCredit)));
        }

        var rules = workbook.Worksheets.FirstOrDefault(w => w.Name == RulesSheetName) is { } rulesSheet
            ? ReadRules(rulesSheet)
            : [.. currentRules];

        return PosRateStore.Normalize(new PosRateSettings(banks, rules));
    }

    static List<PosOnUsRule> ReadRules(IXLWorksheet sheet)
    {
        var rules = new List<PosOnUsRule>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= lastRow; r++)
        {
            var bank = sheet.Cell(r, 1).GetString().Trim();
            if (bank.Length == 0)
                continue;

            var cardBanks = sheet.Cell(r, 2).GetString()
                .Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            rules.Add(new PosOnUsRule(bank, [.. cardBanks]));
        }
        return rules;
    }

    static decimal? ReadRate(IXLWorksheet sheet, int row, int? col)
    {
        if (col is null)
            return null;

        var cell = sheet.Cell(row, col.Value);
        if (cell.IsEmpty())
            return null;

        if (cell.DataType == XLDataType.Number)
            return (decimal)cell.GetDouble();

        var text = cell.GetString().Trim();
        if (text.Length == 0)
            return null;

        var isPercent = text.Contains('%');
        text = text.Replace("%", "").Replace(',', '.').Trim();
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"Row {row}: '{cell.GetString()}' is not a valid rate.");

        return isPercent ? value / 100m : value;
    }

    static void WriteRate(IXLCell cell, decimal? rate)
    {
        if (rate is null)
            return;

        cell.Value = rate.Value;
        cell.Style.NumberFormat.Format = "0.00##%";
    }
}
