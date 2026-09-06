using System.Globalization;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Finds the incidents a seller has left unanswered too long, groups them by seller, and attaches each
/// seller's WhatsApp group.
///
/// <para><b>The clock is the incident's, not the order's.</b> Age is measured from <c>Opened on</c> —
/// when the complaint was raised — and never from <c>Order created on</c>. The two are unrelated: an
/// incident opened this morning against a three-month-old order is not something to chase, and one
/// opened three days ago against an order placed yesterday is. <see cref="IncidentsReportBuilder"/>
/// already reports the gap between them as <c>orderToIncidentDays</c>; that figure is not a chase
/// signal and nothing here reads it.</para>
///
/// <para><b>Only <c>open</c> incidents where the seller owes the reply are chased.</b> Both halves
/// matter and both come from rules this app already committed to elsewhere:</para>
/// <list type="bullet">
///   <item><description><c>resolved</c> means the seller has answered and written a closing reason,
///   and the verification and closure are ours — see <see cref="IncidentWaitingOn"/>. Messaging the
///   seller there warns the party that is not holding anything up, which is the inverse of the team's
///   real worklist and exactly the mistake that doc comment exists to prevent.</description></item>
///   <item><description>On an open incident, <c>waitingOn == seller</c> means the customer spoke last.
///   That is the only state in which "you have not replied" is a true statement.</description></item>
/// </list>
///
/// <para><b>This threshold is not the report's SLA threshold.</b>
/// <see cref="IncidentsReportBuilder.WarningDays"/> (7) and <see cref="IncidentsReportBuilder.BreachDays"/>
/// (14) are when an incident's *age* becomes a problem worth reporting; <see cref="DefaultThresholdDays"/>
/// is when it is worth a nudge. A two-day incident is deliberately still green on the dashboard while
/// being chaseable here, and neither number should be moved to make them agree.</para>
///
/// <para>Pure and IO-free, like <see cref="LateOrderBuilder"/>, so the rule above is testable without a
/// database or an upload.</para>
/// </summary>
public static class IncidentWarningBuilder
{
    /// <summary>Days an incident must have been open before its seller is chased.</summary>
    public const int DefaultThresholdDays = 2;

    /// <summary>
    /// Below this the threshold stops meaning anything: every incident opened today would qualify,
    /// including ones the seller has had no working hours to see.
    /// </summary>
    public const int MinThresholdDays = 1;

    /// <summary>A typo's worth of headroom, not a real policy. Past this the list is empty anyway.</summary>
    public const int MaxThresholdDays = 90;

    /// <summary>
    /// Most incidents one message lists before it is truncated.
    ///
    /// <para>Lower than <see cref="LateOrderBuilder.MaxOrderLinesPerMessage"/> (60) on purpose. That cap
    /// is safe because a late-order line is <c>• {orderNumber}</c> — about a dozen characters. The
    /// default incident line carries the reason and the age too, so sixty of them would run past
    /// <see cref="Automation.WhatsAppMessageRunner.MaxMessageChars"/> and the send endpoint would refuse
    /// a message the operator had already approved.</para>
    /// </summary>
    public const int MaxIncidentLinesPerMessage = 30;

    /// <summary>Keeps a pathological upload from producing a megabyte of review rows.</summary>
    const int MaxReviewRows = 500;

    const string DisplayFormat = "yyyy-MM-dd HH:mm";

    /// <summary>One incident that survived every filter, before the rows are grouped by seller.</summary>
    sealed record ChaseableIncident(
        string SellerKey,
        string SellerName,
        string OrderNumber,
        DateTime OpenedOn,
        double AgeDays,
        string Reason,
        string Status);

    public static IncidentWarningData Build(
        IReadOnlyList<IncidentWarningInputRow> rows, int thresholdDays, SellerGroupMap map)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(map);

        var threshold = Math.Clamp(thresholdDays, MinThresholdDays, MaxThresholdDays);

        // Captured once for the whole build. A per-row DateTime.Now would let a slow pass straddle a
        // minute boundary and report two incidents of the same age a day apart.
        //
        // Ages are recomputed here rather than read off the dashboard's own ageDays: that number was
        // measured when the export was uploaded and is stale by however long the operator spent
        // reading the report — and it arrives from the browser, so eligibility would be decided by a
        // value the client supplies. The date string is re-parsed instead.
        var referenceTime = DateTime.Now;

        var rowsIn = 0;
        var closed = 0;
        var resolvedAwaitingUs = 0;
        var waitingOnOther = 0;
        var noOpenedDate = 0;
        var belowThreshold = 0;

        var chaseable = new List<ChaseableIncident>();
        var review = new List<IncidentWarningReviewRow>();

        void Review(string orderNumber, string seller, string openedOn, string reason)
        {
            if (review.Count < MaxReviewRows)
                review.Add(new IncidentWarningReviewRow(orderNumber, seller, openedOn, reason));
        }

        foreach (var row in rows)
        {
            var orderNumber = (row.OrderNumber ?? "").Trim();
            var seller = (row.Seller ?? "").Trim();
            var lifecycle = (row.Lifecycle ?? "").Trim();
            var waitingOn = (row.WaitingOn ?? "").Trim();
            var openedOnText = (row.OpenedOn ?? "").Trim();
            var reason = (row.Reason ?? "").Trim();
            var status = (row.Status ?? "").Trim();

            rowsIn++;

            if (string.Equals(lifecycle, IncidentLifecycle.Closed, StringComparison.Ordinal))
            {
                closed++;
                continue;
            }

            if (string.Equals(lifecycle, IncidentLifecycle.Resolved, StringComparison.Ordinal))
            {
                resolvedAwaitingUs++;
                continue;
            }

            // An allow-list on the state that makes the seller the one holding things up, not a
            // deny-list on the states that do not. Anything unrecognised — a lifecycle this app has
            // not seen, a blank waitingOn — must not default into "message the seller".
            if (!string.Equals(lifecycle, IncidentLifecycle.Open, StringComparison.Ordinal) ||
                !string.Equals(waitingOn, IncidentWaitingOn.Seller, StringComparison.Ordinal))
            {
                waitingOnOther++;
                continue;
            }

            var openedOn = TabularFile.ParseDate(openedOnText);
            if (openedOn is null)
            {
                // Never fall through to "assume it is old". Telling a seller they have sat on a
                // complaint for four days off a date we could not read is the failure that ends this
                // channel's credibility.
                noOpenedDate++;
                Review(orderNumber, seller, openedOnText,
                    openedOnText.Length == 0
                        ? "No opened-on date on the row"
                        : "The opened-on date could not be read");
                continue;
            }

            var ageDays = Math.Round((referenceTime - openedOn.Value).TotalDays, 1);
            if (ageDays < threshold)
            {
                belowThreshold++;
                continue;
            }

            chaseable.Add(new ChaseableIncident(
                SellerKey: SellerGroupMap.FoldName(seller),
                SellerName: seller,
                OrderNumber: orderNumber,
                OpenedOn: openedOn.Value,
                AgeDays: ageDays,
                Reason: reason,
                Status: status));
        }

        var warnings = new List<string>();
        var sellers = GroupSellers(chaseable, map, warnings);

        warnings.AddRange(map.LoadWarnings);

        var funnel = new IncidentWarningFunnel(
            RowsIn: rowsIn,
            Closed: closed,
            ResolvedAwaitingUs: resolvedAwaitingUs,
            WaitingOnOther: waitingOnOther,
            NoOpenedDate: noOpenedDate,
            BelowThreshold: belowThreshold,
            Eligible: chaseable.Count,
            Sellers: sellers.Count,
            MappedSellers: sellers.Count(s => s.GroupName is not null),
            UnmappedSellers: sellers.Count(s => s.GroupName is null),
            NameConflictSellers: sellers.Count(s => s.MappingConflict));

        return new IncidentWarningData(
            ReferenceTime: referenceTime.ToString(DisplayFormat, CultureInfo.InvariantCulture),
            ThresholdDays: threshold,
            Sellers: sellers,
            Funnel: funnel,
            Review: review,
            Warnings: warnings);
    }

    // ---------------------------------------------------------------------

    static List<IncidentWarningSeller> GroupSellers(
        List<ChaseableIncident> chaseable, SellerGroupMap map, List<string> warnings)
    {
        var result = new List<IncidentWarningSeller>();

        foreach (var group in chaseable.GroupBy(r => r.SellerKey, StringComparer.Ordinal))
        {
            // Deliberately not collapsed by order number: one order can carry several incidents, and
            // each is a separate complaint the seller has to answer. This is where Late Order Warnings
            // does the opposite — there the export is one row per order *line* and the lines are one
            // order, so they merge.
            var incidents = group
                .OrderByDescending(r => r.AgeDays)
                .ThenBy(r => r.OrderNumber, StringComparer.OrdinalIgnoreCase)
                .Select(r => new IncidentWarningLine(
                    OrderNumber: r.OrderNumber,
                    OpenedOn: r.OpenedOn.ToString(DisplayFormat, CultureInfo.InvariantCulture),
                    AgeDays: r.AgeDays,
                    Reason: r.Reason,
                    Status: r.Status))
                .ToList();

            // One seller can appear under two spellings inside a single export (a mid-period rebrand,
            // a dotted/dotless i). They fold to one key; the most frequent spelling is what is shown.
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

            // Name only: the incident export has no seller-id column, so there is nothing else to
            // resolve on. Passing "" keeps SellerGroupMap.Resolve's precedence rules intact rather
            // than adding a second lookup path that could disagree with the late-orders one.
            var match = map.Resolve("", displayName);

            result.Add(new IncidentWarningSeller(
                SellerName: displayName,
                GroupName: match.GroupName,
                MappingProblem: match.Problem,
                MappingConflict: match.IsConflict,
                IncidentCount: incidents.Count,
                MaxAgeDays: incidents.Count > 0 ? incidents.Max(i => i.AgeDays) : 0,
                Incidents: incidents));
        }

        return [.. result
            .OrderByDescending(s => s.MaxAgeDays)
            .ThenByDescending(s => s.IncidentCount)
            .ThenBy(s => s.SellerName, StringComparer.OrdinalIgnoreCase)];
    }
}
