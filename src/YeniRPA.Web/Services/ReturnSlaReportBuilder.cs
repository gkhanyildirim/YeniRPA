using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Builds the Return SLA Report: tracks orders whose return shipment has missed the 20-day SLA
/// (measured from the date the return was shipped back to the seller), highlights orders that have
/// crossed a 15-day early-warning threshold, and computes the refund/payment time for canceled,
/// rejected or refunded orders.
///
/// The orders export is required; the two return templates are not — at least one of them has to be
/// uploaded, and a template that is left out simply contributes no rows. Refund times are unaffected
/// either way because they are computed from the orders file alone.
///  - orders export (orders.xlsx/csv) from Mirakl,
///  - Return template A ("Marketplace Iade &amp; Degisim Talepleri...") - a row counts as "shipped to
///    seller" when "Kargo Takip Kodu" holds a real code. This template has no explicit ship date
///    column, so "Talep Tarihi" (request date) is used as the closest available proxy for the SLA
///    start date.
///  - Return template B ("NNNNNN-MP.csv") - a row counts as "shipped to seller" when "YK Takip Kodu"
///    holds a real code. "Kargo Kodu Oluşturma Tarihi" is used as the SLA start date.
///
/// <para><b>Matching is on <see cref="TabularFile.OrderCore"/>, and that is the point of this
/// report.</b> The templates carry the bare customer order number (<c>321097726</c>) while the orders
/// export carries the full Mirakl form (<c>01259_321097726-A</c>); the SLA verdict is a property of
/// the order's <em>status</em>, so a report that cannot join the two sides cannot tell a return that
/// is genuinely overdue from one that was completed weeks ago. An earlier version keyed on "every
/// digit in the number", which never matched a single row: every seller came out blank and every row
/// past the SLA window was reported as a breach.</para>
///
/// One customer order splits per seller into …-A / …-B, so a bare number can match several full ones.
/// The seller id on template A and the MarketPlaceId on template B resolve that; where they cannot,
/// the candidates' statuses are used if they all agree, and anything still undecided is reported for
/// review rather than guessed at.
///
/// NOTE: the column names read from the uploaded files are Turkish because that is what the source
/// exports actually contain. They are data, not UI text, and must never be translated.
/// </summary>
public static class ReturnSlaReportBuilder
{
    /// <summary>A return still open past this many days has breached the SLA.</summary>
    public const int SlaDays = 20;

    /// <summary>A return still open past this many days is flagged early, before it breaches.</summary>
    public const int WarningDays = 15;

    static readonly string[] ConfirmedReturnKeywords =
        ["refused", "cancel", "refund", "reject", "iade", "ret"];

    // Match states, as they travel to the dashboard.
    public const string MatchedState = "matched";
    public const string MatchedByStatusState = "matched-by-status";
    public const string AmbiguousState = "ambiguous";
    public const string NotFoundState = "not-found";

    /// <summary>Status shown for a return row whose order is not in the uploaded export.</summary>
    public const string UnmatchedStatus = "Not in the orders export";

    /// <summary>Status shown when a bare order number matches several orders that disagree.</summary>
    public const string AmbiguousStatus = "Ambiguous order number";

    /// <summary>
    /// One order of the orders export — i.e. one full order number, with its lines folded together.
    /// The export is one row per order line, so a single order can carry several statuses; the return
    /// concerns the order, which is why <see cref="IsConfirmedReturn"/> is true when <em>any</em> line
    /// of it has been refused, canceled, refunded or rejected.
    /// </summary>
    sealed record OrderRef(
        string FullNumber,
        string Core,
        string SellerId,
        string Seller,
        IReadOnlyList<string> Statuses,
        bool IsConfirmedReturn)
    {
        /// <summary>The status to print: one line's status, or every distinct one when they differ.</summary>
        public string StatusText => string.Join(" / ", Statuses);
    }

    /// <summary>One order line, kept flat for the refund-time table.</summary>
    sealed record OrderLine(
        string OrderNumberRaw,
        string Status,
        DateTime? DateCreated,
        DateTime? CustomerDebitDate,
        string Seller,
        string SellerId,
        double Amount,
        string Currency);

    sealed record ReturnCandidate(
        string Source,
        string SourceOrderNo,
        string Core,
        /// <summary>Template B carries the full order number itself; template A has to look it up.</summary>
        string? FullOrderNumber,
        string SellerId,
        DateTime? ShippedToSellerDate,
        string ReasonOrDetail);

    /// <summary>What a candidate resolved to, and how sure the resolution is.</summary>
    readonly record struct Resolution(
        string State,
        OrderRef? Order,
        int MatchCount,
        bool IsConfirmedReturn,
        string StatusText,
        string OrderNumber,
        string Seller);

    /// <summary>
    /// The <paramref name="ordersFileName"/> / <paramref name="templateAFileName"/> /
    /// <paramref name="templateBFileName"/> arguments are load-bearing: the table reader picks the
    /// XLSX or the CSV path purely from the file extension.
    ///
    /// Either template stream may be null (that template was not uploaded); the caller is responsible
    /// for rejecting the case where both are missing.
    /// </summary>
    public static ReturnSlaData BuildData(
        Stream ordersStream, string ordersFileName,
        Stream? templateAStream, string? templateAFileName,
        Stream? templateBStream, string? templateBFileName)
    {
        var lines = ReadOrders(ordersStream, ordersFileName);
        if (lines.Count == 0)
            throw new InvalidOperationException("No order rows were found in the uploaded orders file.");

        var ordersByCore = IndexOrders(lines);

        // A template that was not uploaded contributes no rows; the caller guarantees at least one.
        List<ReturnCandidate> candidatesA = templateAStream is null
            ? []
            : ReadTemplateA(templateAStream, templateAFileName!);
        List<ReturnCandidate> candidatesB = templateBStream is null
            ? []
            : ReadTemplateB(templateBStream, templateBFileName!);

        var today = DateTime.Now;
        var rows = new List<ReturnSlaRow>();

        foreach (var candidate in candidatesA.Concat(candidatesB))
        {
            var resolution = Resolve(candidate, ordersByCore);

            // Only a resolved order can be late: without a status there is nothing to be late
            // against, and reporting those as breaches is what made every old row look overdue.
            var resolved = resolution.State is MatchedState or MatchedByStatusState;

            var elapsedDays = candidate.ShippedToSellerDate.HasValue
                ? (today - candidate.ShippedToSellerDate.Value).TotalDays
                : (double?)null;

            var open = resolved && !resolution.IsConfirmedReturn && elapsedDays.HasValue;
            var slaMissed = open && elapsedDays!.Value > SlaDays;

            // Gated on the return still being open, unlike the original: a return that is already
            // completed is not "at risk at 18 days", and before the order match was fixed nothing
            // was ever completed, so the difference never showed.
            var pastWarning = open && elapsedDays!.Value > WarningDays && elapsedDays.Value <= SlaDays;

            rows.Add(new ReturnSlaRow(
                Source: candidate.Source,
                OrderNumber: resolution.OrderNumber,
                SourceOrderNumber: candidate.SourceOrderNo,
                MatchState: resolution.State,
                MatchCount: resolution.MatchCount,
                Seller: resolution.Seller,
                Status: resolution.StatusText,
                ShippedToSellerDate: candidate.ShippedToSellerDate?.ToString("yyyy-MM-dd"),
                ElapsedDays: elapsedDays.HasValue ? Math.Round(elapsedDays.Value, 1) : (double?)null,
                SlaDays: SlaDays,
                WarningDays: WarningDays,
                IsConfirmedReturn: resolution.IsConfirmedReturn,
                SlaMissed: slaMissed,
                PastWarning: pastWarning,
                Reason: candidate.ReasonOrDetail));
        }

        // Refund time for canceled / refunded / rejected orders, taken over the whole orders file
        // rather than only the orders present in the return templates. Computed per order *line*,
        // which is the level the amount and the debit date live at.
        var paymentRows = lines
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

    // ---------------------------------------------------------------------
    // Matching
    // ---------------------------------------------------------------------

    /// <summary>
    /// Folds the order lines into one entry per full order number, indexed by the bare customer
    /// order number the templates carry.
    /// </summary>
    static Dictionary<string, List<OrderRef>> IndexOrders(List<OrderLine> lines)
    {
        var byFullNumber = new Dictionary<string, List<OrderLine>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.OrderNumberRaw.Length == 0)
                continue;
            if (!byFullNumber.TryGetValue(line.OrderNumberRaw, out var group))
                byFullNumber[line.OrderNumberRaw] = group = [];
            group.Add(line);
        }

        var index = new Dictionary<string, List<OrderRef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fullNumber, group) in byFullNumber)
        {
            var core = TabularFile.OrderCore(fullNumber);
            if (core.Length == 0)
                continue;

            var statuses = group
                .Select(l => l.Status)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var order = new OrderRef(
                FullNumber: fullNumber,
                Core: core,
                SellerId: group[0].SellerId,
                Seller: group[0].Seller,
                Statuses: statuses,
                IsConfirmedReturn: statuses.Any(IsConfirmedReturn));

            if (!index.TryGetValue(core, out var refs))
                index[core] = refs = [];
            refs.Add(order);
        }

        return index;
    }

    static Resolution Resolve(ReturnCandidate candidate, Dictionary<string, List<OrderRef>> ordersByCore)
    {
        var display = candidate.SourceOrderNo;

        if (candidate.Core.Length == 0 || !ordersByCore.TryGetValue(candidate.Core, out var matches) || matches.Count == 0)
            return new Resolution(NotFoundState, null, 0, false, UnmatchedStatus, display, "-");

        // Template B already knows the full form; the orders file only has to confirm it.
        if (candidate.FullOrderNumber is { Length: > 0 } fromTemplate)
        {
            var confirmed = matches.FirstOrDefault(m =>
                string.Equals(m.FullNumber, fromTemplate, StringComparison.OrdinalIgnoreCase));
            if (confirmed is not null)
                return Matched(confirmed, matches.Count);
        }

        if (matches.Count == 1)
            return Matched(matches[0], 1);

        // One customer order splits per seller into …-A / …-B, so the seller is what makes the choice
        // safe: searching the bare number by hand simply returns several hits.
        if (candidate.SellerId.Length > 0)
        {
            var bySeller = matches
                .Where(m => m.SellerId.Length > 0 &&
                            m.SellerId.Equals(candidate.SellerId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (bySeller.Count == 1)
                return Matched(bySeller[0], matches.Count);
        }

        // Which seller it is stays unknown, but the verdict does not have to: when every candidate
        // agrees on whether the return completed, the answer is the same whichever one it was.
        if (matches.Select(m => m.IsConfirmedReturn).Distinct().Count() == 1)
        {
            var verdict = matches[0].IsConfirmedReturn;
            var statuses = matches
                .SelectMany(m => m.Statuses)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            return new Resolution(
                MatchedByStatusState, null, matches.Count, verdict,
                string.Join(" / ", statuses),
                string.Join(" / ", matches.Select(m => m.FullNumber)),
                Sellers(matches));
        }

        return new Resolution(
            AmbiguousState, null, matches.Count, false, AmbiguousStatus,
            string.Join(" / ", matches.Select(m => m.FullNumber)),
            Sellers(matches));

        static Resolution Matched(OrderRef order, int matchCount) => new(
            MatchedState, order, matchCount, order.IsConfirmedReturn,
            order.StatusText, order.FullNumber, order.Seller.Length > 0 ? order.Seller : "-");

        static string Sellers(List<OrderRef> matches) =>
            string.Join(" / ", matches.Select(m => m.Seller).Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)) is { Length: > 0 } joined ? joined : "-";
    }

    static bool IsConfirmedReturn(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var s = status.ToLowerInvariant();
        return ConfirmedReturnKeywords.Any(s.Contains);
    }

    // ---------------------------------------------------------------------
    // Orders file parsing
    // ---------------------------------------------------------------------

    static List<OrderLine> ReadOrders(Stream stream, string fileName)
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

        int? Opt(params string[] names)
        {
            foreach (var n in names)
                if (idx.TryGetValue(n, out var i))
                    return i;
            return null;
        }

        var cOrderNumber = Col("Order number");
        var cStatus = Col("Status");
        var cDateCreated = Col("Date created");
        var cDebitDate = Col("Customer debit date");
        var cSeller = Col("Seller");
        var cAmount = Col("Amount");
        var cCurrency = Col("Currency");
        var cSellerId = Opt("Seller ID");

        var result = new List<OrderLine>();
        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];
            var orderNumberRaw = GetCell(row, cOrderNumber).Trim();
            if (string.IsNullOrWhiteSpace(orderNumberRaw))
                continue;

            result.Add(new OrderLine(
                OrderNumberRaw: orderNumberRaw,
                Status: GetCell(row, cStatus).Trim(),
                DateCreated: ParseDate(GetCell(row, cDateCreated)),
                CustomerDebitDate: ParseDate(GetCell(row, cDebitDate)),
                Seller: GetCell(row, cSeller).Trim(),
                SellerId: TabularFile.NormalizeSellerId(GetCell(row, cSellerId)),
                Amount: ParseNumber(GetCell(row, cAmount)),
                Currency: GetCell(row, cCurrency).Trim()));
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Return template A: "Marketplace Iade & Degisim Talepleri" — a real "Kargo Takip Kodu" marks
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
        var cSellerId = ColOrNull("Satıcı Id", "Satici Id");

        var result = new List<ReturnCandidate>();
        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];

            // Only rows actually shipped back to the seller count, and "NULL" is not a code.
            var (trackingState, _) = TabularFile.ReadTracking(GetCell(row, cTrackingCode));
            if (trackingState == TabularFile.TrackingState.Missing)
                continue;

            var orderNoRaw = GetCell(row, cOrderNo).Trim();
            if (string.IsNullOrWhiteSpace(orderNoRaw))
                continue;

            result.Add(new ReturnCandidate(
                Source: "Marketplace Return Requests",
                SourceOrderNo: orderNoRaw,
                Core: TabularFile.OrderCore(orderNoRaw),
                FullOrderNumber: null,
                SellerId: TabularFile.NormalizeSellerId(GetCell(row, cSellerId)),
                // Day-first: this template writes 12.08.2026 for 12 August.
                ShippedToSellerDate: cRequestDate.HasValue
                    ? TabularFile.ParseDayFirstDate(GetCell(row, cRequestDate.Value))
                    : null,
                ReasonOrDetail: cReason.HasValue ? GetCell(row, cReason.Value).Trim() : ""));
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Return template B: "NNNNNN-MP.csv" — a real "YK Takip Kodu" marks a row as shipped to the
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

            var (trackingState, _) = TabularFile.ReadTracking(GetCell(row, cTrackingCode));
            if (trackingState == TabularFile.TrackingState.Missing)
                continue;

            var orderNoRaw = GetCell(row, cOrderNo).Trim();
            var marketPlaceId = cMarketPlaceId.HasValue ? GetCell(row, cMarketPlaceId.Value).Trim() : "";
            if (string.IsNullOrWhiteSpace(orderNoRaw) && string.IsNullOrWhiteSpace(marketPlaceId))
                continue;

            // The marketplace id is the full order number; the bare CustomerOrderNumber is the key
            // to look it up by when it is missing.
            var core = TabularFile.OrderCore(
                string.IsNullOrWhiteSpace(orderNoRaw) ? marketPlaceId : orderNoRaw);

            result.Add(new ReturnCandidate(
                Source: "300726-MP",
                SourceOrderNo: string.IsNullOrWhiteSpace(orderNoRaw) ? marketPlaceId : orderNoRaw,
                Core: core,
                FullOrderNumber: marketPlaceId.Length > 0 ? marketPlaceId : null,
                SellerId: "",
                ShippedToSellerDate: cShipDate.HasValue
                    ? TabularFile.ParseDayFirstDate(GetCell(row, cShipDate.Value))
                    : null,
                ReasonOrDetail: cState.HasValue ? GetCell(row, cState.Value).Trim() : ""));
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Generic helpers
    // ---------------------------------------------------------------------

    // The file readers below live in TabularFile, shared with ReturnListBuilder. They are forwarded
    // rather than inlined at the call sites so this report keeps running on the same code.

    static Dictionary<string, int> BuildHeaderIndex(List<string> header) => TabularFile.BuildHeaderIndex(header);

    static string GetCell(List<string> row, int? col) => TabularFile.GetCell(row, col);

    static string GetCell(List<string> row, int col) => TabularFile.GetCell(row, col);

    static List<List<string>> ReadTable(Stream stream, string fileName) => TabularFile.Read(stream, fileName);

    static DateTime? ParseDate(string text) => TabularFile.ParseDate(text);

    static double ParseNumber(string text) => TabularFile.ParseNumber(text);
}
