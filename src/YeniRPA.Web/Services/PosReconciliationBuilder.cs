using System.Globalization;
using ClosedXML.Excel;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Backfills Bulut Tahsilat's blank "Sipariş Numarası" rows — every Akbank row, plus a handful on
/// other banks — from Craftgate, then pivots the result.
///
/// <para>The two exports share a code: Bulut Tahsilat calls it "Provizyon No", Craftgate calls it
/// "authCode". Craftgate's own "externalId" column holds the missing order number for that
/// transaction, so the join is "Provizyon No" → "authCode" → "externalId", applied to any row whose
/// "Sipariş Numarası" is blank (not only Akbank rows — a few other banks have the same gap).</para>
///
/// <para>Both files are read through <see cref="TabularFile"/> (ClosedXML) — at ~13-14k rows each
/// they are well inside its normal range; <see cref="OfferExportReader"/>'s streaming reader is
/// reserved for the ~200k-row Mirakl offer export, not this.</para>
/// </summary>
internal static class PosReconciliationBuilder
{
    public const string CraftgateSheetName = "Transactions";
    public const string MiraklSheetName = "Mirakl";

    public const string SourceOriginal = "Orijinal";
    public const string SourceMatched = "Craftgate Eşleşmesi";
    public const string SourceUnmatched = "Eşleşmedi";
    public const string SourceConflict = "Çakışmalı";

    public const string GrandTotalLabel = "Genel Toplam";

    const string ReasonNoProvizyonNo = "Provizyon No boş";
    const string ReasonNotFound = "Craftgate'te eşleşme bulunamadı";
    const string ReasonConflict = "Provizyon No birden fazla farklı sipariş numarasına eşleşiyor";

    public static PosReconciliationBatch Build(
        Stream bulutTahsilatStream, string bulutTahsilatFileName,
        Stream craftgateStream, string craftgateFileName,
        Stream? miraklStream = null, string? miraklFileName = null)
    {
        var craftgateLookup = ReadCraftgateLookup(craftgateStream, craftgateFileName);
        var miraklTotals = miraklStream is null ? null : ReadMiraklTotals(miraklStream, miraklFileName ?? "mirakl.xlsx");
        var (rows, unmatched) = ReadAndMergeBulutTahsilat(bulutTahsilatStream, bulutTahsilatFileName, craftgateLookup, miraklTotals);

        if (rows.Count == 0)
            throw new InvalidOperationException("No records were found in the uploaded Bulut Tahsilat file.");

        var pivot = BuildPivot(rows);
        var summary = BuildSummary(rows, unmatched);

        return new PosReconciliationBatch(DateTime.Now, rows, unmatched, pivot, summary);
    }

    // ---------------------------------------------------------------------
    // Craftgate — authCode -> externalId lookup
    // ---------------------------------------------------------------------

    static Dictionary<string, HashSet<string>> ReadCraftgateLookup(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName, CraftgateSheetName);
        if (table.Count == 0)
            throw new InvalidOperationException("The uploaded Craftgate file is empty.");

        var idx = TabularFile.BuildHeaderIndex(table[0]);
        var cAuthCode = RequireColumn(idx, "Craftgate", "authCode");
        var cExternalId = RequireColumn(idx, "Craftgate", "externalId");

        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        for (var r = 1; r < table.Count; r++)
        {
            var code = NormalizeCode(TabularFile.GetCell(table[r], cAuthCode));
            if (code.Length == 0)
                continue;

            var externalId = TabularFile.GetCell(table[r], cExternalId).Trim();
            if (externalId.Length == 0)
                continue;

            if (!lookup.TryGetValue(code, out var ids))
                lookup[code] = ids = new HashSet<string>(StringComparer.Ordinal);

            ids.Add(externalId);
        }

        return lookup;
    }

    // ---------------------------------------------------------------------
    // Mirakl — order core -> summed Amount (every -A/-B/-C split of an order added together)
    // ---------------------------------------------------------------------

    static Dictionary<string, decimal> ReadMiraklTotals(Stream stream, string fileName)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        Dictionary<string, int>? idx = null;
        int cOrder = 0, cAmount = 0;

        // Streamed: the Mirakl export is one row per order line and can run to a large workbook.
        foreach (var row in OfferExportReader.Read(stream, fileName, MiraklSheetName))
        {
            if (idx is null)
            {
                idx = TabularFile.BuildHeaderIndex(row);
                cOrder = RequireColumn(idx, "Mirakl", "Order number");
                cAmount = RequireColumn(idx, "Mirakl", "Amount");
                continue;
            }

            var key = TabularFile.OrderCore(TabularFile.GetCell(row, cOrder));
            if (key.Length == 0)
                continue;

            totals[key] = totals.GetValueOrDefault(key) + ParseMoney(TabularFile.GetCell(row, cAmount));
        }

        if (idx is null)
            throw new InvalidOperationException("The uploaded Mirakl file is empty.");

        return totals;
    }

    // ---------------------------------------------------------------------
    // Bulut Tahsilat — read, backfill, keep the merged rows and the unmatched ones apart
    // ---------------------------------------------------------------------

    static (List<PosReconciliationRow> Rows, List<PosReconciliationUnmatchedRow> Unmatched) ReadAndMergeBulutTahsilat(
        Stream stream, string fileName, Dictionary<string, HashSet<string>> craftgateLookup,
        Dictionary<string, decimal>? miraklTotals)
    {
        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("The uploaded Bulut Tahsilat file is empty.");

        var idx = TabularFile.BuildHeaderIndex(table[0]);
        var cIslemKodu = RequireColumn(idx, "Bulut Tahsilat", "İşlem Kodu");
        var cPosBanka = RequireColumn(idx, "Bulut Tahsilat", "POS Banka");
        var cKartBanka = RequireColumn(idx, "Bulut Tahsilat", "Kart Banka");
        var cIslemTutari = RequireColumn(idx, "Bulut Tahsilat", "İşlem Tutarı");
        var cKomisyonTutari = RequireColumn(idx, "Bulut Tahsilat", "Toplam Komisyon Tutarı");
        var cProvizyonNo = RequireColumn(idx, "Bulut Tahsilat", "Provizyon No");
        var cTaksit = RequireColumn(idx, "Bulut Tahsilat", "Taksit");
        var cSiparisNo = RequireColumn(idx, "Bulut Tahsilat", "Sipariş Numarası");
        // Optional: older exports without it still load, their card type is just blank.
        int? cKartTipi = idx.TryGetValue("POS Kart Tipi", out var kartTipiIndex) ? kartTipiIndex : null;

        var rows = new List<PosReconciliationRow>(table.Count - 1);
        var unmatched = new List<PosReconciliationUnmatchedRow>();

        // An order paid across several Bulut rows has its Mirakl total subtracted once, from the first sale row.
        var miraklApplied = new HashSet<string>(StringComparer.Ordinal);

        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];

            var islemKodu = TabularFile.GetCell(row, cIslemKodu).Trim();
            var posBanka = TabularFile.GetCell(row, cPosBanka).Trim();
            var kartBanka = TabularFile.GetCell(row, cKartBanka).Trim();
            var islemTutari = ParseMoney(TabularFile.GetCell(row, cIslemTutari));
            var komisyonTutari = ParseMoney(TabularFile.GetCell(row, cKomisyonTutari));
            var provizyonNo = TabularFile.GetCell(row, cProvizyonNo).Trim();
            var taksit = (int)Math.Round(TabularFile.ParseNumber(TabularFile.GetCell(row, cTaksit)), MidpointRounding.AwayFromZero);
            var siparisNo = NormalizeOrderNumber(TabularFile.GetCell(row, cSiparisNo));
            var kartTipi = TabularFile.GetCell(row, cKartTipi).Trim();

            // A row this whole workbook is otherwise empty on (a trailing blank line) is skipped
            // rather than counted as a record with no bank and no amount.
            if (islemKodu.Length == 0 && posBanka.Length == 0 && islemTutari == 0 && komisyonTutari == 0)
                continue;

            var kaynak = SourceOriginal;

            if (siparisNo.Length == 0)
            {
                var code = NormalizeCode(provizyonNo);

                if (code.Length == 0)
                {
                    kaynak = SourceUnmatched;
                    unmatched.Add(new PosReconciliationUnmatchedRow(provizyonNo, posBanka, kartBanka, islemTutari, taksit, ReasonNoProvizyonNo));
                }
                else if (craftgateLookup.TryGetValue(code, out var externalIds))
                {
                    if (externalIds.Count == 1)
                    {
                        siparisNo = NormalizeOrderNumber(externalIds.Single());
                        kaynak = SourceMatched;
                    }
                    else
                    {
                        kaynak = SourceConflict;
                        unmatched.Add(new PosReconciliationUnmatchedRow(provizyonNo, posBanka, kartBanka, islemTutari, taksit, ReasonConflict));
                    }
                }
                else
                {
                    kaynak = SourceUnmatched;
                    unmatched.Add(new PosReconciliationUnmatchedRow(provizyonNo, posBanka, kartBanka, islemTutari, taksit, ReasonNotFound));
                }
            }

            decimal? marketplaceGmv = null;
            decimal? retailGmv = null;
            if (miraklTotals is not null && siparisNo.Length > 0)
            {
                var key = TabularFile.OrderCore(siparisNo);
                // Only a positive (sale) row can carry it — a refund row of the same order must not
                // consume it before the sale row is reached.
                marketplaceGmv = islemTutari > 0 && miraklTotals.TryGetValue(key, out var mirakl) && miraklApplied.Add(key)
                    ? Math.Round(mirakl, 2, MidpointRounding.AwayFromZero)
                    : 0m;
                retailGmv = islemTutari - marketplaceGmv;
            }

            rows.Add(new PosReconciliationRow(
                islemKodu, posBanka, kartBanka, islemTutari, komisyonTutari, provizyonNo, taksit, siparisNo, kaynak,
                marketplaceGmv, retailGmv, kartTipi));
        }

        return (rows, unmatched);
    }

    // ---------------------------------------------------------------------
    // Pivot — rows = POS Banka, columns = Taksit (+ a closing Genel Toplam column and row)
    // ---------------------------------------------------------------------

    static PosReconciliationPivot BuildPivot(IReadOnlyList<PosReconciliationRow> rows)
    {
        var installments = rows.Select(r => r.Taksit).Distinct().OrderBy(t => t).ToList();
        var labels = installments.Select(t => t.ToString(CultureInfo.InvariantCulture)).ToList();
        labels.Add(GrandTotalLabel);

        var banks = rows.Select(r => r.PosBanka)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(b => b, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var pivotRows = new List<PosReconciliationPivotRow>(banks.Count + 1);
        var columnIslemTotals = new decimal[installments.Count];
        var columnKomisyonTotals = new decimal[installments.Count];
        decimal grandIslemTotal = 0;
        decimal grandKomisyonTotal = 0;

        foreach (var bank in banks)
        {
            var bankRows = rows.Where(r => string.Equals(r.PosBanka, bank, StringComparison.CurrentCultureIgnoreCase)).ToList();
            var cells = new List<PosReconciliationPivotCell>(labels.Count);
            decimal rowIslemTotal = 0;
            decimal rowKomisyonTotal = 0;

            for (var c = 0; c < installments.Count; c++)
            {
                var cellRows = bankRows.Where(r => r.Taksit == installments[c]);
                var islemSum = Math.Round(cellRows.Sum(r => r.IslemTutari), 2, MidpointRounding.AwayFromZero);
                var komisyonSum = Math.Round(cellRows.Sum(r => r.ToplamKomisyonTutari), 2, MidpointRounding.AwayFromZero);

                cells.Add(new PosReconciliationPivotCell(islemSum, komisyonSum));
                rowIslemTotal += islemSum;
                rowKomisyonTotal += komisyonSum;
                columnIslemTotals[c] += islemSum;
                columnKomisyonTotals[c] += komisyonSum;
            }

            cells.Add(new PosReconciliationPivotCell(rowIslemTotal, rowKomisyonTotal));
            grandIslemTotal += rowIslemTotal;
            grandKomisyonTotal += rowKomisyonTotal;

            pivotRows.Add(new PosReconciliationPivotRow(bank, cells));
        }

        var totalCells = new List<PosReconciliationPivotCell>(labels.Count);
        for (var c = 0; c < installments.Count; c++)
            totalCells.Add(new PosReconciliationPivotCell(columnIslemTotals[c], columnKomisyonTotals[c]));
        totalCells.Add(new PosReconciliationPivotCell(grandIslemTotal, grandKomisyonTotal));

        pivotRows.Add(new PosReconciliationPivotRow(GrandTotalLabel, totalCells));

        return new PosReconciliationPivot(labels, pivotRows);
    }

    static PosReconciliationSummary BuildSummary(
        IReadOnlyList<PosReconciliationRow> rows, IReadOnlyList<PosReconciliationUnmatchedRow> unmatched)
    {
        var original = rows.Count(r => r.SiparisKaynagi == SourceOriginal);
        var matched = rows.Count(r => r.SiparisKaynagi == SourceMatched);
        var unmatchedCount = rows.Count(r => r.SiparisKaynagi == SourceUnmatched);
        var conflicted = rows.Count(r => r.SiparisKaynagi == SourceConflict);

        return new PosReconciliationSummary(
            rows.Count, original, matched, unmatchedCount, conflicted,
            Math.Round(rows.Sum(r => r.IslemTutari), 2, MidpointRounding.AwayFromZero),
            Math.Round(rows.Sum(r => r.ToplamKomisyonTutari), 2, MidpointRounding.AwayFromZero),
            rows.Any(r => r.MarketplaceGmv.HasValue)
                ? Math.Round(rows.Sum(r => r.MarketplaceGmv ?? 0), 2, MidpointRounding.AwayFromZero) : null,
            rows.Any(r => r.RetailGmv.HasValue)
                ? Math.Round(rows.Sum(r => r.RetailGmv ?? 0), 2, MidpointRounding.AwayFromZero) : null);
    }

    /// <summary>
    /// Bulut Tahsilat's İşlem Tutarı / Toplam Komisyon Tutarı as ClosedXML rendered the cell, not as
    /// <see cref="TabularFile.ParseNumber"/> — which is culture-invariant — would read it.
    ///
    /// <para><see cref="IXLCell.GetString"/> formats a numeric cell's decimal point using
    /// <see cref="CultureInfo.CurrentCulture"/>, not the workbook's own locale: on the operator's
    /// Turkish machine a cell holding <c>81.27</c> comes back as the text <c>"81,27"</c>.
    /// <see cref="TabularFile.ParseNumber"/> parses with <see cref="CultureInfo.InvariantCulture"/>,
    /// whose decimal point is <c>.</c> and whose thousands separator is <c>,</c> — so it reads that
    /// comma as a misplaced grouping separator, strips it, and turns 81,27 into 8127 (confirmed against
    /// the real August export: every bank's summed total came back a suspiciously round number before
    /// this fix). Neither of these two columns' own number format ever adds real thousands grouping
    /// either (a genuine five-digit amount like "40599" renders with no separator at all), so a comma
    /// actually present here can only ever be the decimal point — safe to normalize and reparse as
    /// invariant.</para>
    /// </summary>
    static decimal ParseMoney(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return 0;

        text = text.Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>
    /// An order number in the form Mirakl uses. Some Bulut Tahsilat / Craftgate rows carry a 3-D Secure
    /// reference instead — <c>TDS_0000000000335216382</c> — which is the order <c>335216382</c> behind a
    /// <c>TDS_</c> prefix and zero padding; left as is, it never matches a Mirakl order. Any other
    /// value (including one with a real leading zero) is only trimmed.
    /// </summary>
    internal static string NormalizeOrderNumber(string raw)
    {
        var text = (raw ?? "").Trim();
        if (!text.StartsWith("TDS_", StringComparison.OrdinalIgnoreCase))
            return text;

        var digits = text[4..].Trim().TrimStart('0');
        return digits.Length > 0 && digits.All(char.IsDigit) ? digits : text;
    }

    /// <summary>
    /// A Provizyon No / authCode as it should be compared, not as Excel wrote it. Only touched when
    /// the text visibly carries scientific or decimal formatting (an <c>E</c> or a <c>.</c>) — a plain
    /// digit string is left alone, because a real leading zero can only survive in a text-typed cell.
    ///
    /// <para>Deliberately not shared with <see cref="CargoSellerReportBuilder.NormalizeTrackingCode"/>
    /// or any other module's normalizer — each keeps its own copy so a fix made here cannot silently
    /// move another report's figures.</para>
    /// </summary>
    static string NormalizeCode(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return "";

        var looksNumericFormatted = text.Contains('E', StringComparison.OrdinalIgnoreCase) || text.Contains('.');
        if (looksNumericFormatted &&
            decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number.ToString("F0", CultureInfo.InvariantCulture);
        }

        return text;
    }

    static int RequireColumn(Dictionary<string, int> idx, string fileDescription, string name)
    {
        if (idx.TryGetValue(name, out var i))
            return i;

        throw new InvalidOperationException(
            $"Required column '{name}' was not found in the uploaded {fileDescription} file.");
    }

    // ---------------------------------------------------------------------
    // Workbook — Pivot, Detay (every merged row) and Eşleşmeyenler, in that order
    // ---------------------------------------------------------------------

    public static byte[] BuildWorkbook(PosReconciliationBatch batch, PosRateSettings rates)
    {
        using var workbook = new XLWorkbook();

        BuildPivotSheet(workbook, batch.Pivot);
        var classifier = new PosCardClassifier(rates);
        BuildDetailSheet(workbook, batch.Rows, classifier, new PosRateResolver(rates, classifier));
        BuildUnmatchedSheet(workbook, batch.Unmatched);
        BuildRateSheet(workbook, batch.Rows, rates);

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    static void BuildPivotSheet(XLWorkbook workbook, PosReconciliationPivot pivot)
    {
        var sheet = workbook.Worksheets.Add("Pivot");
        XlsxStyles.ApplyBaseFont(sheet);

        sheet.Cell(1, 1).SetValue("POS Banka");
        for (var c = 0; c < pivot.Labels.Count; c++)
        {
            sheet.Cell(1, 2 + c * 2).SetValue(pivot.Labels[c] + " - İşlem Tutarı");
            sheet.Cell(1, 3 + c * 2).SetValue(pivot.Labels[c] + " - Toplam Komisyon Tutarı");
        }
        var lastCol = 1 + pivot.Labels.Count * 2;
        XlsxStyles.StyleHeaderRow(sheet.Range(1, 1, 1, lastCol));

        for (var r = 0; r < pivot.Rows.Count; r++)
        {
            var pivotRow = pivot.Rows[r];
            var excelRow = r + 2;

            sheet.Cell(excelRow, 1).SetValue(pivotRow.PosBanka);
            if (pivotRow.PosBanka == GrandTotalLabel)
                sheet.Cell(excelRow, 1).Style.Font.Bold = true;

            for (var c = 0; c < pivotRow.Cells.Count; c++)
            {
                WriteMoneyCell(sheet.Cell(excelRow, 2 + c * 2), pivotRow.Cells[c].IslemTutari);
                WriteMoneyCell(sheet.Cell(excelRow, 3 + c * 2), pivotRow.Cells[c].ToplamKomisyonTutari);
            }
        }

        var lastRow = pivot.Rows.Count + 1;
        if (pivot.Rows.Count > 0)
        {
            XlsxStyles.ApplyThinBorders(sheet.Range(1, 1, lastRow, lastCol));
            XlsxStyles.ApplyZebra(sheet, 2, lastRow, 1, lastCol);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.SheetView.FreezeColumns(1);
        sheet.Columns().AdjustToContents();
    }

    static void BuildDetailSheet(XLWorkbook workbook, IReadOnlyList<PosReconciliationRow> rows,
        PosCardClassifier classifier, PosRateResolver resolver)
    {
        var sheet = workbook.Worksheets.Add("Detay");
        XlsxStyles.ApplyBaseFont(sheet);

        string[] headers =
        [
            "İşlem Kodu", "POS Banka", "Kart Banka", "Kart Tipi", "On Us / Not On Us", "İşlem Tutarı",
            "Toplam Komisyon Tutarı", "Komisyon Oranı (%)",
            "Provizyon No", "Taksit", "Sipariş Numarası", "Sipariş Numarası Kaynağı",
            "Marketplace GMV", "Retail GMV", "MP GMV - Retail GMV Fark"
        ];
        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).SetValue(headers[c]);
        XlsxStyles.StyleHeaderRow(sheet.Range(1, 1, 1, headers.Length));

        // Written as text so Provizyon No and Sipariş Numarası never round-trip through Excel's own
        // number formatting into scientific notation the next time the file is opened.
        sheet.Column(9).Style.NumberFormat.Format = "@";
        sheet.Column(11).Style.NumberFormat.Format = "@";

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var excelRow = r + 2;

            sheet.Cell(excelRow, 1).SetValue(row.IslemKodu);
            sheet.Cell(excelRow, 2).SetValue(row.PosBanka);
            sheet.Cell(excelRow, 3).SetValue(row.KartBanka);
            sheet.Cell(excelRow, 4).SetValue(row.CardType);
            sheet.Cell(excelRow, 5).SetValue(classifier.IsOnUs(row) ? "On Us" : "Not On Us");
            WriteMoneyCell(sheet.Cell(excelRow, 6), row.IslemTutari);
            WriteMoneyCell(sheet.Cell(excelRow, 7), row.ToplamKomisyonTutari);
            // From the bank-rate table, not from the export: the rate this card/installment should cost.
            if (resolver.Resolve(row) is { } oran)
            {
                sheet.Cell(excelRow, 8).Value = oran;
                sheet.Cell(excelRow, 8).Style.NumberFormat.Format = "0.00##%";
            }
            sheet.Cell(excelRow, 9).SetValue(row.ProvizyonNo);
            sheet.Cell(excelRow, 10).Value = row.Taksit;
            sheet.Cell(excelRow, 11).SetValue(row.SiparisNumarasi);
            sheet.Cell(excelRow, 12).SetValue(row.SiparisKaynagi);
            if (row.MarketplaceGmv is { } marketplace)
                WriteMoneyCell(sheet.Cell(excelRow, 13), marketplace);
            if (row.RetailGmv is { } retail)
                WriteMoneyCell(sheet.Cell(excelRow, 14), retail);
            if (row.MarketplaceGmv is { } mp && row.RetailGmv is { } rt)
                WriteMoneyCell(sheet.Cell(excelRow, 15), mp - rt);
        }

        var lastRow = rows.Count + 1;
        if (rows.Count > 0)
        {
            XlsxStyles.ApplyThinBorders(sheet.Range(1, 1, lastRow, headers.Length));
            XlsxStyles.ApplyZebra(sheet, 2, lastRow, 1, headers.Length);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
    }

    static void BuildUnmatchedSheet(XLWorkbook workbook, IReadOnlyList<PosReconciliationUnmatchedRow> unmatched)
    {
        var sheet = workbook.Worksheets.Add("Eşleşmeyenler");
        XlsxStyles.ApplyBaseFont(sheet);

        string[] headers = ["Provizyon No", "POS Banka", "Kart Banka", "İşlem Tutarı", "Taksit", "Neden"];
        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).SetValue(headers[c]);
        XlsxStyles.StyleHeaderRow(sheet.Range(1, 1, 1, headers.Length));
        sheet.Column(1).Style.NumberFormat.Format = "@";

        for (var r = 0; r < unmatched.Count; r++)
        {
            var row = unmatched[r];
            var excelRow = r + 2;

            sheet.Cell(excelRow, 1).SetValue(row.ProvizyonNo);
            sheet.Cell(excelRow, 2).SetValue(row.PosBanka);
            sheet.Cell(excelRow, 3).SetValue(row.KartBanka);
            WriteMoneyCell(sheet.Cell(excelRow, 4), row.IslemTutari);
            sheet.Cell(excelRow, 5).Value = row.Taksit;
            sheet.Cell(excelRow, 6).SetValue(row.Reason);
        }

        var lastRow = unmatched.Count + 1;
        if (unmatched.Count > 0)
        {
            XlsxStyles.ApplyThinBorders(sheet.Range(1, 1, lastRow, headers.Length));
            XlsxStyles.ApplyZebra(sheet, 2, lastRow, 1, headers.Length);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
    }

    // ---------------------------------------------------------------------
    // Banka Oranları — per POS bank card mix (by transaction count) beside the editable rate table
    // ---------------------------------------------------------------------

    /// <summary>One POS bank's transaction counts by card mix. Only sale rows (positive İşlem Tutarı) are
    /// counted, so refunds and penalties do not distort the shares.</summary>
    public sealed record BankCardMix(string PosBanka, int Total, int OnUs, int Debit, int Credit, int Foreign);

    public static List<BankCardMix> ComputeCardMix(
        IEnumerable<PosReconciliationRow> rows, PosCardClassifier classifier, IEnumerable<string> bankOrder)
    {
        var counts = new Dictionary<string, int[]>(StringComparer.Ordinal); // [total, onUs, debit, credit, foreign]
        var display = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.IslemTutari <= 0)
                continue;

            var key = PosCardClassifier.Norm(row.PosBanka);
            if (key.Length == 0)
                continue;

            display.TryAdd(key, row.PosBanka);
            if (!counts.TryGetValue(key, out var c))
                counts[key] = c = new int[5];

            c[0]++;
            if (classifier.IsOnUs(row)) c[1]++;
            switch (classifier.Kind(row))
            {
                case PosCardKind.Debit: c[2]++; break;
                case PosCardKind.Credit: c[3]++; break;
                case PosCardKind.Foreign: c[4]++; break;
            }
        }

        var result = new List<BankCardMix>();
        var added = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name)
        {
            var key = PosCardClassifier.Norm(name);
            if (key.Length == 0 || !added.Add(key))
                return;
            var c = counts.GetValueOrDefault(key) ?? new int[5];
            result.Add(new BankCardMix(display.GetValueOrDefault(key, name), c[0], c[1], c[2], c[3], c[4]));
        }

        foreach (var bank in bankOrder)
            Add(bank);
        foreach (var bank in display.Values.OrderBy(b => b, StringComparer.CurrentCultureIgnoreCase))
            Add(bank);

        return result;
    }

    static void BuildRateSheet(XLWorkbook workbook, IReadOnlyList<PosReconciliationRow> rows, PosRateSettings rates)
    {
        var sheet = workbook.Worksheets.Add("Banka Oranları");
        XlsxStyles.ApplyBaseFont(sheet);

        var classifier = new PosCardClassifier(rates);
        var mix = ComputeCardMix(rows, classifier, rates.Banks.Select(b => b.PosBank));
        var installments = rates.Banks.SelectMany(b => b.Installments.Select(i => i.Installment))
            .Distinct().OrderBy(i => i).ToList();

        string[] fixedHeaders =
        [
            "", "on us", "not on us", "Debit Kart", "Credit Card", "Foreign Card", "Foreign Card Oranı",
            "1 taksit on us debit", "1 taksit on us credit", "1 taksit not on us debit", "1 taksit not on us credit"
        ];
        for (var c = 0; c < fixedHeaders.Length; c++)
            sheet.Cell(1, c + 1).SetValue(fixedHeaders[c]);
        for (var c = 0; c < installments.Count; c++)
            sheet.Cell(1, fixedHeaders.Length + 1 + c).SetValue($"{installments[c]} taksit oran");
        var countCol = fixedHeaders.Length + installments.Count + 1;
        sheet.Cell(1, countCol).SetValue("İşlem Adedi");
        XlsxStyles.StyleHeaderRow(sheet.Range(1, 1, 1, countCol));

        void WriteShare(int excelRow, int column, int part, int total)
        {
            if (total == 0)
                return;
            var cell = sheet.Cell(excelRow, column);
            cell.Value = (decimal)part / total;
            cell.Style.NumberFormat.Format = "0.00%";
        }

        void WriteRate(int excelRow, int column, decimal? rate)
        {
            if (rate is null)
                return;
            var cell = sheet.Cell(excelRow, column);
            cell.Value = rate.Value;
            cell.Style.NumberFormat.Format = "0.00##%";
        }

        for (var r = 0; r < mix.Count; r++)
        {
            var m = mix[r];
            var excelRow = r + 2;

            sheet.Cell(excelRow, 1).SetValue(m.PosBanka);
            WriteShare(excelRow, 2, m.OnUs, m.Total);
            WriteShare(excelRow, 3, m.Total - m.OnUs, m.Total);
            WriteShare(excelRow, 4, m.Debit, m.Total);
            WriteShare(excelRow, 5, m.Credit, m.Total);
            WriteShare(excelRow, 6, m.Foreign, m.Total);
            sheet.Cell(excelRow, countCol).Value = m.Total;

            var bankRate = rates.Banks.FirstOrDefault(b =>
                PosCardClassifier.Norm(b.PosBank) == PosCardClassifier.Norm(m.PosBanka));
            if (bankRate is null)
                continue;

            WriteRate(excelRow, 7, bankRate.ForeignRate);
            WriteRate(excelRow, 8, bankRate.OneOnUsDebit);
            WriteRate(excelRow, 9, bankRate.OneOnUsCredit);
            WriteRate(excelRow, 10, bankRate.OneNotOnUsDebit);
            WriteRate(excelRow, 11, bankRate.OneNotOnUsCredit);
            for (var c = 0; c < installments.Count; c++)
                WriteRate(excelRow, fixedHeaders.Length + 1 + c,
                    bankRate.Installments.FirstOrDefault(i => i.Installment == installments[c])?.Rate);
        }

        var totalRow = mix.Count + 2;
        var total = new BankCardMix(GrandTotalLabel, mix.Sum(m => m.Total), mix.Sum(m => m.OnUs),
            mix.Sum(m => m.Debit), mix.Sum(m => m.Credit), mix.Sum(m => m.Foreign));
        sheet.Cell(totalRow, 1).SetValue(total.PosBanka);
        sheet.Cell(totalRow, 1).Style.Font.Bold = true;
        WriteShare(totalRow, 2, total.OnUs, total.Total);
        WriteShare(totalRow, 3, total.Total - total.OnUs, total.Total);
        WriteShare(totalRow, 4, total.Debit, total.Total);
        WriteShare(totalRow, 5, total.Credit, total.Total);
        WriteShare(totalRow, 6, total.Foreign, total.Total);
        sheet.Cell(totalRow, countCol).Value = total.Total;

        XlsxStyles.ApplyThinBorders(sheet.Range(1, 1, totalRow, countCol));
        XlsxStyles.ApplyZebra(sheet, 2, totalRow, 1, countCol);
        sheet.SheetView.FreezeRows(1);
        sheet.SheetView.FreezeColumns(1);
        sheet.Columns().AdjustToContents();
    }

    static void WriteMoneyCell(IXLCell cell, decimal value)
    {
        cell.Value = value;
        cell.Style.NumberFormat.Format = "#,##0.00";
    }
}
