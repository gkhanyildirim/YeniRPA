using System.Globalization;
using System.IO.Compression;
using ClosedXML.Excel;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Matches a cargo-invoice export, grouped by every seller it holds, against the Marketplace
/// return/exchange export and the MM Pazaryeri cargo data export — the same two source files
/// <see cref="ReturnListBuilder"/> reads, here keyed by tracking code instead of order number, to
/// recover each cargo shipment's order number rather than build a return candidate list.
///
/// <para>Run for every seller in one pass rather than one seller at a time: the operator runs this
/// once a month over the whole file, so <see cref="GenerateAll"/> groups the cargo rows itself and
/// <see cref="BuildZip"/> hands back one workbook per seller plus one overview workbook, zipped
/// together.</para>
///
/// <para>The cargo-invoice file is read through <see cref="TabularFile"/> — the same ClosedXML-based
/// reader Marketplace and MM go through — rather than <see cref="OfferExportReader"/>: the real export
/// (a courier's "Toplu Fatura Detay Raporu") carries genuinely numeric-formatted cells — dates, and a
/// batch invoice code long enough to render in scientific notation — and <see cref="TabularFile"/>'s
/// <c>GetString()</c> reads each cell's own displayed text, the same string Excel shows.
/// <see cref="OfferExportReader"/> deliberately skips that formatting (right for the Mirakl offer
/// export it was written for, wrong here: a date would come back as a bare day-count serial), so it
/// is not used for this file. Its real header row still sits a few free-text/title rows down rather
/// than at row 1, so this module scans the rows <see cref="TabularFile"/> yields for the one that
/// actually carries "Gönderi Kodu" instead of trusting row 1.</para>
/// </summary>
internal static class CargoSellerReportBuilder
{
    public const string TrackingCodeHeader = "Gönderi Kodu";

    /// <summary>Where a cargo row's seller cell is blank — kept and labeled rather than dropped, so a
    /// row with no seller does not just silently disappear from every seller's report.</summary>
    const string UnknownSellerLabel = "(Satıcı Belirtilmemiş)";

    // "Alıcı Müşteri" is checked before "Gönderici Müşteri" on real cargo-invoice exports: the courier
    // bills the marketplace's own account as the sender on every row, so "Gönderici Müşteri" is the
    // marketplace, not the seller. "Alıcı Müşteri" is the party the shipment is actually for.
    static readonly string[] SellerColumnCandidates =
        ["Satıcı", "Satıcı Adı", "Satici", "Satici Adi", "Alıcı Müşteri", "Gönderici Müşteri", "Müşteri Adı"];

    // "İrsaliye Matrahı" (the waybill's pre-VAT base amount) is the real column name on the cargo
    // invoice export — it is what "cargo fee" means for the Özet sheet's totals. The other names are
    // kept as fallbacks for an export laid out differently.
    static readonly string[] CargoFeeCandidates = ["İrsaliye Matrahı", "Kargo Bedeli", "Kargo Tutarı", "Kargo Ücreti"];
    static readonly string[] VatCandidates = ["KDV", "KDV Tutarı"];
    static readonly string[] TotalAmountCandidates = ["Toplam Tutar", "Genel Toplam", "Fatura Tutarı"];
    static readonly string[] DesiCandidates = ["Desi"];
    static readonly string[] DeliveryDateCandidates = ["Teslim Tarihi", "Teslimat Tarihi"];
    static readonly string[] StatusCandidates = ["Kargo Durumu", "Gönderi Durumu", "Durum"];

    public const string MatchSourceMarketplace = "Marketplace";
    public const string MatchSourceMm = "MM PazaryeriKargoDatasi";
    public const string MatchSourceNone = "Eşleşme Yok";
    public const string MatchSourceConflict = "Çakışmalı Eşleşme";

    public const string MatchStatusMatched = "Eşleşti";
    public const string MatchStatusUnmatched = "Eşleşmedi";
    public const string MatchStatusConflicted = "Çakışmalı";

    /// <summary>How many leading rows are scanned for the header before giving up. The real exports
    /// carry at most a handful of title/blank rows above the real header.</summary>
    const int HeaderSearchRowLimit = 30;

    // ---------------------------------------------------------------------
    // Analyze
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads the cargo-invoice file, locates its header row and its tracking-code column, and either
    /// resolves the seller column (from <see cref="SellerColumnCandidates"/> or
    /// <paramref name="sellerColumnOverride"/>) or reports every header so the operator can pick one.
    /// </summary>
    public static CargoSellerReportAnalyzeResult Analyze(Stream stream, string fileName, string? sellerColumnOverride)
    {
        var dataRows = ReadKargoTable(stream, fileName, out var headerRow);
        var idx = TabularFile.BuildHeaderIndex(headerRow);

        var cTracking = RequireColumn(idx, "cargo invoice", TrackingCodeHeader);

        string? sellerColumn = null;
        var sellerColumnResolved = false;
        int? cSeller = null;

        var overrideName = (sellerColumnOverride ?? "").Trim();
        if (overrideName.Length > 0)
        {
            if (!idx.TryGetValue(overrideName, out var overrideIndex))
                throw new InvalidOperationException($"Column '{overrideName}' was not found in the uploaded file.");

            cSeller = overrideIndex;
            sellerColumn = headerRow[overrideIndex];
            sellerColumnResolved = true;
        }
        else
        {
            foreach (var candidate in SellerColumnCandidates)
            {
                if (!idx.TryGetValue(candidate, out var candidateIndex))
                    continue;

                cSeller = candidateIndex;
                sellerColumn = headerRow[candidateIndex];
                sellerColumnResolved = true;
                break;
            }
        }

        var sellers = new List<string>();
        if (cSeller.HasValue)
        {
            // Folded for de-duplication (so "Zitek" and "zitek " count once) but the list itself keeps
            // the first raw spelling seen — the operator picks from what the file actually says.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in dataRows)
            {
                var value = TabularFile.GetCell(row, cSeller.Value).Trim();
                if (value.Length == 0)
                    continue;

                if (seen.Add(SellerGroupMap.FoldName(value)))
                    sellers.Add(value);
            }
            sellers.Sort(StringComparer.CurrentCultureIgnoreCase);
        }

        return new CargoSellerReportAnalyzeResult(headerRow, headerRow[cTracking], sellerColumn, sellerColumnResolved, sellers);
    }

    // ---------------------------------------------------------------------
    // Generate
    // ---------------------------------------------------------------------

    /// <summary>
    /// Groups the cargo-invoice file by every seller in <paramref name="sellerColumn"/> and resolves
    /// each row's order number: Marketplace's "Kargo Takip Kodu" first, the MM cargo data's "YK Takip
    /// Kodu" only when Marketplace has no record of the code at all (found-but-conflicted still counts
    /// as found, and does not fall through to MM). One result per seller, sorted by name.
    /// </summary>
    public static IReadOnlyList<CargoSellerReportResult> GenerateAll(
        Stream kargoStream, string kargoFileName, string sellerColumn,
        Stream marketplaceStream, string marketplaceFileName,
        Stream mmStream, string mmFileName)
    {
        var dataRows = ReadKargoTable(kargoStream, kargoFileName, out var headerRow);
        if (dataRows.Count == 0)
            throw new InvalidOperationException("No cargo records were found in the uploaded file.");

        var idx = TabularFile.BuildHeaderIndex(headerRow);

        var cTracking = RequireColumn(idx, "cargo invoice", TrackingCodeHeader);

        var sellerColumnName = sellerColumn.Trim();
        if (!idx.TryGetValue(sellerColumnName, out var cSeller))
            throw new InvalidOperationException($"Column '{sellerColumnName}' was not found in the uploaded cargo invoice file.");

        var cCargoFee = OptionalColumn(idx, CargoFeeCandidates);
        var cVat = OptionalColumn(idx, VatCandidates);
        var cTotalAmount = OptionalColumn(idx, TotalAmountCandidates);
        var cDesi = OptionalColumn(idx, DesiCandidates);
        var cDeliveryDate = OptionalColumn(idx, DeliveryDateCandidates);
        var cStatus = OptionalColumn(idx, StatusCandidates);

        var optionalColumns = new Dictionary<string, int>();
        if (cCargoFee.HasValue) optionalColumns["cargoFee"] = cCargoFee.Value;
        if (cVat.HasValue) optionalColumns["vat"] = cVat.Value;
        if (cTotalAmount.HasValue) optionalColumns["totalAmount"] = cTotalAmount.Value;
        if (cDesi.HasValue) optionalColumns["desi"] = cDesi.Value;
        if (cDeliveryDate.HasValue) optionalColumns["deliveryDate"] = cDeliveryDate.Value;
        if (cStatus.HasValue) optionalColumns["status"] = cStatus.Value;

        // Grouped by the folded seller value so "Zitek" and " zitek " land in the same report; the
        // first raw spelling seen is what that report and its file name display.
        var groups = new Dictionary<string, (string DisplayName, List<List<string>> Rows)>(StringComparer.Ordinal);
        var groupOrder = new List<string>();

        foreach (var row in dataRows)
        {
            var raw = TabularFile.GetCell(row, cSeller).Trim();
            var display = raw.Length > 0 ? raw : UnknownSellerLabel;
            var key = SellerGroupMap.FoldName(display);

            if (!groups.TryGetValue(key, out var group))
            {
                group = (display, []);
                groups[key] = group;
                groupOrder.Add(key);
            }
            group.Rows.Add(row);
        }

        var marketplaceLookup = ReadMarketplaceLookup(marketplaceStream, marketplaceFileName);
        var mmLookup = ReadMmLookup(mmStream, mmFileName);

        return groupOrder
            .Select(key => groups[key])
            .Select(g => BuildSellerResult(
                g.DisplayName, g.Rows, headerRow, cTracking, marketplaceLookup, mmLookup,
                cCargoFee, cVat, cTotalAmount, cStatus, optionalColumns))
            .OrderBy(r => r.SellerName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    static CargoSellerReportResult BuildSellerResult(
        string sellerName, List<List<string>> filtered, List<string> headerRow, int cTracking,
        Dictionary<string, HashSet<string>> marketplaceLookup, Dictionary<string, HashSet<string>> mmLookup,
        int? cCargoFee, int? cVat, int? cTotalAmount, int? cStatus,
        Dictionary<string, int> optionalColumns)
    {
        var rows = new List<CargoSellerReportRow>(filtered.Count);
        var unmatched = new List<CargoSellerReportUnmatchedRow>();
        var conflictsByCode = new Dictionary<string, CargoSellerReportConflictRow>(StringComparer.OrdinalIgnoreCase);

        var matchedMarketplace = 0;
        var matchedMm = 0;

        foreach (var row in filtered)
        {
            var rawCode = TabularFile.GetCell(row, cTracking);
            var code = NormalizeTrackingCode(rawCode);

            var orderNumber = "";
            var matchSource = MatchSourceNone;
            var matchStatus = MatchStatusUnmatched;

            if (code.Length > 0 && marketplaceLookup.TryGetValue(code, out var marketplaceOrders))
            {
                if (marketplaceOrders.Count == 1)
                {
                    orderNumber = marketplaceOrders.Single();
                    matchSource = MatchSourceMarketplace;
                    matchStatus = MatchStatusMatched;
                    matchedMarketplace++;
                }
                else
                {
                    matchSource = MatchSourceConflict;
                    matchStatus = MatchStatusConflicted;
                    RecordConflict(conflictsByCode, code, MatchSourceMarketplace, marketplaceOrders);
                }
            }
            else if (code.Length > 0 && mmLookup.TryGetValue(code, out var mmOrders))
            {
                if (mmOrders.Count == 1)
                {
                    orderNumber = mmOrders.Single();
                    matchSource = MatchSourceMm;
                    matchStatus = MatchStatusMatched;
                    matchedMm++;
                }
                else
                {
                    matchSource = MatchSourceConflict;
                    matchStatus = MatchStatusConflicted;
                    RecordConflict(conflictsByCode, code, MatchSourceMm, mmOrders);
                }
            }

            var displayRow = new List<string>(row);
            while (displayRow.Count < headerRow.Count) displayRow.Add("");
            if (displayRow.Count > headerRow.Count) displayRow = displayRow[..headerRow.Count];
            if (code.Length > 0) displayRow[cTracking] = code;

            rows.Add(new CargoSellerReportRow(displayRow, code, orderNumber, matchSource, matchStatus));

            if (matchStatus == MatchStatusUnmatched)
                unmatched.Add(new CargoSellerReportUnmatchedRow(code, sellerName, displayRow));
        }

        var summary = BuildSummary(
            sellerName, filtered.Count, matchedMarketplace, matchedMm, unmatched.Count, conflictsByCode.Count,
            filtered, cCargoFee, cVat, cTotalAmount, cStatus);

        return new CargoSellerReportResult(
            sellerName, headerRow, cTracking, optionalColumns, rows, unmatched, [.. conflictsByCode.Values], summary);
    }

    /// <summary>The batch-level counts shown on screen right after <c>generate</c>, before the zip is
    /// built — a straight sum/list over every seller's already-computed result.</summary>
    public static CargoSellerReportBatchSummary Summarize(IReadOnlyList<CargoSellerReportResult> results)
    {
        var sellers = results
            .Select(r => new CargoSellerReportSellerTotal(
                r.SellerName, r.Summary.TotalRecords, r.Summary.TotalMatched, r.Summary.Unmatched, r.Summary.Conflicted))
            .ToList();

        return new CargoSellerReportBatchSummary(
            results.Count,
            results.Sum(r => r.Summary.TotalRecords),
            results.Sum(r => r.Summary.MatchedViaMarketplace),
            results.Sum(r => r.Summary.MatchedViaMmCargoData),
            results.Sum(r => r.Summary.TotalMatched),
            results.Sum(r => r.Summary.Unmatched),
            results.Sum(r => r.Summary.Conflicted),
            sellers);
    }

    static void RecordConflict(
        Dictionary<string, CargoSellerReportConflictRow> conflicts, string code, string source, HashSet<string> orders)
    {
        // The first source to see this code wins the record — Marketplace is checked before MM at the
        // call site, so a code already conflicted in Marketplace never gets a second entry from MM.
        if (conflicts.ContainsKey(code))
            return;

        conflicts[code] = new CargoSellerReportConflictRow(
            code, source, [.. orders.OrderBy(o => o, StringComparer.OrdinalIgnoreCase)]);
    }

    static CargoSellerReportSummary BuildSummary(
        string sellerName, int total, int matchedMarketplace, int matchedMm, int unmatchedCount, int conflictedCount,
        List<List<string>> filtered, int? cCargoFee, int? cVat, int? cTotalAmount, int? cStatus)
    {
        decimal? cargoFeeSum = cCargoFee.HasValue ? SumColumn(filtered, cCargoFee.Value) : null;
        decimal? vatSum = cVat.HasValue ? SumColumn(filtered, cVat.Value) : null;
        decimal? totalAmountSum = cTotalAmount.HasValue ? SumColumn(filtered, cTotalAmount.Value) : null;
        decimal? cargoPlusVat = cargoFeeSum.HasValue || vatSum.HasValue ? (cargoFeeSum ?? 0) + (vatSum ?? 0) : null;

        int? delivered = null;
        int? returned = null;
        int? canceled = null;

        if (cStatus.HasValue)
        {
            delivered = 0;
            returned = 0;
            canceled = 0;

            foreach (var row in filtered)
            {
                var status = TabularFile.GetCell(row, cStatus.Value);
                if (ContainsWord(status, "teslim")) delivered++;
                else if (ContainsWord(status, "iade") || ContainsWord(status, "İade")) returned++;
                else if (ContainsWord(status, "iptal") || ContainsWord(status, "cancel")) canceled++;
            }
        }

        return new CargoSellerReportSummary(
            sellerName, total, matchedMarketplace, matchedMm, matchedMarketplace + matchedMm,
            unmatchedCount, conflictedCount, cargoFeeSum, vatSum, cargoPlusVat, totalAmountSum,
            delivered, returned, canceled);
    }

    static decimal SumColumn(List<List<string>> rows, int col)
    {
        decimal sum = 0;
        foreach (var row in rows)
            sum += (decimal)TabularFile.ParseNumber(TabularFile.GetCell(row, col));

        // ParseNumber returns a double; rounding the final sum cleans up the binary floating-point
        // noise that a double accumulates (e.g. 1.89 + 0.90 landing a few ULPs off 2.79) before it
        // ever reaches the Özet sheet or the screen.
        return Math.Round(sum, 2, MidpointRounding.AwayFromZero);
    }

    static bool ContainsWord(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------
    // Lookup files — Marketplace and MM, the same two exports ReturnListBuilder reads
    // ---------------------------------------------------------------------

    static Dictionary<string, HashSet<string>> ReadMarketplaceLookup(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("The uploaded Marketplace return/exchange file is empty.");

        var idx = TabularFile.BuildHeaderIndex(table[0]);
        var cTracking = RequireColumn(idx, "Marketplace return/exchange", "Kargo Takip Kodu");
        var cOrderNo = RequireColumn(idx, "Marketplace return/exchange", "SiparişNo", "SiparisNo");

        return BuildLookup(table, cTracking, cOrderNo);
    }

    static Dictionary<string, HashSet<string>> ReadMmLookup(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("The uploaded MM Pazaryeri cargo data file is empty.");

        var idx = TabularFile.BuildHeaderIndex(table[0]);
        var cTracking = RequireColumn(idx, "MM Pazaryeri cargo data", "YK Takip Kodu");
        var cOrderNo = RequireColumn(idx, "MM Pazaryeri cargo data", "CustomerOrderNumber");

        return BuildLookup(table, cTracking, cOrderNo);
    }

    static Dictionary<string, HashSet<string>> BuildLookup(List<List<string>> table, int trackingCol, int orderCol)
    {
        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        for (var r = 1; r < table.Count; r++)
        {
            var code = NormalizeTrackingCode(TabularFile.GetCell(table[r], trackingCol));
            if (code.Length == 0)
                continue;

            var orderNo = TabularFile.GetCell(table[r], orderCol).Trim();
            if (orderNo.Length == 0)
                continue;

            if (!lookup.TryGetValue(code, out var orders))
                lookup[code] = orders = new HashSet<string>(StringComparer.Ordinal);

            orders.Add(orderNo);
        }

        return lookup;
    }

    // ---------------------------------------------------------------------
    // Cargo-invoice reading — header row is searched for, not assumed to be row 1
    // ---------------------------------------------------------------------

    static List<List<string>> ReadKargoTable(Stream stream, string fileName, out List<string> headerRow)
    {
        var isXlsx = fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".xls", StringComparison.OrdinalIgnoreCase);

        var allRows = isXlsx ? ReadXlsxWithDates(stream) : TabularFile.Read(stream, fileName);

        var headerRowIndex = -1;
        for (var r = 0; r < allRows.Count && r < HeaderSearchRowLimit; r++)
        {
            if (allRows[r].Any(cell => string.Equals(cell.Trim(), TrackingCodeHeader, StringComparison.OrdinalIgnoreCase)))
            {
                headerRowIndex = r;
                break;
            }
        }

        if (headerRowIndex < 0)
        {
            throw new InvalidOperationException(
                $"'{TrackingCodeHeader}' column was not found in the first {HeaderSearchRowLimit} rows of the uploaded cargo invoice file.");
        }

        headerRow = allRows[headerRowIndex];
        return [.. allRows.Skip(headerRowIndex + 1).Where(row => row.Any(c => c.Trim().Length > 0))];
    }

    /// <summary>
    /// The same cell-by-cell walk as <see cref="TabularFile"/>'s own XLSX reader, except a date cell
    /// is written out as a plain "dd.MM.yyyy" (or "dd.MM.yyyy HH:mm" when it carries a real time of
    /// day) rather than through <see cref="IXLCell.GetString"/> — which renders a date cell as .NET's
    /// default <c>DateTime.ToString()</c> ("05.08.2026 00:00:00"), not the tidy date the cargo-invoice
    /// export actually shows. Every other cell keeps reading through <c>GetString()</c> unchanged —
    /// that already reproduces Excel's own display for plain numbers (no scientific notation for a
    /// 12-digit id, confirmed against the real export) and text.
    /// </summary>
    static List<List<string>> ReadXlsxWithDates(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

        var table = new List<List<string>>();
        for (var r = 1; r <= lastRow; r++)
        {
            var row = new List<string>(lastCol);
            for (var c = 1; c <= lastCol; c++)
            {
                var cell = sheet.Cell(r, c);
                row.Add(cell.DataType == XLDataType.DateTime ? FormatDate(cell.GetDateTime()) : cell.GetString());
            }
            table.Add(row);
        }

        return table;
    }

    static string FormatDate(DateTime value) =>
        value.TimeOfDay == TimeSpan.Zero ? value.ToString("dd.MM.yyyy") : value.ToString("dd.MM.yyyy HH:mm");

    /// <summary>
    /// A takip kodu as it should be compared, not as Excel wrote it. Only touched when the text
    /// visibly carries scientific or decimal formatting (an <c>E</c> or a <c>.</c>) — a plain digit
    /// string is left alone, because a real leading zero can only survive in a text-typed cell and a
    /// true Excel number cell never has one.
    ///
    /// <para>Deliberately not shared with <see cref="VatSplitBuilder.NormalizeGtin"/> or any other
    /// module's normalizer — each keeps its own copy so a fix made here cannot silently move another
    /// report's figures.</para>
    /// </summary>
    static string NormalizeTrackingCode(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return "";

        var looksNumericFormatted = text.Contains('E', StringComparison.OrdinalIgnoreCase) || text.Contains('.');
        if (looksNumericFormatted &&
            decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            // decimal carries 28-29 significant digits with no binary rounding, so a 12-digit tracking
            // code written as "1.00285010611E11" round-trips to "100285010611" exactly.
            return number.ToString("F0", CultureInfo.InvariantCulture);
        }

        return text;
    }

    static int RequireColumn(Dictionary<string, int> idx, string fileDescription, params string[] names)
    {
        foreach (var name in names)
            if (idx.TryGetValue(name, out var i))
                return i;

        throw new InvalidOperationException(
            $"Required column '{names[0]}' was not found in the uploaded {fileDescription} file.");
    }

    static int? OptionalColumn(Dictionary<string, int> idx, IReadOnlyList<string> names)
    {
        foreach (var name in names)
            if (idx.TryGetValue(name, out var i))
                return i;

        return null;
    }

    // ---------------------------------------------------------------------
    // Zip — one workbook per seller plus one overview, for the monthly run
    // ---------------------------------------------------------------------

    /// <summary>One .xlsx per seller (see <see cref="BuildWorkbook"/>) plus one overview workbook
    /// listing every seller's totals, zipped together as the single file <c>download</c> returns.</summary>
    public static byte[] BuildZip(IReadOnlyList<CargoSellerReportResult> results, DateTime generatedAt)
    {
        var stamp = generatedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var result in results)
            {
                var entry = archive.CreateEntry(
                    $"kargo_raporu_{FileSafe(result.SellerName)}_{stamp}.xlsx", CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                var bytes = BuildWorkbook(result);
                entryStream.Write(bytes, 0, bytes.Length);
            }

            var overview = archive.CreateEntry($"Tum_Saticilar_Ozeti_{stamp}.xlsx", CompressionLevel.Optimal);
            using (var overviewStream = overview.Open())
            {
                var bytes = BuildOverviewWorkbook(results);
                overviewStream.Write(bytes, 0, bytes.Length);
            }
        }

        return output.ToArray();
    }

    /// <summary>Strips only the characters Windows file/entry names cannot hold. Turkish letters are
    /// kept — the report shows the seller name exactly as the uploaded file spelled it.</summary>
    public static string FileSafe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Where(c => !invalid.Contains(c))]).Trim();
        return cleaned.Length > 0 ? cleaned : "satici";
    }

    static byte[] BuildOverviewWorkbook(IReadOnlyList<CargoSellerReportResult> results)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Tüm Satıcılar Özeti");
        XlsxStyles.ApplyBaseFont(sheet);

        string[] headers =
        [
            "Satıcı", "Toplam Kargo Kaydı", "Marketplace Üzerinden Eşleşen",
            "MM PazaryeriKargoDatasi Üzerinden Eşleşen", "Toplam Eşleşen", "Eşleşmeyen", "Çakışmalı Eşleşen",
            "Toplam Kargo Bedeli", "Toplam KDV", "Toplam Kargo Bedeli + KDV"
        ];
        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).SetValue(headers[c]);
        XlsxStyles.StyleHeaderRow(sheet.Range(1, 1, 1, headers.Length));

        for (var r = 0; r < results.Count; r++)
        {
            var s = results[r].Summary;
            var excelRow = r + 2;

            sheet.Cell(excelRow, 1).SetValue(s.SellerName);
            sheet.Cell(excelRow, 2).Value = s.TotalRecords;
            sheet.Cell(excelRow, 3).Value = s.MatchedViaMarketplace;
            sheet.Cell(excelRow, 4).Value = s.MatchedViaMmCargoData;
            sheet.Cell(excelRow, 5).Value = s.TotalMatched;
            sheet.Cell(excelRow, 6).Value = s.Unmatched;
            sheet.Cell(excelRow, 7).Value = s.Conflicted;
            WriteMoneyCell(sheet.Cell(excelRow, 8), s.TotalCargoFee);
            WriteMoneyCell(sheet.Cell(excelRow, 9), s.TotalVat);
            WriteMoneyCell(sheet.Cell(excelRow, 10), s.TotalCargoFeePlusVat);
        }

        var totalRow = results.Count + 2;
        sheet.Cell(totalRow, 1).SetValue("TOPLAM");
        sheet.Cell(totalRow, 1).Style.Font.Bold = true;
        for (var c = 2; c <= headers.Length; c++)
        {
            var column = XLHelper.GetColumnLetterFromNumber(c);
            var cell = sheet.Cell(totalRow, c);
            cell.FormulaA1 = $"=SUM({column}2:{column}{totalRow - 1})";
            cell.Style.Font.Bold = true;
            if (c >= 8) cell.Style.NumberFormat.Format = "#,##0.00";
        }

        if (results.Count > 0)
        {
            XlsxStyles.ApplyThinBorders(sheet.Range(1, 1, totalRow, headers.Length));
            XlsxStyles.ApplyZebra(sheet, 2, results.Count + 1, 1, headers.Length);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    static void WriteMoneyCell(IXLCell cell, decimal? value)
    {
        if (!value.HasValue)
            return;

        cell.Value = value.Value;
        cell.Style.NumberFormat.Format = "#,##0.00";
    }

    // ---------------------------------------------------------------------
    // Workbook — one seller's report, one sheet: the cargo-invoice file's own template with
    // "Sipariş Numarası" added as the first column. No other column and no other sheet.
    // ---------------------------------------------------------------------

    public static byte[] BuildWorkbook(CargoSellerReportResult result)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Kargo Bedelleri");
        XlsxStyles.ApplyBaseFont(sheet);

        var headers = new List<string> { "Sipariş Numarası" };
        headers.AddRange(result.Headers);

        for (var c = 0; c < headers.Count; c++)
            sheet.Cell(1, c + 1).SetValue(headers[c]);
        XlsxStyles.StyleHeaderRow(sheet.Range(1, 1, 1, headers.Count));

        // Written as text so a long tracking code never round-trips back through Excel's own number
        // formatting into scientific notation the next time the file is opened. +2: 1 because Excel
        // columns are 1-based, 1 more because "Sipariş Numarası" now sits ahead of every source column.
        sheet.Column(result.TrackingColumnIndex + 2).Style.NumberFormat.Format = "@";

        for (var r = 0; r < result.Rows.Count; r++)
        {
            var row = result.Rows[r];
            var excelRow = r + 2;

            sheet.Cell(excelRow, 1).SetValue(row.OrderNumber);
            for (var c = 0; c < row.Cells.Count; c++)
                sheet.Cell(excelRow, c + 2).SetValue(row.Cells[c]);
        }

        var lastRow = result.Rows.Count + 1;
        if (lastRow > 1)
        {
            XlsxStyles.ApplyThinBorders(sheet.Range(1, 1, lastRow, headers.Count));
            XlsxStyles.ApplyZebra(sheet, 2, lastRow, 1, headers.Count);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }
}
