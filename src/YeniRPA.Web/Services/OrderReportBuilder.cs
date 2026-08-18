using System.Globalization;
using ClosedXML.Excel;
using YeniRPA.Web.Models;
using static YeniRPA.Web.Services.XlsxStyles;

namespace YeniRPA.Web.Services;

/// <summary>
/// Builds the Late Shipment &amp; Cancellation Report from an uploaded Mirakl orders export.
///
/// Two outputs share the same parsed rows:
/// <list type="bullet">
///   <item><see cref="Build"/> produces the four-sheet Excel workbook (Summary, Late Top 5,
///   Canceled Top 5, Data). Every rate on the Summary and Top-5 sheets is a live Excel formula
///   pointing at the Data sheet <em>by column letter</em>, so the Data column order is load-bearing.</item>
///   <item><see cref="BuildData"/> produces the JSON payload for the in-page dashboard. All KPI and
///   chart aggregation runs client-side so the date-range filter can recompute instantly without a
///   server round-trip.</item>
/// </list>
/// </summary>
public static partial class OrderReportBuilder
{
    const string LateYes = "Yes";
    const string LateNo = "No";
    const string LateNa = "-";
    public const string CanceledStatus = "Canceled";
    public const string RefundedStatus = "Refunded";

    /// <summary>
    /// Status of a line the customer actually received, and of one the seller turned down. Together
    /// with <see cref="CanceledStatus"/> these are the three outcomes reported on the Key metrics
    /// row, and all three are read from the Status column alone — no date column stands in for them.
    /// </summary>
    public const string ReceivedStatus = "Received";
    public const string RejectedStatus = "Rejected";
    const string IntegratedGroup = "Integrated";
    const string ManualGroup = "Manual";

    /// <summary>
    /// Reason the platform writes when it closes a line as delivered on its own because the carrier
    /// never reported a delivery. The received date on those rows is a bulk system timestamp, not a
    /// real delivery, so every delivery-duration metric in the dashboard excludes them.
    /// </summary>
    public const string AutoReceivedReason = "Received automatically";

    /// <summary>
    /// Minimum shipped lines before a seller is ranked in the best/worst on-time lists. Sent to the
    /// dashboard rather than duplicated in JavaScript so the Methodology page cannot disagree with
    /// the number the report actually applies.
    /// </summary>
    public const int MinSampleSize = 3;

    /// <summary>Minimum shipped lines before a lead-time change is suggested for a seller.</summary>
    public const int MinLeadTimeSample = 2;

    /// <summary>Carrier name fragments (case-insensitive) that have an automatic "Received" integration.</summary>
    public static readonly string[] IntegratedCarrierKeywords = ["Aras", "Yurtici", "Yurtiçi", "DHL", "Hepsijet", "MNG"];

    /// <summary>
    /// Applies the keyword rule to a <em>canonical</em> carrier name from <see cref="CarrierNames"/>
    /// rather than to the text the seller typed, so one carrier cannot end up with an Integrated
    /// badge on one spelling and a Manual badge on another. Both sides are folded, which is what
    /// lets the keyword "Yurtici" match the canonical "Yurtiçi Kargo".
    /// </summary>
    static bool IsIntegratedCarrier(string canonicalName)
    {
        var folded = CarrierNames.Fold(canonicalName);
        if (folded.Length == 0)
            return false;

        return IntegratedCarrierKeywords.Any(keyword =>
            folded.Contains(CarrierNames.Fold(keyword), StringComparison.Ordinal));
    }

    /// <summary>
    /// Cancellation reason codes as written into "Cancellation Request Payload". The export carries
    /// only the code, so the Turkish wording and the suggested follow-up are supplied here and travel
    /// with the dashboard payload. Codes not listed fall through and are shown as-is.
    /// </summary>
    public static readonly CancellationReasonLabel[] ReasonLabels =
    [
        new("CSTWSH", "Customer changed their mind",
            "Clarify the product page and which seller is being bought from."),
        new("CDELTM", "Delivery time too long",
            "Shorten the delivery promise; review the lead time of the sellers involved."),
        new("CITCHP", "Found it cheaper elsewhere",
            "Price competitiveness — set up price monitoring on these listings."),
        new("CITWNG", "Wrong item ordered",
            "Check variant and product matching quality in the catalogue."),
    ];

    sealed record OrderRow(
        string Seller,
        string OrderNumber,
        DateTime? DateCreated,
        string Status,
        double Quantity,
        double Amount,
        string Currency,
        DateTime? ShippingDeadline,
        DateTime? ShippingDate,
        string Reason,
        string ShippingCompany,
        DateTime? ReceivedDate,
        // --- extended fields: every one of these comes from an optional column and falls back to a
        // neutral value, so an export that predates them still produces the original dashboard.
        DateTime? AcceptanceDate,
        string CancellationRequestStatus,
        string CancellationReasonCode,
        double LeadTimeToShip,
        double AmountTransferredToSeller,
        double CanceledAmount,
        double RefundedAmount,
        bool HasInvoice,
        string Category,
        string Brand,
        string City,
        string TrackingUrl);

    /// <summary>
    /// Optional columns, i.e. the ones the extended dashboard sections read. A file missing any of
    /// them still produces a report; the affected section renders an empty state naming the column.
    /// </summary>
    /// <summary>
    /// Columns the report cannot be produced without. Validated up front so a short file fails with
    /// one clear message; the individual lookups further down use the same names.
    /// </summary>
    public static readonly string[] RequiredColumns =
    [
        "Seller", "Order number", "Date created", "Status", "Quantity", "Amount", "Currency",
        "Shipping deadline", "Shipping date", "Reason", "Shipping company", "Received date",
    ];

    public static readonly string[] OptionalColumns =
    [
        "Acceptance date", "Cancellation Request Status", "Cancellation Request Payload",
        "Lead time to ship", "Amount transferred to seller (including taxes)",
        "Total canceled amount (including taxes)", "Total refunded amount (including taxes)",
        "Order with invoice", "Category label", "Brand", "Shipping address city", "Tracking URL",
    ];

    public static byte[] Build(Stream inputStream)
    {
        var rows = ReadOrders(inputStream);
        if (rows.Count == 0)
            throw new InvalidOperationException("No order rows were found in the uploaded file.");

        using var workbook = new XLWorkbook();

        // The workbook and the dashboard go through the same index so the two outputs cannot merge
        // carriers differently — the Carrier Group column reads the canonical name written here.
        var carrierIndex = new CarrierIndex();
        var carrierSlots = rows.Select(r => carrierIndex.IndexOf(r.ShippingCompany, r.TrackingUrl)).ToList();
        var carrierGroups = carrierIndex.ToGroups();
        var carrierNames = carrierSlots.Select(slot => carrierGroups[slot].Name).ToList();

        var dataSheet = workbook.AddWorksheet("Data");
        WriteDataSheet(dataSheet, rows, carrierNames);
        var lastDataRow = rows.Count + 1;

        var top5Late = rows
            .Where(r => r.ShippingDate.HasValue)
            .GroupBy(r => r.Seller, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Seller = g.Key,
                LateCount = g.Count(r => r.ShippingDate!.Value > r.ShippingDeadline)
            })
            .Where(x => x.LateCount > 0)
            .OrderByDescending(x => x.LateCount)
            .Take(5)
            .Select(x => x.Seller)
            .ToList();

        var top5Canceled = rows
            .GroupBy(r => r.Seller, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Seller = g.Key,
                CanceledCount = g.Count(r => string.Equals(r.Status, CanceledStatus, StringComparison.OrdinalIgnoreCase))
            })
            .Where(x => x.CanceledCount > 0)
            .OrderByDescending(x => x.CanceledCount)
            .Take(5)
            .Select(x => x.Seller)
            .ToList();

        var statuses = rows
            .Select(r => r.Status)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summarySheet = workbook.AddWorksheet("Summary");
        WriteSummarySheet(summarySheet, lastDataRow, statuses);

        var lateSheet = workbook.AddWorksheet("Late Shipment - Top 5");
        WriteTopFiveSheet(
            lateSheet,
            top5Late,
            lastDataRow,
            headerMetricLabel: "Late Shipped Orders",
            headerTotalLabel: "Seller's Total Shipped Orders",
            headerRateLabel: "Late Shipment Rate %",
            metricColumnLetter: "K",
            metricValue: LateYes,
            totalCondition: "<>",
            totalColumnLetter: "I",
            barColor: XLColor.FromArgb(0xDC, 0x26, 0x26));

        var cancelSheet = workbook.AddWorksheet("Canceled - Top 5");
        WriteTopFiveSheet(
            cancelSheet,
            top5Canceled,
            lastDataRow,
            headerMetricLabel: "Canceled Orders",
            headerTotalLabel: "Seller's Total Orders",
            headerRateLabel: "Cancellation Rate %",
            metricColumnLetter: "L",
            metricValue: LateYes,
            totalCondition: null,
            totalColumnLetter: null,
            barColor: XLColor.FromArgb(0x1F, 0x38, 0x64));

        // Reorder: Summary, Late Top 5, Canceled Top 5, Data (Data must be last).
        summarySheet.Position = 1;
        lateSheet.Position = 2;
        cancelSheet.Position = 3;
        dataSheet.Position = 4;
        workbook.Worksheets.Worksheet("Summary").SetTabActive();

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    /// <summary>
    /// Parses the same orders export into the payload the in-page dashboard renders from. The short
    /// field names on <see cref="OrderReportRow"/> keep the payload small for large exports and are
    /// read by name in <c>wwwroot/js/order-report.js</c> — do not rename them.
    /// </summary>
    public static OrderReportData BuildData(Stream inputStream)
    {
        var rows = ReadOrders(inputStream, out var missingColumns);
        if (rows.Count == 0)
            throw new InvalidOperationException("No order rows were found in the uploaded file.");

        static string? Iso(DateTime? dt) => dt?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        // Index 0 is reserved as the "unknown" slot so that a row with no value can omit the key
        // entirely (default int 0) and the dashboard still resolves it to a blank label.
        var categories = new Interner();
        var brands = new Interner();
        var cities = new Interner();
        var carriers = new CarrierIndex();

        var payload = rows
            .Select(r => new OrderReportRow(
                s: r.Seller,
                dc: Iso(r.DateCreated),
                st: r.Status,
                amt: Math.Round(r.Amount, 2),
                cur: r.Currency,
                sd: Iso(r.ShippingDeadline),
                sh: Iso(r.ShippingDate),
                rd: Iso(r.ReceivedDate),
                rsn: r.Reason,
                k: carriers.IndexOf(r.ShippingCompany, r.TrackingUrl),
                ord: NullIfEmpty(r.OrderNumber),
                ac: Iso(r.AcceptanceDate),
                crs: CompressRequestStatus(r.CancellationRequestStatus),
                crr: NullIfEmpty(r.CancellationReasonCode),
                lt: r.LeadTimeToShip,
                pay: Math.Round(r.AmountTransferredToSeller, 2),
                can: Math.Round(r.CanceledAmount, 2),
                @ref: Math.Round(r.RefundedAmount, 2),
                inv: r.HasInvoice,
                ci: categories.IndexOf(r.Category),
                bi: brands.IndexOf(r.Brand),
                yi: cities.IndexOf(r.City)))
            .ToList();

        return new OrderReportData(
            payload,
            carriers.ToGroups(),
            CanceledStatus,
            RefundedStatus,
            ReceivedStatus,
            RejectedStatus,
            AutoReceivedReason,
            categories.Values,
            brands.Values,
            cities.Values,
            ReasonLabels,
            MinSampleSize,
            MinLeadTimeSample,
            missingColumns);
    }

    static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>"ACCEPTED"/"REJECTED" repeat on every cancelled line; one character is enough.</summary>
    static string? CompressRequestStatus(string value) => value.ToUpperInvariant() switch
    {
        "ACCEPTED" => "A",
        "REJECTED" => "R",
        _ => null,
    };

    /// <summary>Builds a de-duplicated label list and hands out indexes into it.</summary>
    sealed class Interner
    {
        readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        readonly List<string> _values = [""]; // slot 0 = unknown

        public IReadOnlyList<string> Values => _values;

        public int IndexOf(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            if (_index.TryGetValue(value, out var existing)) return existing;

            _values.Add(value);
            var next = _values.Count - 1;
            _index[value] = next;
            return next;
        }
    }

    /// <summary>
    /// The carrier equivalent of <see cref="Interner"/>, with the merging <see cref="CarrierNames"/>
    /// performs folded in: a line joins the group of its canonical carrier, or — when nothing in the
    /// catalogue recognises it — the group of its own folded spelling, so <c>SÜRAT KARGO</c> and
    /// <c>sürat kargo</c> still land together. Every raw spelling that arrives is counted, which is
    /// what supplies both the display name of an unrecognised group and the variant list shown on
    /// the carrier table.
    /// </summary>
    sealed class CarrierIndex
    {
        readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        readonly List<Bucket> _buckets = [new Bucket(null)]; // slot 0 = no shipping company recorded

        sealed class Bucket(string? canonical)
        {
            /// <summary>The catalogue name, or null when this group is a bare folded spelling.</summary>
            public string? Canonical { get; } = canonical;

            public Dictionary<string, int> Spellings { get; } = new(StringComparer.Ordinal);
        }

        public int IndexOf(string shippingCompany, string trackingUrl)
        {
            var raw = (shippingCompany ?? "").Trim();
            var canonical = CarrierNames.Resolve(raw, trackingUrl);

            // Prefixed so a catalogue name and a folded spelling can never collide on one key.
            var key = canonical is not null ? "c:" + canonical : "r:" + CarrierNames.Fold(raw);
            if (canonical is null && key.Length == 2)
                return 0; // nothing to group on: no name, and no tracking URL that named a carrier

            if (!_index.TryGetValue(key, out var index))
            {
                _buckets.Add(new Bucket(canonical));
                index = _buckets.Count - 1;
                _index[key] = index;
            }

            if (raw.Length > 0)
            {
                var spellings = _buckets[index].Spellings;
                spellings[raw] = spellings.TryGetValue(raw, out var seen) ? seen + 1 : 1;
            }

            return index;
        }

        /// <summary>
        /// Materialises the dictionary the dashboard indexes into. An unrecognised group is labelled
        /// with its most common spelling rather than with its fold key, so the table keeps showing
        /// the carrier the way the sellers write it.
        /// </summary>
        public IReadOnlyList<CarrierGroup> ToGroups() => [.. _buckets.Select((bucket, i) =>
        {
            if (i == 0)
                return new CarrierGroup("", false, []);

            var variants = bucket.Spellings
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key)
                .ToList();

            var name = bucket.Canonical ?? (variants.Count > 0 ? variants[0] : "");
            return new CarrierGroup(name, IsIntegratedCarrier(name), variants);
        })];
    }

    static List<OrderRow> ReadOrders(Stream inputStream) => ReadOrders(inputStream, out _);

    static List<OrderRow> ReadOrders(Stream inputStream, out List<string> missingColumns)
    {
        using var workbook = new XLWorkbook(inputStream);
        var sheet = workbook.Worksheets.First();
        var headerRow = sheet.Row(1);

        var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastHeaderCell = headerRow.LastCellUsed();
        var lastHeaderColumn = lastHeaderCell?.Address.ColumnNumber ?? 1;
        for (var c = 1; c <= lastHeaderColumn; c++)
        {
            var header = headerRow.Cell(c).GetString().Trim();
            if (!string.IsNullOrEmpty(header) && !columnIndex.ContainsKey(header))
                columnIndex[header] = c;
        }

        int Col(string name)
        {
            if (columnIndex.TryGetValue(name, out var idx))
                return idx;
            throw new InvalidOperationException($"Required column '{name}' was not found in the uploaded file.");
        }

        // Optional column: 0 means "not in this file"; every read below tolerates it.
        int Opt(string name) => columnIndex.TryGetValue(name, out var idx) ? idx : 0;

        // Fail on the first missing required column before doing any work, so the operator gets one
        // actionable message instead of whichever lookup happened to run first.
        foreach (var required in RequiredColumns)
            Col(required);

        var cSeller = Col("Seller");
        var cOrderNumber = Col("Order number");
        var cDateCreated = Col("Date created");
        var cStatus = Col("Status");
        var cQuantity = Col("Quantity");
        var cAmount = Col("Amount");
        var cCurrency = Col("Currency");
        var cShippingDeadline = Col("Shipping deadline");
        var cShippingDate = Col("Shipping date");
        var cReason = Col("Reason");
        var cShippingCompany = Col("Shipping company");
        var cReceivedDate = Col("Received date");

        var cAcceptanceDate = Opt("Acceptance date");
        var cCancelRequestStatus = Opt("Cancellation Request Status");
        var cCancelRequestPayload = Opt("Cancellation Request Payload");
        var cLeadTime = Opt("Lead time to ship");
        var cTransferred = Opt("Amount transferred to seller (including taxes)");
        var cCanceledAmount = Opt("Total canceled amount (including taxes)");
        var cRefundedAmount = Opt("Total refunded amount (including taxes)");
        var cInvoice = Opt("Order with invoice");
        var cCategory = Opt("Category label");
        var cBrand = Opt("Brand");
        var cCity = Opt("Shipping address city");
        var cTrackingUrl = Opt("Tracking URL");

        missingColumns = OptionalColumns.Where(name => !columnIndex.ContainsKey(name)).ToList();

        string Text(IXLRow row, int col) => col == 0 ? "" : row.Cell(col).GetString().Trim();
        double Number(IXLRow row, int col) => col == 0 ? 0 : ParseNumber(row.Cell(col));
        DateTime? Date(IXLRow row, int col) => col == 0 ? null : ParseDate(row.Cell(col));

        var rows = new List<OrderRow>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

        for (var r = 2; r <= lastRow; r++)
        {
            var row = sheet.Row(r);
            if (row.IsEmpty())
                continue;

            var orderNumber = row.Cell(cOrderNumber).GetString().Trim();
            var seller = row.Cell(cSeller).GetString().Trim();
            if (string.IsNullOrWhiteSpace(orderNumber) && string.IsNullOrWhiteSpace(seller))
                continue;

            rows.Add(new OrderRow(
                Seller: seller,
                OrderNumber: orderNumber,
                DateCreated: ParseDate(row.Cell(cDateCreated)),
                Status: row.Cell(cStatus).GetString().Trim(),
                Quantity: ParseNumber(row.Cell(cQuantity)),
                Amount: ParseNumber(row.Cell(cAmount)),
                Currency: row.Cell(cCurrency).GetString().Trim(),
                ShippingDeadline: ParseDate(row.Cell(cShippingDeadline)),
                ShippingDate: ParseDate(row.Cell(cShippingDate)),
                Reason: row.Cell(cReason).GetString().Trim(),
                ShippingCompany: row.Cell(cShippingCompany).GetString().Trim(),
                ReceivedDate: ParseDate(row.Cell(cReceivedDate)),
                AcceptanceDate: Date(row, cAcceptanceDate),
                CancellationRequestStatus: Text(row, cCancelRequestStatus),
                CancellationReasonCode: ExtractReasonCode(Text(row, cCancelRequestPayload)),
                LeadTimeToShip: Number(row, cLeadTime),
                AmountTransferredToSeller: Number(row, cTransferred),
                CanceledAmount: Number(row, cCanceledAmount),
                RefundedAmount: Number(row, cRefundedAmount),
                HasInvoice: string.Equals(Text(row, cInvoice), "yes", StringComparison.OrdinalIgnoreCase),
                Category: Text(row, cCategory),
                Brand: Text(row, cBrand),
                City: Text(row, cCity),
                TrackingUrl: Text(row, cTrackingUrl)));
        }

        return rows;
    }

    /// <summary>
    /// Pulls the reason code out of the cancellation request payload, which the export stores as a
    /// JSON blob such as <c>{"status":"ACCEPTED","reason":"CSTWSH","additionalNotes":"…"}</c>. Only
    /// the code is lifted; the free-text customer note is deliberately left in the file rather than
    /// pushed to the browser.
    /// </summary>
    static string ExtractReasonCode(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return "";

        var match = ReasonCodePattern().Match(payload);
        return match.Success ? match.Groups[1].Value : "";
    }

    [System.Text.RegularExpressions.GeneratedRegex("\"reason\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial System.Text.RegularExpressions.Regex ReasonCodePattern();

    static DateTime? ParseDate(IXLCell cell)
    {
        if (cell.IsEmpty())
            return null;

        if (cell.DataType == XLDataType.DateTime)
            return cell.GetDateTime();

        var text = cell.GetString().Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        string[] formats =
        [
            "MM/dd/yyyy hh:mm:ss tt",
            "MM/dd/yyyy h:mm:ss tt",
            "M/d/yyyy h:mm:ss tt",
        ];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            return parsed;

        return null;
    }

    static double ParseNumber(IXLCell cell)
    {
        if (cell.IsEmpty())
            return 0;

        if (cell.DataType == XLDataType.Number)
            return cell.GetDouble();

        var text = cell.GetString().Trim();
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>
    /// Writes the Data sheet. <paramref name="carrierNames"/> is parallel to <paramref name="rows"/>
    /// and holds the canonical carrier per line.
    ///
    /// <para>The canonical name is appended as the <em>last</em> column on purpose: the Summary and
    /// Top-5 sheets address this one by column letter, so inserting anywhere earlier would silently
    /// repoint every one of their formulas.</para>
    /// </summary>
    static void WriteDataSheet(IXLWorksheet sheet, List<OrderRow> rows, List<string> carrierNames)
    {
        ApplyBaseFont(sheet);

        string[] headers =
        [
            "Seller", "Order Number", "Date Created", "Status", "Quantity", "Amount", "Currency",
            "Shipping Deadline", "Shipping Date", "Cancellation/Rejection Reason",
            "Late Shipment?", "Canceled?", "Shipping Company", "Received Date", "Carrier Group",
            "Hours to Ship", "Hours to Receive", "Carrier (Normalized)"
        ];

        for (var i = 0; i < headers.Length; i++)
            sheet.Cell(1, i + 1).Value = headers[i];

        var lastRow = rows.Count + 1;

        for (var i = 0; i < rows.Count; i++)
        {
            var r = i + 2;
            var row = rows[i];

            sheet.Cell(r, 1).Value = row.Seller;
            sheet.Cell(r, 2).Value = row.OrderNumber;

            if (row.DateCreated.HasValue)
                sheet.Cell(r, 3).Value = row.DateCreated.Value;
            sheet.Cell(r, 3).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";

            sheet.Cell(r, 4).Value = row.Status;
            sheet.Cell(r, 5).Value = row.Quantity;
            sheet.Cell(r, 6).Value = row.Amount;
            sheet.Cell(r, 6).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(r, 7).Value = row.Currency;

            if (row.ShippingDeadline.HasValue)
                sheet.Cell(r, 8).Value = row.ShippingDeadline.Value;
            sheet.Cell(r, 8).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";

            if (row.ShippingDate.HasValue)
                sheet.Cell(r, 9).Value = row.ShippingDate.Value;
            sheet.Cell(r, 9).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";

            sheet.Cell(r, 10).Value = row.Reason;

            // Late Shipment?: "-" if not shipped yet, otherwise Yes/No compared to deadline.
            sheet.Cell(r, 11).FormulaA1 = $"=IF(I{r}=\"\",\"{LateNa}\",IF(I{r}>H{r},\"{LateYes}\",\"{LateNo}\"))";

            // Canceled?: Yes/No based on Status.
            sheet.Cell(r, 12).FormulaA1 = $"=IF(D{r}=\"{CanceledStatus}\",\"{LateYes}\",\"{LateNo}\")";

            sheet.Cell(r, 13).Value = row.ShippingCompany;

            if (row.ReceivedDate.HasValue)
                sheet.Cell(r, 14).Value = row.ReceivedDate.Value;
            sheet.Cell(r, 14).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";

            // Carrier (Normalized): every spelling of one carrier folded to a single name, so the
            // group below and any pivot built on this sheet count a carrier once.
            sheet.Cell(r, 18).Value = carrierNames[i];

            // Carrier Group: carriers with automatic "Received" integration vs. manually marked
            // ones. Read off the normalized name (R) rather than the seller's free text (M), which
            // is what keeps this column agreeing with the dashboard's Integrated/Manual badge.
            var carrierSearchTerms = string.Join(",",
                IntegratedCarrierKeywords.Select(k => $"ISNUMBER(SEARCH(\"{k}\",R{r}))"));
            sheet.Cell(r, 15).FormulaA1 = $"=IF(R{r}=\"\",\"{ManualGroup}\",IF(OR({carrierSearchTerms}),\"{IntegratedGroup}\",\"{ManualGroup}\"))";

            // Hours to Ship: Date Created -> Shipping Date, "-" until shipped.
            sheet.Cell(r, 16).FormulaA1 = $"=IF(I{r}=\"\",\"{LateNa}\",(I{r}-C{r})*24)";
            sheet.Cell(r, 16).Style.NumberFormat.Format = "0.0";

            // Hours to Receive: Shipping Date -> Received Date, "-" until both are known.
            sheet.Cell(r, 17).FormulaA1 = $"=IF(OR(I{r}=\"\",N{r}=\"\"),\"{LateNa}\",(N{r}-I{r})*24)";
            sheet.Cell(r, 17).Style.NumberFormat.Format = "0.0";
        }

        var headerRange = sheet.Range(1, 1, 1, headers.Length);
        StyleHeaderRow(headerRange);

        if (rows.Count > 0)
        {
            var fullRange = sheet.Range(1, 1, lastRow, headers.Length);
            ApplyThinBorders(fullRange);
            ApplyZebra(sheet, 2, lastRow, 1, headers.Length);

            sheet.Range(2, 11, lastRow, 11).AddConditionalFormat()
                .WhenEquals(LateYes)
                .Fill.SetBackgroundColor(XLColor.FromArgb(0xFE, 0xE2, 0xE2))
                .Font.SetFontColor(XLColor.FromArgb(0x99, 0x1B, 0x1B));

            sheet.Range(2, 12, lastRow, 12).AddConditionalFormat()
                .WhenEquals(LateYes)
                .Fill.SetBackgroundColor(XLColor.FromArgb(0xFE, 0xE2, 0xE2))
                .Font.SetFontColor(XLColor.FromArgb(0x99, 0x1B, 0x1B));
        }

        sheet.SheetView.FreezeRows(1);
        sheet.ShowGridLines = true;
        sheet.Columns().AdjustToContents();
        sheet.Column(1).Width = Math.Min(sheet.Column(1).Width, 28);
        sheet.Column(10).Width = Math.Min(sheet.Column(10).Width, 40);
    }

    static void WriteSummarySheet(IXLWorksheet sheet, int lastDataRow, List<string> statuses)
    {
        ApplyBaseFont(sheet);
        sheet.ShowGridLines = false;

        sheet.Cell("B2").Value = "Late Shipment & Cancellation Report";
        sheet.Cell("B2").Style.Font.FontSize = 18;
        sheet.Cell("B2").Style.Font.Bold = true;
        sheet.Cell("B2").Style.Font.FontColor = NavyColor;

        sheet.Cell("B3").Value = "Prepared on:";
        sheet.Cell("C3").Value = DateTime.Now;
        sheet.Cell("C3").Style.DateFormat.Format = "yyyy-mm-dd HH:mm";
        sheet.Cell("B3").Style.Font.FontColor = XLColor.FromArgb(0x6B, 0x72, 0x80);
        sheet.Cell("C3").Style.Font.FontColor = XLColor.FromArgb(0x6B, 0x72, 0x80);

        // KPI boxes: label in row 5, formula value in row 6, one per column starting at B. These
        // mirror the dashboard's Key metrics row, so the two outputs cannot tell different stories.
        //
        // The late-shipment denominator is the only one that is not a visible box: it counts lines
        // with a shipping date rather than lines with a status, so it is inlined into the rate
        // formula instead of taking a column of its own.
        var shippedCount = $"COUNTIF(Data!I2:I{lastDataRow},\"<>\")";
        var kpis = new (string Label, string Formula, bool IsPercent, bool Red)[]
        {
            ("Total Order Lines", $"=COUNTA(Data!B2:B{lastDataRow})", false, false),
            ("Received Orders", $"=COUNTIF(Data!D2:D{lastDataRow},\"{ReceivedStatus}\")", false, false),
            ("Received Rate %", $"=IF(B6=0,0,C6/B6)", true, false),
            ("Rejected Orders", $"=COUNTIF(Data!D2:D{lastDataRow},\"{RejectedStatus}\")", false, true),
            ("Rejection Rate %", $"=IF(B6=0,0,E6/B6)", true, true),
            ("Late Shipped Orders", $"=COUNTIF(Data!K2:K{lastDataRow},\"{LateYes}\")", false, true),
            ("Late Shipment Rate %", $"=IF({shippedCount}=0,0,G6/{shippedCount})", true, true),
            ("Canceled Orders", $"=COUNTIF(Data!L2:L{lastDataRow},\"{LateYes}\")", false, true),
            ("Cancellation Rate %", $"=IF(B6=0,0,I6/B6)", true, true),
        };

        var col = 2; // start at column B
        var labelRow = 5;
        var valueRow = 6;
        foreach (var kpi in kpis)
        {
            var labelCell = sheet.Cell(labelRow, col);
            var valueCell = sheet.Cell(valueRow, col);

            labelCell.Value = kpi.Label;
            labelCell.Style.Font.Bold = true;
            labelCell.Style.Font.FontSize = 9;
            labelCell.Style.Font.FontColor = XLColor.FromArgb(0x6B, 0x72, 0x80);
            labelCell.Style.Alignment.WrapText = true;

            valueCell.FormulaA1 = kpi.Formula;
            valueCell.Style.Font.Bold = true;
            valueCell.Style.Font.FontSize = 20;
            valueCell.Style.Font.FontColor = kpi.Red ? RedColor : NavyColor;
            if (kpi.IsPercent)
                valueCell.Style.NumberFormat.Format = "0.0%";

            var box = sheet.Range(labelRow, col, valueRow, col);
            box.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            box.Style.Border.OutsideBorderColor = XLColor.FromArgb(0xD1, 0xD5, 0xDB);
            box.Style.Fill.BackgroundColor = XLColor.FromArgb(0xF9, 0xFA, 0xFB);
            box.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            sheet.Column(col).Width = 16;
            col++;
        }

        sheet.Cell(8, 2).Value =
            $"Note: received, rejected and canceled are counted from the Status column alone " +
            $"(Status = {ReceivedStatus} / {RejectedStatus} / {CanceledStatus}), and each rate is that " +
            "count over all order lines. \"Late\" is the exception: it means the shipping date is filled in " +
            "and is later than the shipping deadline, so rows without a shipping date yet are excluded from " +
            "the late-shipment rate denominator. A single order with multiple line items counts each line separately.";
        sheet.Cell(8, 2).Style.Font.Italic = true;
        sheet.Cell(8, 2).Style.Font.FontColor = XLColor.FromArgb(0x9C, 0xA3, 0xAF);
        sheet.Cell(8, 2).Style.Font.FontSize = 9;
        sheet.Range(8, 2, 8, 10).Merge();
        sheet.Row(8).Height = 45;
        sheet.Cell(8, 2).Style.Alignment.WrapText = true;

        // Shipping & receiving time metrics (placed alongside the status distribution table,
        // in columns F-H so the row count of the distribution table below never overlaps it).
        sheet.Cell(10, 6).Value = "Shipping & Receiving Time (hours)";
        sheet.Cell(10, 6).Style.Font.Bold = true;
        sheet.Cell(10, 6).Style.Font.FontColor = NavyColor;
        sheet.Cell(10, 6).Style.Font.FontSize = 12;

        sheet.Cell(11, 6).Value = "Metric";
        sheet.Cell(11, 7).Value = "Avg. Hours";
        var timeHeader = sheet.Range(11, 6, 11, 7);
        StyleHeaderRow(timeHeader);

        var carrierList = string.Join(", ", IntegratedCarrierKeywords.Distinct(StringComparer.OrdinalIgnoreCase));
        var timeMetrics = new (string Label, string Formula)[]
        {
            ("Avg. Hours to Ship (Created → Shipped)",
                $"=IFERROR(AVERAGEIF(Data!P2:P{lastDataRow},\"<>{LateNa}\"),0)"),
            ($"Avg. Hours to Receive – Integrated Carriers ({carrierList})",
                $"=IFERROR(AVERAGEIFS(Data!Q2:Q{lastDataRow},Data!O2:O{lastDataRow},\"{IntegratedGroup}\",Data!Q2:Q{lastDataRow},\"<>{LateNa}\"),0)"),
            ("Avg. Hours to Receive – Manual Carriers (no integration)",
                $"=IFERROR(AVERAGEIFS(Data!Q2:Q{lastDataRow},Data!O2:O{lastDataRow},\"{ManualGroup}\",Data!Q2:Q{lastDataRow},\"<>{LateNa}\"),0)"),
        };

        var timeRow = 12;
        foreach (var metric in timeMetrics)
        {
            sheet.Cell(timeRow, 6).Value = metric.Label;
            sheet.Cell(timeRow, 6).Style.Alignment.WrapText = true;
            sheet.Cell(timeRow, 7).FormulaA1 = metric.Formula;
            sheet.Cell(timeRow, 7).Style.NumberFormat.Format = "0.0";
            timeRow++;
        }

        var timeTableRange = sheet.Range(11, 6, timeRow - 1, 7);
        ApplyThinBorders(timeTableRange);
        ApplyZebra(sheet, 12, timeRow - 1, 6, 7);
        sheet.Column(6).Width = 46;
        sheet.Column(7).Width = 14;

        // Status distribution table.
        sheet.Cell(10, 2).Value = "Status Distribution";
        sheet.Cell(10, 2).Style.Font.Bold = true;
        sheet.Cell(10, 2).Style.Font.FontColor = NavyColor;
        sheet.Cell(10, 2).Style.Font.FontSize = 12;

        sheet.Cell(11, 2).Value = "Status";
        sheet.Cell(11, 3).Value = "Count";
        sheet.Cell(11, 4).Value = "Share of Total %";
        var statusHeader = sheet.Range(11, 2, 11, 4);
        StyleHeaderRow(statusHeader);

        var row = 12;
        foreach (var status in statuses)
        {
            sheet.Cell(row, 2).Value = status;
            sheet.Cell(row, 3).FormulaA1 = $"=COUNTIF(Data!D2:D{lastDataRow},\"{EscapeQuotes(status)}\")";
            sheet.Cell(row, 4).FormulaA1 = $"=IF($B$6=0,0,C{row}/$B$6)";
            sheet.Cell(row, 4).Style.NumberFormat.Format = "0.0%";
            row++;
        }

        var statusTableRange = sheet.Range(11, 2, row - 1, 4);
        ApplyThinBorders(statusTableRange);
        ApplyZebra(sheet, 12, row - 1, 2, 4);

        sheet.Columns(2, 5).AdjustToContents();
        sheet.Column(2).Width = Math.Max(sheet.Column(2).Width, 22);
        sheet.Column(6).Width = 46;
        sheet.Column(7).Width = 14;
    }

    static string EscapeQuotes(string value) => value.Replace("\"", "\"\"");

    static void WriteTopFiveSheet(
        IXLWorksheet sheet,
        List<string> sellers,
        int lastDataRow,
        string headerMetricLabel,
        string headerTotalLabel,
        string headerRateLabel,
        string metricColumnLetter,
        string metricValue,
        string? totalCondition,
        string? totalColumnLetter,
        XLColor barColor)
    {
        ApplyBaseFont(sheet);
        sheet.ShowGridLines = false;

        sheet.Cell(1, 1).Value = "Seller";
        sheet.Cell(1, 2).Value = headerMetricLabel;
        sheet.Cell(1, 3).Value = headerTotalLabel;
        sheet.Cell(1, 4).Value = headerRateLabel;

        var headerRange = sheet.Range(1, 1, 1, 4);
        StyleHeaderRow(headerRange);

        var row = 2;
        for (; row - 2 < sellers.Count; row++)
        {
            var sellerName = sellers[row - 2];
            var escaped = EscapeQuotes(sellerName);
            sheet.Cell(row, 1).Value = sellerName;
            sheet.Cell(row, 2).FormulaA1 =
                $"=COUNTIFS(Data!A2:A{lastDataRow},\"{escaped}\",Data!{metricColumnLetter}2:{metricColumnLetter}{lastDataRow},\"{metricValue}\")";

            if (totalColumnLetter is not null && totalCondition is not null)
            {
                sheet.Cell(row, 3).FormulaA1 =
                    $"=COUNTIFS(Data!A2:A{lastDataRow},\"{escaped}\",Data!{totalColumnLetter}2:{totalColumnLetter}{lastDataRow},\"{totalCondition}\")";
            }
            else
            {
                sheet.Cell(row, 3).FormulaA1 = $"=COUNTIF(Data!A2:A{lastDataRow},\"{escaped}\")";
            }

            sheet.Cell(row, 4).FormulaA1 = $"=IF(C{row}=0,0,B{row}/C{row})";
            sheet.Cell(row, 4).Style.NumberFormat.Format = "0.0%";
        }

        var lastRow = row - 1;
        if (lastRow >= 2)
        {
            var tableRange = sheet.Range(1, 1, lastRow, 4);
            ApplyThinBorders(tableRange);
            ApplyZebra(sheet, 2, lastRow, 1, 4);

            // Horizontal "bar chart" visualization using data bars on the metric column.
            sheet.Range(2, 2, lastRow, 2).AddConditionalFormat()
                .DataBar(barColor, true);
        }

        sheet.Columns().AdjustToContents();
        sheet.Column(1).Width = Math.Max(sheet.Column(1).Width, 24);
    }
}
