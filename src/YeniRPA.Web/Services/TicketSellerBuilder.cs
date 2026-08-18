using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Answers "which seller is this case about?" for a list of Oracle support tickets.
///
/// The ticket export carries a customer order number but no seller; the Mirakl orders export carries
/// both. The two are matched on <see cref="TabularFile.OrderCore"/>, i.e. the bare customer order
/// number with the marketplace prefix and the per-seller suffix stripped. One customer order splits
/// per seller into <c>…-A</c> / <c>…-B</c>, so a ticket can legitimately belong to several sellers at
/// once — those are all reported rather than resolved down to one, because the operator has to decide
/// which seller the complaint actually concerns.
///
/// Only cases sitting in an HQ queue are reported; see <see cref="IsHqQueue"/>.
///
/// NOTE: the column names read from the ticket file are Turkish because that is what the Oracle export
/// actually contains. They are data, not UI text, and must never be translated.
/// </summary>
public static class TicketSellerBuilder
{
    /// <summary>
    /// Oracle writes this literal into every cell it has no value for, rather than leaving it empty.
    /// Treating it as text would put "Değer Yok" on screen as a subject and as an order number.
    /// </summary>
    const string OraclePlaceholder = "Değer Yok";

    /// <summary>
    /// The word that marks a queue as belonging to HQ, e.g. "TR MM HQ MediaMarkt Pazaryeri" and
    /// "TR MM HQ Customer Care". Matched as a whole word rather than as a substring so a queue that
    /// merely contains the letters cannot slip in.
    /// </summary>
    public const string HqQueueWord = "HQ";

    /// <summary>One order line of the orders export, reduced to what the lookup needs.</summary>
    sealed record OrderRef(string FullNumber, string Seller, string SellerId);

    public static TicketSellerData BuildData(
        Stream ticketsStream, string ticketsFileName,
        Stream ordersStream, string ordersFileName)
    {
        var orders = ReadOrders(ordersStream, ordersFileName, out var orderLineCount);
        var allTickets = ReadTickets(ticketsStream, ticketsFileName);

        if (allTickets.Count == 0)
            throw new InvalidOperationException("No ticket rows were found in the uploaded case list.");

        // Everything downstream — rows, counts, filters — is HQ only. The cases in the other queues
        // are dropped here rather than hidden in the browser so they never reach the export either.
        var tickets = allTickets.Where(t => IsHqQueue(t.Queue)).ToList();
        var otherQueueCount = allTickets.Count - tickets.Count;

        var rows = new List<TicketSellerRow>();

        foreach (var ticket in tickets)
        {
            var core = TabularFile.OrderCore(ticket.OrderNo);

            if (!IsUsableOrderNumber(core))
            {
                rows.Add(Row(ticket, ticket.OrderNo, "", "", TicketSellerMatchState.NoOrderNumber, 1));
                continue;
            }

            if (!orders.TryGetValue(core, out var matches) || matches.Count == 0)
            {
                rows.Add(Row(ticket, ticket.OrderNo, "", "", TicketSellerMatchState.NotFound, 1));
                continue;
            }

            foreach (var match in matches)
            {
                rows.Add(Row(
                    ticket, match.FullNumber, match.Seller, match.SellerId,
                    TicketSellerMatchState.Matched, matches.Count));
            }
        }

        return new TicketSellerData(rows, tickets.Count, orderLineCount, otherQueueCount);
    }

    /// <summary>
    /// True when "Kuyruk" names an HQ queue. The export writes the queue as space-separated words
    /// ("TR MM HQ Customer Care"), so the check is on a whole word: a queue with no value at all, or
    /// one belonging to the call centre or an outbound team, is not HQ.
    /// </summary>
    static bool IsHqQueue(string queue)
        => queue.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.Equals(HqQueueWord, StringComparison.OrdinalIgnoreCase));

    static TicketSellerRow Row(Ticket ticket, string orderNumber, string seller, string sellerId, string state, int matchCount)
        => new(
            TicketNo: ticket.TicketNo,
            SourceOrderNo: ticket.OrderNo,
            OrderNumber: orderNumber,
            Subject: ticket.Subject,
            Seller: seller,
            SellerId: sellerId,
            Queue: ticket.Queue,
            MatchState: state,
            MatchCount: matchCount);

    /// <summary>
    /// A ticket with no order attached is exported with the placeholder text or with a bare "0", so a
    /// number is only worth looking up once it has a non-zero digit in it.
    /// </summary>
    static bool IsUsableOrderNumber(string core)
        => core.Length > 0 && core.Any(char.IsDigit) && core.Any(c => char.IsDigit(c) && c != '0');

    // ---------------------------------------------------------------------
    // Ticket export
    // ---------------------------------------------------------------------

    sealed record Ticket(string TicketNo, string OrderNo, string Subject, string Queue);

    static List<Ticket> ReadTickets(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("The uploaded case list is empty.");

        var idx = TabularFile.BuildHeaderIndex(table[0]);

        int? Optional(params string[] names)
        {
            foreach (var name in names)
                if (idx.TryGetValue(name, out var i))
                    return i;
            return null;
        }

        var cTicketNo = Optional("Referans No")
            ?? throw new InvalidOperationException("Required column 'Referans No' was not found in the uploaded case list.");

        // Both order columns are read: the export has a "Sipariş" column that is filled only when the
        // order came in through the integration, and a "Manuel Sipariş No" the agent types by hand.
        var cOrderNo = Optional("Sipariş");
        var cManualOrderNo = Optional("Manuel Sipariş No");
        var cSubject = Optional("Konu");
        var cQueue = Optional("Kuyruk");
        var categoryColumns = new[] { "Kategoriler(1)", "Kategoriler(2)", "Kategoriler(3)" }
            .Select(name => Optional(name))
            .Where(i => i.HasValue)
            .ToList();

        var tickets = new List<Ticket>();

        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];

            var ticketNo = Clean(TabularFile.GetCell(row, cTicketNo));
            if (ticketNo.Length == 0)
                continue;

            var orderNo = Clean(TabularFile.GetCell(row, cOrderNo));
            if (orderNo.Length == 0)
                orderNo = Clean(TabularFile.GetCell(row, cManualOrderNo));

            tickets.Add(new Ticket(
                TicketNo: ticketNo,
                OrderNo: orderNo,
                Subject: Subject(row, cSubject, categoryColumns),
                Queue: Clean(TabularFile.GetCell(row, cQueue))));
        }

        return tickets;
    }

    /// <summary>
    /// "Konu" is free text the agent may never have filled in — a quarter of the rows on the current
    /// export have none. The category columns underneath it are almost always populated, so the
    /// chain they form ("Sipariş &gt; Hasarlı Ürün &gt; Eksik/Hasarlı Ürün") stands in as the subject.
    /// </summary>
    static string Subject(List<string> row, int? subjectColumn, List<int?> categoryColumns)
    {
        var subject = Clean(TabularFile.GetCell(row, subjectColumn));
        if (subject.Length > 0)
            return subject;

        var parts = categoryColumns
            .Select(c => Clean(TabularFile.GetCell(row, c)))
            .Where(v => v.Length > 0);

        return string.Join(" > ", parts);
    }

    /// <summary>Trims the cell and folds Oracle's "no value" placeholder down to an empty string.</summary>
    static string Clean(string raw)
    {
        var value = raw.Trim();
        return value.Equals(OraclePlaceholder, StringComparison.OrdinalIgnoreCase) ? "" : value;
    }

    // ---------------------------------------------------------------------
    // Orders export
    // ---------------------------------------------------------------------

    /// <summary>
    /// Indexes the orders export by customer order number. The export is one row per order line, so a
    /// seller repeats once per item bought from them; the entries are deduplicated on
    /// (full number, seller) because the lookup reports who a case belongs to, not what was in it.
    /// </summary>
    static Dictionary<string, List<OrderRef>> ReadOrders(Stream stream, string fileName, out int lineCount)
    {
        var table = TabularFile.Read(stream, fileName);
        var index = new Dictionary<string, List<OrderRef>>(StringComparer.OrdinalIgnoreCase);
        lineCount = 0;

        if (table.Count == 0)
            throw new InvalidOperationException("The uploaded orders file is empty.");

        var idx = TabularFile.BuildHeaderIndex(table[0]);

        if (!idx.TryGetValue("Order number", out var cOrderNumber))
            throw new InvalidOperationException("Required column 'Order number' was not found in the uploaded orders file.");

        int? cSeller = idx.TryGetValue("Seller", out var seller) ? seller : null;
        int? cSellerId = idx.TryGetValue("Seller ID", out var sellerId) ? sellerId : null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];
            var full = TabularFile.GetCell(row, cOrderNumber).Trim();
            if (full.Length == 0)
                continue;

            lineCount++;

            var core = TabularFile.OrderCore(full);
            if (core.Length == 0)
                continue;

            var sellerName = TabularFile.GetCell(row, cSeller).Trim();
            if (!seen.Add(full + " " + sellerName))
                continue;

            if (!index.TryGetValue(core, out var refs))
                index[core] = refs = [];

            refs.Add(new OrderRef(
                full,
                sellerName,
                TabularFile.NormalizeSellerId(TabularFile.GetCell(row, cSellerId))));
        }

        if (lineCount == 0)
            throw new InvalidOperationException("No order rows were found in the uploaded orders file.");

        return index;
    }
}
