using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Builds the Return SLA Report: tracks orders whose return shipment has missed the 15-day SLA
/// (measured from the date the return was shipped back to the seller), highlights orders that have
/// crossed a 10-day early-warning threshold, and computes the refund/payment time for canceled,
/// rejected or refunded orders.
///
/// Three files are uploaded on every run:
///  - orders export (orders.xlsx/csv) from Mirakl,
///  - Return template A ("Marketplace Iade &amp; Degisim Talepleri...") - a row counts as "shipped to
///    seller" when "Kargo Takip Kodu" is filled in. This template has no explicit ship date column,
///    so "Talep Tarihi" (request date) is used as the closest available proxy for the SLA start date.
///  - Return template B ("NNNNNN-MP.csv") - a row counts as "shipped to seller" when "YK Takip Kodu"
///    is filled in. "Kargo Kodu Oluşturma Tarihi" is used as the SLA start date.
///
/// Rows from both templates are matched to the orders file by order number, and only orders whose
/// Mirakl status indicates a confirmed return (Refused / Canceled / Refunded / Rejected) are counted.
///
/// NOTE: the column names read from the uploaded files are Turkish because that is what the source
/// exports actually contain. They are data, not UI text, and must never be translated.
/// </summary>
public static class ReturnSlaReportBuilder
{
    public const int SlaDays = 15;
    public const int WarningDays = 10;

    static readonly string[] ConfirmedReturnKeywords =
        ["refused", "cancel", "refund", "reject", "iade", "ret"];

    sealed record OrderInfo(
        string OrderNumberRaw,
        string OrderNumberNumeric,
        string Status,
        DateTime? DateCreated,
        DateTime? CustomerDebitDate,
        string Seller,
        double Amount,
        string Currency);

    sealed record ReturnCandidate(
        string Source,
        string OrderNumberRaw,
        string OrderNumberNumeric,
        DateTime? ShippedToSellerDate,
        string ReasonOrDetail);

    /// <summary>
    /// The <paramref name="ordersFileName"/> / <paramref name="templateAFileName"/> /
    /// <paramref name="templateBFileName"/> arguments are load-bearing: the table reader picks the
    /// XLSX or the CSV path purely from the file extension.
    /// </summary>
    public static ReturnSlaData BuildData(
        Stream ordersStream, string ordersFileName,
        Stream templateAStream, string templateAFileName,
        Stream templateBStream, string templateBFileName)
    {
        var orders = ReadOrders(ordersStream, ordersFileName);
        if (orders.Count == 0)
            throw new InvalidOperationException("No order rows were found in the uploaded orders file.");

        var ordersByNumeric = new Dictionary<string, OrderInfo>(StringComparer.OrdinalIgnoreCase);
        var ordersByRaw = new Dictionary<string, OrderInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in orders)
        {
            if (!string.IsNullOrEmpty(o.OrderNumberNumeric) && !ordersByNumeric.ContainsKey(o.OrderNumberNumeric))
                ordersByNumeric[o.OrderNumberNumeric] = o;
            if (!string.IsNullOrEmpty(o.OrderNumberRaw) && !ordersByRaw.ContainsKey(o.OrderNumberRaw))
                ordersByRaw[o.OrderNumberRaw] = o;
        }

        var candidatesA = ReadTemplateA(templateAStream, templateAFileName);
        var candidatesB = ReadTemplateB(templateBStream, templateBFileName);
        var allCandidates = candidatesA.Concat(candidatesB).ToList();

        OrderInfo? Match(ReturnCandidate c)
        {
            if (!string.IsNullOrEmpty(c.OrderNumberNumeric) && ordersByNumeric.TryGetValue(c.OrderNumberNumeric, out var o1))
                return o1;
            if (!string.IsNullOrEmpty(c.OrderNumberRaw) && ordersByRaw.TryGetValue(c.OrderNumberRaw, out var o2))
                return o2;
            return null;
        }

        static bool IsConfirmedReturn(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return false;
            var s = status.ToLowerInvariant();
            return ConfirmedReturnKeywords.Any(s.Contains);
        }

        var today = DateTime.Now;
        var rows = new List<ReturnSlaRow>();

        foreach (var c in allCandidates)
        {
            var order = Match(c);
            var matched = order is not null;
            var isConfirmedReturn = matched && IsConfirmedReturn(order!.Status);
            var elapsedDays = c.ShippedToSellerDate.HasValue
                ? (today - c.ShippedToSellerDate.Value).TotalDays
                : (double?)null;
            var slaMissed = isConfirmedReturn == false && elapsedDays.HasValue && elapsedDays.Value > SlaDays;
            var pastWarning = elapsedDays.HasValue && elapsedDays.Value > WarningDays && elapsedDays.Value <= SlaDays;

            rows.Add(new ReturnSlaRow(
                Source: c.Source,
                OrderNumber: string.IsNullOrEmpty(c.OrderNumberRaw) ? c.OrderNumberNumeric : c.OrderNumberRaw,
                Seller: order?.Seller ?? "-",
                Status: order?.Status ?? UnmatchedStatus,
                ShippedToSellerDate: c.ShippedToSellerDate?.ToString("yyyy-MM-dd"),
                ElapsedDays: elapsedDays.HasValue ? Math.Round(elapsedDays.Value, 1) : (double?)null,
                SlaDays: SlaDays,
                IsConfirmedReturn: isConfirmedReturn,
                SlaMissed: slaMissed,
                PastWarning: pastWarning,
                Reason: c.ReasonOrDetail));
        }

        // Payment time for canceled / refunded / rejected orders (from the full orders file, not
        // limited to the ones present in the return templates).
        var paymentRows = orders
            .Where(o => IsConfirmedReturn(o.Status) && o.DateCreated.HasValue && o.CustomerDebitDate.HasValue)
            .Select(o => new ReturnSlaPaymentRow(
                OrderNumber: o.OrderNumberRaw,
                Seller: o.Seller,
                Status: o.Status,
                Amount: Math.Round(o.Amount, 2),
                Currency: o.Currency,
                DateCreated: o.DateCreated!.Value.ToString("yyyy-MM-dd"),
                DebitDate: o.CustomerDebitDate!.Value.ToString("yyyy-MM-dd"),
                PaymentDays: Math.Round((o.CustomerDebitDate.Value - o.DateCreated.Value).TotalDays, 1)))
            .ToList();

        return new ReturnSlaData(rows, paymentRows);
    }

    /// <summary>Status assigned to a return row that has no counterpart in the orders export.</summary>
    public const string UnmatchedStatus = "Not matched in orders file";

    // ---------------------------------------------------------------------
    // Orders file parsing
    // ---------------------------------------------------------------------

    static List<OrderInfo> ReadOrders(Stream stream, string fileName)
    {
        var table = ReadTable(stream, fileName);
        if (table.Count == 0) return [];

        var header = table[0];
        var idx = BuildHeaderIndex(header);

        int Col(params string[] names)
        {
            foreach (var n in names)
                if (idx.TryGetValue(n, out var i))
                    return i;
            throw new InvalidOperationException($"Required column '{names[0]}' was not found in the uploaded orders file.");
        }

        var cOrderNumber = Col("Order number");
        var cStatus = Col("Status");
        var cDateCreated = Col("Date created");
        var cDebitDate = Col("Customer debit date");
        var cSeller = Col("Seller");
        var cAmount = Col("Amount");
        var cCurrency = Col("Currency");

        var result = new List<OrderInfo>();
        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];
            var orderNumberRaw = GetCell(row, cOrderNumber).Trim();
            if (string.IsNullOrWhiteSpace(orderNumberRaw))
                continue;

            result.Add(new OrderInfo(
                OrderNumberRaw: orderNumberRaw,
                OrderNumberNumeric: ExtractNumeric(orderNumberRaw),
                Status: GetCell(row, cStatus).Trim(),
                DateCreated: ParseDate(GetCell(row, cDateCreated)),
                CustomerDebitDate: ParseDate(GetCell(row, cDebitDate)),
                Seller: GetCell(row, cSeller).Trim(),
                Amount: ParseNumber(GetCell(row, cAmount)),
                Currency: GetCell(row, cCurrency).Trim()));
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Return template A: "Marketplace Iade & Degisim Talepleri" — filled "Kargo Takip Kodu" marks
    // a row as shipped to the seller. No dedicated ship date column exists, so "Talep Tarihi" is
    // used as the closest available proxy.
    // ---------------------------------------------------------------------

    static List<ReturnCandidate> ReadTemplateA(Stream stream, string fileName)
    {
        var table = ReadTable(stream, fileName);
        if (table.Count == 0) return [];

        var header = table[0];
        var idx = BuildHeaderIndex(header);

        int? ColOrNull(params string[] names)
        {
            foreach (var n in names)
                if (idx.TryGetValue(n, out var i))
                    return i;
            return null;
        }

        var cOrderNo = ColOrNull("SiparişNo", "SiparisNo") ?? throw new InvalidOperationException("Required column 'SiparişNo' was not found in the return template A file.");
        var cTrackingCode = ColOrNull("Kargo Takip Kodu") ?? throw new InvalidOperationException("Required column 'Kargo Takip Kodu' was not found in the return template A file.");
        var cRequestDate = ColOrNull("Talep Tarihi");
        var cReason = ColOrNull("Talep Nedeni");

        var result = new List<ReturnCandidate>();
        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];
            var trackingCode = GetCell(row, cTrackingCode).Trim();
            if (string.IsNullOrWhiteSpace(trackingCode))
                continue; // only rows actually shipped to the seller count.

            var orderNoRaw = GetCell(row, cOrderNo).Trim();
            if (string.IsNullOrWhiteSpace(orderNoRaw))
                continue;

            result.Add(new ReturnCandidate(
                Source: "Marketplace Return Requests",
                OrderNumberRaw: orderNoRaw,
                OrderNumberNumeric: ExtractNumeric(orderNoRaw),
                ShippedToSellerDate: cRequestDate.HasValue ? ParseDate(GetCell(row, cRequestDate.Value)) : null,
                ReasonOrDetail: cReason.HasValue ? GetCell(row, cReason.Value).Trim() : ""));
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Return template B: "NNNNNN-MP.csv" — filled "YK Takip Kodu" marks a row as shipped to the
    // seller. "Kargo Kodu Oluşturma Tarihi" is the actual ship date.
    // ---------------------------------------------------------------------

    static List<ReturnCandidate> ReadTemplateB(Stream stream, string fileName)
    {
        var table = ReadTable(stream, fileName);
        if (table.Count == 0) return [];

        var header = table[0];
        var idx = BuildHeaderIndex(header);

        int? ColOrNull(params string[] names)
        {
            foreach (var n in names)
                if (idx.TryGetValue(n, out var i))
                    return i;
            return null;
        }

        var cOrderNo = ColOrNull("CustomerOrderNumber") ?? throw new InvalidOperationException("Required column 'CustomerOrderNumber' was not found in the return template B file.");
        var cMarketPlaceId = ColOrNull("MarketPlaceId");
        var cTrackingCode = ColOrNull("YK Takip Kodu") ?? throw new InvalidOperationException("Required column 'YK Takip Kodu' was not found in the return template B file.");
        var cShipDate = ColOrNull("Kargo Kodu Oluşturma Tarihi", "Kargo Kodu Olusturma Tarihi");
        var cState = ColOrNull("State");

        var result = new List<ReturnCandidate>();
        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];
            var trackingCode = GetCell(row, cTrackingCode).Trim();
            if (string.IsNullOrWhiteSpace(trackingCode))
                continue; // only rows actually shipped to the seller count.

            var orderNoRaw = GetCell(row, cOrderNo).Trim();
            var marketPlaceId = cMarketPlaceId.HasValue ? GetCell(row, cMarketPlaceId.Value).Trim() : "";
            if (string.IsNullOrWhiteSpace(orderNoRaw) && string.IsNullOrWhiteSpace(marketPlaceId))
                continue;

            // Deliberate asymmetry, preserved from the original: the display number prefers the
            // marketplace id, but the numeric match key always comes from CustomerOrderNumber.
            result.Add(new ReturnCandidate(
                Source: "300726-MP",
                OrderNumberRaw: string.IsNullOrWhiteSpace(marketPlaceId) ? orderNoRaw : marketPlaceId,
                OrderNumberNumeric: ExtractNumeric(orderNoRaw),
                ShippedToSellerDate: cShipDate.HasValue ? ParseDate(GetCell(row, cShipDate.Value)) : null,
                ReasonOrDetail: cState.HasValue ? GetCell(row, cState.Value).Trim() : ""));
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Generic helpers
    // ---------------------------------------------------------------------

    static string ExtractNumeric(string orderNumber)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
            return "";

        var digits = new string(orderNumber.Where(char.IsDigit).ToArray());
        return digits;
    }

    // The file readers below live in TabularFile, shared with ReturnListBuilder. They are forwarded
    // rather than inlined at the call sites so this report keeps running on byte-identical code.

    static Dictionary<string, int> BuildHeaderIndex(List<string> header) => TabularFile.BuildHeaderIndex(header);

    static string GetCell(List<string> row, int? col) => TabularFile.GetCell(row, col);

    static string GetCell(List<string> row, int col) => TabularFile.GetCell(row, col);

    static List<List<string>> ReadTable(Stream stream, string fileName) => TabularFile.Read(stream, fileName);

    static DateTime? ParseDate(string text) => TabularFile.ParseDate(text);

    static double ParseNumber(string text) => TabularFile.ParseNumber(text);
}
