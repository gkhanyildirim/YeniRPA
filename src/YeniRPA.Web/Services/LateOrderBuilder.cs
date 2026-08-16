using System.Globalization;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Finds the orders that are <em>currently</em> overdue in a Mirakl orders export, groups them by
/// seller, and attaches each seller's WhatsApp group.
///
/// <para><b>This is a different question from the one <see cref="OrderReportBuilder"/> answers.</b>
/// That report is retrospective — it asks whether a shipped order went out after its deadline
/// (<c>Shipping date &gt; Shipping deadline</c>) and is used to rank sellers. This one is
/// prospective: not shipped at all, and the deadline has passed. An order can be late here and
/// invisible there, because it has no shipping date to compare yet. Do not merge the two rules.</para>
/// </summary>
public static class LateOrderBuilder
{
    /// <summary>
    /// Columns the module cannot run without. <c>Shipping date</c> is on this list even though every
    /// cell in it is empty on a typical overdue export: without the column we cannot tell "not
    /// shipped" from "we don't know", and the whole premise collapses into "message everyone".
    /// </summary>
    public static readonly string[] RequiredColumns =
    [
        "Seller", "Order number", "Status", "Shipping deadline", "Shipping date",
    ];

    public static readonly string[] OptionalColumns =
    [
        "Seller ID", "Date created", "Acceptance date", "Shipping company",
    ];

    /// <summary>
    /// Statuses the seller can still act on. Everything else is surfaced for review, never messaged.
    ///
    /// <para>An <b>allow-list, never a deny-list</b>. Mirakl statuses this app has not seen —
    /// <c>Shipping</c>, <c>To collect</c>, <c>Incident</c>, whatever the next platform release adds —
    /// must not default into "chase the seller". Same argument <c>ReturnListBuilder.IsIade</c> already
    /// wins: matching positively means something nobody has seen before is reviewed, not filed.</para>
    ///
    /// <para><c>Pending acceptance</c> is here on purpose. In the sample export both such rows carry a
    /// shipping deadline and no acceptance date, so the deadline clock does not wait for the seller to
    /// accept — an overdue <c>Pending acceptance</c> order is a seller who has failed at an even
    /// earlier step, and excluding it would quietly let the worst-behaved sellers out of the report.
    /// The corrective action differs, which is why the line template carries <c>{status}</c>.</para>
    /// </summary>
    public static readonly string[] ChaseableStatuses = ["Awaiting shipment", "Pending acceptance"];

    /// <summary>Most overdue orders one message lists before it is truncated.</summary>
    public const int MaxOrderLinesPerMessage = 60;

    /// <summary>Keeps a pathological file from producing a megabyte of review rows.</summary>
    const int MaxReviewRows = 500;

    const string DisplayFormat = "yyyy-MM-dd HH:mm";

    /// <summary>One export row that survived every filter.</summary>
    sealed record OverdueRow(
        string SellerKey,
        string SellerId,
        string SellerName,
        string OrderNumber,
        string Status,
        string DeadlineRaw,
        DateTime DeadlineEffective,
        double HoursLate,
        string? DateCreated,
        string? AcceptanceDate,
        string? ShippingCompany);

    /// <summary>The file name is load-bearing — <see cref="TabularFile.Read"/> picks the XLSX or the
    /// CSV reader from the extension.</summary>
    public static LateOrderData Build(Stream stream, string fileName, LateOrderOptions options, SellerGroupMap map)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(map);

        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("No rows were found in the uploaded file.");

        var header = TabularFile.BuildHeaderIndex(table[0]);

        int Col(string name)
        {
            if (header.TryGetValue(name, out var index))
                return index;
            throw new InvalidOperationException($"Required column '{name}' was not found in the uploaded file.");
        }

        int? Opt(string name) => header.TryGetValue(name, out var index) ? index : null;

        // Fail on the first missing required column before doing any work, so the operator gets one
        // actionable message instead of whichever lookup happened to run first.
        foreach (var required in RequiredColumns)
            Col(required);

        var cSeller = Col("Seller");
        var cOrderNumber = Col("Order number");
        var cStatus = Col("Status");
        var cShippingDeadline = Col("Shipping deadline");
        var cShippingDate = Col("Shipping date");

        var cSellerId = Opt("Seller ID");
        var cDateCreated = Opt("Date created");
        var cAcceptanceDate = Opt("Acceptance date");
        var cShippingCompany = Opt("Shipping company");

        // Captured once for the whole build. Per-row DateTime.Now would let a slow parse straddle a
        // minute boundary, and would let the preview and the message text disagree about "now".
        var referenceTime = DateTime.Now;

        var rowsInFile = 0;
        var alreadyShipped = 0;
        var statusNotChaseable = 0;
        var noDeadline = 0;
        var unreadableDeadline = 0;
        var notYetLate = 0;

        var overdue = new List<OverdueRow>();
        var review = new List<LateOrderReviewRow>();
        var skippedStatuses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Review(string orderNumber, string seller, string status, string deadlineRaw, string reason)
        {
            if (review.Count < MaxReviewRows)
                review.Add(new LateOrderReviewRow(orderNumber, seller, status, deadlineRaw, reason));
        }

        foreach (var row in table.Skip(1))
        {
            var orderNumber = TabularFile.GetCell(row, cOrderNumber).Trim();
            if (orderNumber.Length == 0)
                continue;

            rowsInFile++;

            var seller = TabularFile.GetCell(row, cSeller).Trim();
            var status = TabularFile.GetCell(row, cStatus).Trim();
            var deadlineRaw = TabularFile.GetCell(row, cShippingDeadline).Trim();

            // The raw text, not ParseDate: a shipping date we cannot parse is still a shipping date.
            // Telling a seller they have not shipped an order they shipped last Tuesday is the failure
            // that destroys this module's credibility, and being conservative here costs nothing.
            if (TabularFile.GetCell(row, cShippingDate).Trim().Length > 0)
            {
                alreadyShipped++;
                continue;
            }

            if (!ChaseableStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            {
                statusNotChaseable++;
                var key = status.Length == 0 ? "(blank)" : status;
                skippedStatuses[key] = skippedStatuses.GetValueOrDefault(key) + 1;
                Review(orderNumber, seller, status, deadlineRaw, $"Status '{key}' is not one the seller can act on");
                continue;
            }

            if (deadlineRaw.Length == 0)
            {
                noDeadline++;
                Review(orderNumber, seller, status, deadlineRaw, "No shipping deadline in the file");
                continue;
            }

            // TabularFile.ParseDate resolves "08/10/2026 09:59:59 PM" on its first branch (invariant,
            // month-first), which is exactly the format the Mirakl orders export writes and always has.
            // The README's dd.MM.yyyy transposition bug does not apply here, so do NOT reach for
            // ReturnListBuilder.ParseTemplateDate's day-first override — the return templates need it
            // and this module would be broken by it.
            var parsed = TabularFile.ParseDate(deadlineRaw);
            if (parsed is null)
            {
                // Never fall through to "assume late" — that would message a seller off a value we
                // could not read.
                unreadableDeadline++;
                Review(orderNumber, seller, status, deadlineRaw, "Shipping deadline could not be read as a date");
                continue;
            }

            var deadlineEffective = parsed.Value.AddHours(options.OffsetHours);
            if (deadlineEffective >= referenceTime)
            {
                notYetLate++;
                continue;
            }

            var sellerId = SellerGroupMap.NormalizeSellerId(TabularFile.GetCell(row, cSellerId));
            var sellerKey = sellerId.Length > 0 ? "id:" + sellerId : "name:" + SellerGroupMap.FoldName(seller);

            overdue.Add(new OverdueRow(
                SellerKey: sellerKey,
                SellerId: sellerId,
                SellerName: seller,
                OrderNumber: orderNumber,
                Status: status,
                DeadlineRaw: deadlineRaw,
                DeadlineEffective: deadlineEffective,
                HoursLate: (referenceTime - deadlineEffective).TotalHours,
                DateCreated: FormatOptionalDate(row, cDateCreated),
                AcceptanceDate: FormatOptionalDate(row, cAcceptanceDate),
                ShippingCompany: NullIfBlank(TabularFile.GetCell(row, cShippingCompany))));
        }

        var warnings = new List<string>();
        var sellers = GroupSellers(overdue, map, warnings);

        if (skippedStatuses.Count > 0)
        {
            var summary = string.Join(", ", skippedStatuses
                .OrderByDescending(pair => pair.Value)
                .Select(pair => $"{pair.Key} ({pair.Value:N0})"));
            warnings.Add($"Unshipped rows skipped because their status is not on the chaseable list: {summary}.");
        }

        warnings.AddRange(map.LoadWarnings);

        var funnel = new LateOrderFunnel(
            RowsInFile: rowsInFile,
            AlreadyShipped: alreadyShipped,
            StatusNotChaseable: statusNotChaseable,
            NoDeadline: noDeadline,
            UnreadableDeadline: unreadableDeadline,
            NotYetLate: notYetLate,
            OverdueRows: overdue.Count,
            OverdueOrders: sellers.Sum(s => s.OrderCount),
            Sellers: sellers.Count,
            MappedSellers: sellers.Count(s => s.GroupName is not null),
            UnmappedSellers: sellers.Count(s => s.GroupName is null));

        return new LateOrderData(
            ReferenceTime: referenceTime.ToString(DisplayFormat, CultureInfo.InvariantCulture),
            OffsetHours: options.OffsetHours,
            Sellers: sellers,
            Funnel: funnel,
            Review: review,
            Warnings: warnings);
    }

    // ---------------------------------------------------------------------

    static List<LateOrderSeller> GroupSellers(
        List<OverdueRow> overdue, SellerGroupMap map, List<string> warnings)
    {
        var result = new List<LateOrderSeller>();

        foreach (var group in overdue.GroupBy(r => r.SellerKey, StringComparer.Ordinal))
        {
            // The export is one row per order *line*. Collapse to distinct orders, keeping the most
            // late of the unshipped lines: if one line of a two-line order shipped and one did not,
            // the order is still incomplete and the unshipped line's deadline is the honest one.
            var orders = group
                .GroupBy(r => r.OrderNumber, StringComparer.OrdinalIgnoreCase)
                .Select(byOrder =>
                {
                    var worst = byOrder.MaxBy(r => r.HoursLate)!;
                    return ToLine(worst, byOrder.Count());
                })
                .OrderByDescending(line => line.HoursLate)
                .ToList();

            // One seller id can carry two spellings of the name inside a single export (a mid-period
            // rebrand). Group on the key and display the most frequent spelling — grouping on
            // (id, name) would split one seller into two messages to the same group.
            var names = group
                .Select(r => r.SellerName)
                .Where(n => n.Length > 0)
                .GroupBy(n => n, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            var displayName = names.FirstOrDefault()?.Key ?? "(no seller name)";
            if (names.Count > 1)
            {
                warnings.Add(
                    $"Seller '{displayName}' appears under more than one name in this export " +
                    $"({string.Join(", ", names.Select(n => $"'{n.Key}'"))}). They are treated as one seller.");
            }

            var sellerId = group.Select(r => r.SellerId).FirstOrDefault(id => id.Length > 0) ?? "";
            var match = map.Resolve(sellerId, displayName);

            result.Add(new LateOrderSeller(
                SellerId: sellerId,
                SellerName: displayName,
                GroupName: match.GroupName,
                MappingProblem: match.Problem,
                OrderCount: orders.Count,
                MaxDaysLate: orders.Count > 0 ? orders.Max(o => o.DaysLate) : 0,
                Orders: orders));
        }

        return [.. result
            .OrderByDescending(s => s.MaxDaysLate)
            .ThenByDescending(s => s.OrderCount)
            .ThenBy(s => s.SellerName, StringComparer.OrdinalIgnoreCase)];
    }

    static LateOrderLine ToLine(OverdueRow row, int lineCount) => new(
        OrderNumber: row.OrderNumber,
        Status: row.Status,
        DeadlineRaw: row.DeadlineRaw,
        DeadlineEffective: row.DeadlineEffective.ToString(DisplayFormat, CultureInfo.InvariantCulture),
        // Floor, never round up: announcing an order 40 minutes past its deadline as "1 day late" is
        // a number the seller can check and disprove, after which every figure from this channel is
        // suspect. HoursLate carries the detail so a sub-day delay can still be stated honestly.
        DaysLate: Math.Max(0, (int)Math.Floor(row.HoursLate / 24.0)),
        HoursLate: Math.Round(row.HoursLate, 1),
        DateCreated: row.DateCreated,
        AcceptanceDate: row.AcceptanceDate,
        ShippingCompany: row.ShippingCompany,
        LineCount: lineCount);

    /// <summary>
    /// Reformats an optional date column, falling back to the raw text when it cannot be parsed —
    /// these are display-only, so an odd value is better shown than blanked.
    /// </summary>
    static string? FormatOptionalDate(List<string> row, int? column)
    {
        var text = TabularFile.GetCell(row, column).Trim();
        if (text.Length == 0)
            return null;

        var parsed = TabularFile.ParseDate(text);
        return parsed?.ToString(DisplayFormat, CultureInfo.InvariantCulture) ?? text;
    }

    static string? NullIfBlank(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
