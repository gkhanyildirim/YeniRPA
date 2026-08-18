using System.Globalization;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Builds the two-column list the Create Return automation runs on, from the four exports the
/// operator used to combine by hand.
///
/// Inputs:
///  - Return template A ("Marketplace Iade &amp; Degisim Talepleri") — candidates, order number bare.
///  - Return template B ("...MP") — candidates, order number bare plus the full form in MarketPlaceId.
///  - returns export — orders that already have a return; they must not get a second one.
///  - orders export — the only place the full <c>01259_311911494-A</c> form can be looked up.
///
/// NOTE: the Turkish column names are what the source exports actually contain. They are data, not
/// UI text, and must never be translated.
/// </summary>
public static class ReturnListBuilder
{
    public const string TemplateASource = "Marketplace";
    public const string TemplateBSource = "MP";

    // Exclusion reasons, shown verbatim in the review table.
    const string ReasonMalformedTracking = "Tracking code is not numeric";
    const string ReasonDuplicate = "Duplicate order number in the template";
    const string ReasonBothTemplates = "Same order in both templates";
    const string ReasonNotAReturn = "Request type is not İade";
    const string ReasonAlreadyReturned = "Return already exists";
    const string ReasonNotInOrders = "Not found in the orders export";
    const string ReasonAmbiguous = "Ambiguous order number (…-A / …-B)";

    /// <summary>One order line of the orders export, reduced to what the lookup needs.</summary>
    sealed record OrderRef(string FullNumber, string SellerId, string Seller);

    /// <summary>A template row that survived the tracking-code and date filters.</summary>
    sealed record Candidate(
        string Source,
        string SourceOrderNo,
        string Core,
        /// <summary>Template B carries the full order number itself; template A has to look it up.</summary>
        string? FullOrderNumber,
        string TrackingNumber,
        string Seller,
        string SellerId,
        string TypeOrState,
        DateTime? Date,
        bool IsReturnRequest);

    /// <summary>What one template contributed, plus the counts behind its funnel.</summary>
    sealed record Scan(
        string Source,
        int Total,
        int WithTracking,
        List<Candidate> Candidates,
        List<ReturnListExcludedRow> Excluded);

    /// <summary>
    /// The file names are load-bearing — <see cref="TabularFile.Read"/> picks the XLSX or the CSV
    /// reader from the extension.
    /// </summary>
    public static ReturnListData Build(
        Stream templateAStream, string templateAFileName,
        Stream templateBStream, string templateBFileName,
        Stream returnsStream, string returnsFileName,
        Stream ordersStream, string ordersFileName,
        ReturnListOptions options)
    {
        var scanA = ScanTemplateA(templateAStream, templateAFileName, options);
        var scanB = ScanTemplateB(templateBStream, templateBFileName, options);

        var alreadyReturned = ReadExistingReturns(returnsStream, returnsFileName);
        var orders = ReadOrders(ordersStream, ordersFileName);
        if (orders.Count == 0)
            throw new InvalidOperationException("No order rows were found in the uploaded orders file.");

        var excluded = new List<ReturnListExcludedRow>();
        excluded.AddRange(scanA.Excluded);
        excluded.AddRange(scanB.Excluded);

        // Per-template narrowing: duplicates, request type, and returns that already exist.
        var narrowedA = Narrow(scanA, options, alreadyReturned, excluded, out var afterDupA, out var afterTypeA);
        var narrowedB = Narrow(scanB, options, alreadyReturned, excluded, out var afterDupB, out var afterTypeB);

        // The same order turning up in both templates would file two returns for it. Not seen on the
        // current exports, but it was before the "NULL" tracking codes were excluded, so the guard
        // stays — and it drops both copies, exactly like the within-template rule.
        var shared = narrowedA.Select(c => c.Core)
            .Intersect(narrowedB.Select(c => c.Core), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (shared.Count > 0)
        {
            foreach (var candidate in narrowedA.Concat(narrowedB).Where(c => shared.Contains(c.Core)))
                excluded.Add(Exclude(candidate, ReasonBothTemplates));

            narrowedA = [.. narrowedA.Where(c => !shared.Contains(c.Core))];
            narrowedB = [.. narrowedB.Where(c => !shared.Contains(c.Core))];
        }

        var rowsA = Resolve(narrowedA, orders, excluded);
        var rowsB = Resolve(narrowedB, orders, excluded);

        var funnels = new List<ReturnListFunnel>
        {
            new(scanA.Source, scanA.Total, scanA.WithTracking, scanA.Candidates.Count, afterDupA, afterTypeA, narrowedA.Count, rowsA.Count),
            new(scanB.Source, scanB.Total, scanB.WithTracking, scanB.Candidates.Count, afterDupB, afterTypeB, narrowedB.Count, rowsB.Count)
        };

        return new ReturnListData([.. rowsA, .. rowsB], excluded, funnels);
    }

    // ---------------------------------------------------------------------
    // Shared narrowing
    // ---------------------------------------------------------------------

    static List<Candidate> Narrow(
        Scan scan,
        ReturnListOptions options,
        HashSet<string> alreadyReturned,
        List<ReturnListExcludedRow> excluded,
        out int afterDuplicates,
        out int afterRequestType)
    {
        // An order number appearing more than once is dropped entirely — no copy is kept. Two return
        // requests against one order cannot both be right, and picking one silently is worse than
        // leaving it to be checked by hand.
        var remaining = new List<Candidate>();
        foreach (var group in scan.Candidates.GroupBy(c => c.Core, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1)
            {
                remaining.Add(group.First());
                continue;
            }

            foreach (var candidate in group)
                excluded.Add(Exclude(candidate, ReasonDuplicate));
        }
        afterDuplicates = remaining.Count;

        if (options.ReturnsOnly)
        {
            // Keeping only what is positively an İade means an unfamiliar new request type shows up
            // in the review table instead of quietly becoming a return.
            var returnsOnly = new List<Candidate>();
            foreach (var candidate in remaining)
            {
                if (candidate.IsReturnRequest) returnsOnly.Add(candidate);
                else excluded.Add(Exclude(candidate, ReasonNotAReturn));
            }
            remaining = returnsOnly;
        }
        afterRequestType = remaining.Count;

        var withoutExisting = new List<Candidate>();
        foreach (var candidate in remaining)
        {
            if (alreadyReturned.Contains(candidate.Core)) excluded.Add(Exclude(candidate, ReasonAlreadyReturned));
            else withoutExisting.Add(candidate);
        }

        return withoutExisting;
    }

    /// <summary>Attaches the full Mirakl order number, which is the one thing the automation needs.</summary>
    static List<ReturnListRow> Resolve(
        List<Candidate> candidates,
        Dictionary<string, List<OrderRef>> orders,
        List<ReturnListExcludedRow> excluded)
    {
        var rows = new List<ReturnListRow>();

        foreach (var candidate in candidates)
        {
            if (!orders.TryGetValue(candidate.Core, out var matches) || matches.Count == 0)
            {
                excluded.Add(Exclude(candidate, ReasonNotInOrders));
                continue;
            }

            string fullOrderNumber;

            if (candidate.FullOrderNumber is { Length: > 0 } fromTemplate)
            {
                // Template B already knows the full form; the orders file only has to confirm it.
                var confirmed = matches.FirstOrDefault(m =>
                    string.Equals(m.FullNumber, fromTemplate, StringComparison.OrdinalIgnoreCase));

                if (confirmed is null)
                {
                    excluded.Add(Exclude(candidate, ReasonNotInOrders));
                    continue;
                }
                fullOrderNumber = confirmed.FullNumber;
            }
            else if (matches.Count == 1)
            {
                fullOrderNumber = matches[0].FullNumber;
            }
            else
            {
                // One customer order splits per seller into -A, -B, -C. On the current export 1001 of
                // 31854 order numbers do this, so the seller is what makes the choice safe: searching
                // the bare number by hand simply returns several hits.
                var bySeller = matches
                    .Where(m => m.SellerId.Length > 0 && m.SellerId.Equals(candidate.SellerId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (bySeller.Count != 1)
                {
                    excluded.Add(Exclude(candidate, ReasonAmbiguous));
                    continue;
                }
                fullOrderNumber = bySeller[0].FullNumber;
            }

            rows.Add(new ReturnListRow(
                Source: candidate.Source,
                OrderNumber: fullOrderNumber,
                TrackingNumber: candidate.TrackingNumber,
                SourceOrderNo: candidate.SourceOrderNo,
                Seller: candidate.Seller,
                SellerId: candidate.SellerId,
                TypeOrState: candidate.TypeOrState,
                RequestDate: FormatDate(candidate.Date)));
        }

        return rows;
    }

    // ---------------------------------------------------------------------
    // Template A — "Marketplace Iade & Degisim Talepleri"
    // ---------------------------------------------------------------------

    static Scan ScanTemplateA(Stream stream, string fileName, ReturnListOptions options)
    {
        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("The uploaded return template A file is empty.");

        var idx = TabularFile.BuildHeaderIndex(table[0]);

        var cOrderNo = Required(idx, "return template A", "SiparişNo", "SiparisNo");
        var cTracking = Required(idx, "return template A", "Kargo Takip Kodu");
        var cSellerId = Optional(idx, "Satıcı Id", "Satici Id");
        var cSeller = Optional(idx, "Satıcı Adı", "Satici Adi");
        var cType = Optional(idx, "Talep Tipi");
        var cDate = Optional(idx, "Talep Tarihi");

        var candidates = new List<Candidate>();
        var excluded = new List<ReturnListExcludedRow>();
        var total = 0;
        var withTracking = 0;

        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];
            var orderNo = TabularFile.GetCell(row, cOrderNo).Trim();
            if (orderNo.Length == 0)
                continue;

            total++;

            var seller = TabularFile.GetCell(row, cSeller).Trim();
            var sellerId = NormalizeSellerId(TabularFile.GetCell(row, cSellerId));
            var type = TabularFile.GetCell(row, cType).Trim();
            var date = ParseTemplateDate(TabularFile.GetCell(row, cDate));

            var (state, code) = ReadTracking(TabularFile.GetCell(row, cTracking));
            if (state == TrackingState.Missing)
                continue;

            withTracking++;

            if (state == TrackingState.Malformed)
            {
                excluded.Add(new ReturnListExcludedRow(
                    TemplateASource, orderNo, code, seller, type, FormatDate(date), ReasonMalformedTracking));
                continue;
            }

            if (!InRange(date, options))
                continue;

            candidates.Add(new Candidate(
                Source: TemplateASource,
                SourceOrderNo: orderNo,
                Core: Core(orderNo),
                FullOrderNumber: null,
                TrackingNumber: code,
                Seller: seller,
                SellerId: sellerId,
                TypeOrState: type,
                Date: date,
                IsReturnRequest: IsIade(type)));
        }

        return new Scan(TemplateASource, total, withTracking, candidates, excluded);
    }

    // ---------------------------------------------------------------------
    // Template B — "...MP"
    // ---------------------------------------------------------------------

    static Scan ScanTemplateB(Stream stream, string fileName, ReturnListOptions options)
    {
        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("The uploaded return template B (MP) file is empty.");

        var idx = TabularFile.BuildHeaderIndex(table[0]);

        var cOrderNo = Required(idx, "return template B (MP)", "CustomerOrderNumber");
        var cTracking = Required(idx, "return template B (MP)", "YK Takip Kodu");
        var cMarketPlaceId = Optional(idx, "MarketPlaceId");
        var cSeller = Optional(idx, "SellerName");
        var cState = Optional(idx, "State");
        var cDate = Optional(idx, "Kargo Kodu Oluşturma Tarihi", "Kargo Kodu Olusturma Tarihi");

        var candidates = new List<Candidate>();
        var excluded = new List<ReturnListExcludedRow>();
        var total = 0;
        var withTracking = 0;

        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];
            var orderNo = TabularFile.GetCell(row, cOrderNo).Trim();
            if (orderNo.Length == 0)
                continue;

            total++;

            var seller = TabularFile.GetCell(row, cSeller).Trim();
            var state = TabularFile.GetCell(row, cState).Trim();
            var date = ParseTemplateDate(TabularFile.GetCell(row, cDate));
            var marketPlaceId = TabularFile.GetCell(row, cMarketPlaceId).Trim();

            var (trackingState, code) = ReadTracking(TabularFile.GetCell(row, cTracking));
            if (trackingState == TrackingState.Missing)
                continue;

            withTracking++;

            if (trackingState == TrackingState.Malformed)
            {
                excluded.Add(new ReturnListExcludedRow(
                    TemplateBSource, orderNo, code, seller, state, FormatDate(date), ReasonMalformedTracking));
                continue;
            }

            if (!InRange(date, options))
                continue;

            candidates.Add(new Candidate(
                Source: TemplateBSource,
                SourceOrderNo: orderNo,
                Core: Core(orderNo),
                FullOrderNumber: marketPlaceId.Length > 0 ? marketPlaceId : null,
                TrackingNumber: code,
                Seller: seller,
                SellerId: "",
                TypeOrState: state,
                Date: date,
                // No request-type column on this template, so the "only İade" filter cannot drop it.
                IsReturnRequest: true));
        }

        return new Scan(TemplateBSource, total, withTracking, candidates, excluded);
    }

    // ---------------------------------------------------------------------
    // Supporting files
    // ---------------------------------------------------------------------

    /// <summary>Order numbers that already carry a return, keyed the same way as the candidates.</summary>
    static HashSet<string> ReadExistingReturns(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (table.Count == 0)
            return result;

        var idx = TabularFile.BuildHeaderIndex(table[0]);
        var cOrderNumber = Required(idx, "returns", "Order number");

        for (var r = 1; r < table.Count; r++)
        {
            var core = Core(TabularFile.GetCell(table[r], cOrderNumber));
            if (core.Length > 0)
                result.Add(core);
        }

        return result;
    }

    static Dictionary<string, List<OrderRef>> ReadOrders(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName);
        var index = new Dictionary<string, List<OrderRef>>(StringComparer.OrdinalIgnoreCase);
        if (table.Count == 0)
            return index;

        var idx = TabularFile.BuildHeaderIndex(table[0]);
        var cOrderNumber = Required(idx, "orders", "Order number");
        var cSellerId = Optional(idx, "Seller ID");
        var cSeller = Optional(idx, "Seller");

        // The export is one row per order line, so a single order number repeats once per item.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];
            var full = TabularFile.GetCell(row, cOrderNumber).Trim();
            if (full.Length == 0 || !seen.Add(full))
                continue;

            var core = Core(full);
            if (core.Length == 0)
                continue;

            if (!index.TryGetValue(core, out var refs))
                index[core] = refs = [];

            refs.Add(new OrderRef(
                full,
                NormalizeSellerId(TabularFile.GetCell(row, cSellerId)),
                TabularFile.GetCell(row, cSeller).Trim()));
        }

        return index;
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    enum TrackingState { Missing, Malformed, Ok }

    /// <summary>
    /// The MP export writes the literal text <c>NULL</c> into the tracking column on three rows out
    /// of four. Treating "not empty" as "has a code" would file the word NULL as a tracking number,
    /// so the placeholder counts as missing. Every real code on both templates is digits only;
    /// anything else is surfaced for review rather than sent to the marketplace.
    /// </summary>
    static (TrackingState State, string Code) ReadTracking(string raw)
    {
        var code = raw.Trim();

        if (code.Length == 0 || code.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return (TrackingState.Missing, "");

        return code.All(char.IsAsciiDigit)
            ? (TrackingState.Ok, code)
            : (TrackingState.Malformed, code);
    }

    // The two below moved to TabularFile when TicketSellerBuilder needed the same order-number key.
    // They are forwarded rather than inlined at the call sites so this module keeps running on
    // byte-identical code, the same way ReturnSlaReportBuilder forwards its file readers.

    static string Core(string orderNumber) => TabularFile.OrderCore(orderNumber);

    static string NormalizeSellerId(string raw) => TabularFile.NormalizeSellerId(raw);

    /// <summary>
    /// Only "İade" counts as a return request. The template also carries "Değişim" (exchange), and
    /// matching positively means a request type nobody has seen before is reviewed, not filed.
    /// </summary>
    static bool IsIade(string requestType) =>
        requestType.Trim() is "İade" or "Iade" or "iade" or "İADE" or "IADE";

    /// <summary>
    /// Both templates write the day first, so the explicit formats have to be tried before anything
    /// lenient. <see cref="TabularFile.ParseDate"/> leads with
    /// <c>DateTime.TryParse(…, InvariantCulture)</c>, which is month-first and accepts '.' as a
    /// separator — it reads "12.08.2026" as 8 December and "07.08.2026" as 8 July. Every date whose
    /// day and month are both 12 or under comes out transposed, which here would silently move rows
    /// in and out of the operator's date range.
    ///
    /// This does not correct <see cref="TabularFile.ParseDate"/> itself: the Return SLA report has
    /// always run on it and its published figures would shift. That is a real bug in that report and
    /// is worth fixing on its own terms, not as a side effect of this module.
    /// </summary>
    static DateTime? ParseTemplateDate(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;

        string[] dayFirstFormats =
        [
            "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy",
            "d.M.yyyy HH:mm", "d.M.yyyy",
            "yyyy-MM-dd HH:mm:ss.fffffff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd",
        ];

        if (DateTime.TryParseExact(text, dayFirstFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        // An unexpected shape is better read leniently than dropped; only the ambiguous day/month
        // case above needed pinning down.
        return TabularFile.ParseDate(text);
    }

    static bool InRange(DateTime? date, ReturnListOptions options)
    {
        if (date is null)
            return false;

        if (options.From.HasValue && date.Value < options.From.Value.Date)
            return false;

        // The picker sends a plain date, so "to" has to cover the whole of that day.
        return !options.To.HasValue || date.Value < options.To.Value.Date.AddDays(1);
    }

    static string? FormatDate(DateTime? date) => date?.ToString("yyyy-MM-dd HH:mm");

    static ReturnListExcludedRow Exclude(Candidate candidate, string reason) => new(
        candidate.Source,
        candidate.SourceOrderNo,
        candidate.TrackingNumber,
        candidate.Seller,
        candidate.TypeOrState,
        FormatDate(candidate.Date),
        reason);

    static int Required(Dictionary<string, int> idx, string file, params string[] names)
    {
        foreach (var name in names)
            if (idx.TryGetValue(name, out var i))
                return i;

        throw new InvalidOperationException($"Required column '{names[0]}' was not found in the uploaded {file} file.");
    }

    static int? Optional(Dictionary<string, int> idx, params string[] names)
    {
        foreach (var name in names)
            if (idx.TryGetValue(name, out var i))
                return i;

        return null;
    }
}
