using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Incident Warnings — incidents left open too long, grouped by seller, ready to be messaged.
//
// The sibling of Late Order Warnings, and the difference is the clock it reads. That module chases a
// missed *shipping deadline*; this one chases the age of the *incident itself* — how long ago it was
// opened, never when the order behind it was placed. An incident raised today against a three-month-old
// order is not late, and one raised three days ago against an order placed this morning is.
//
// Lives inside the Incidents Report panel and is consumed by wwwroot/js/incidents-report.js.
// ---------------------------------------------------------------------------

/// <summary>
/// One incident as the browser hands it back for chasing — a deliberately narrow projection of
/// <see cref="IncidentRow"/>, not the row itself.
///
/// <para>The dashboard holds ~35 fields per incident over both exports, and the closed one is a full
/// history dump. Posting all of that back to ask "who should be chased" would put the customer's name
/// and the product they complained about on the wire for no reason: nothing below the eligibility rule
/// reads them. These seven fields are what the rule and the message actually need.</para>
///
/// <para><see cref="OpenedOn"/> arrives as the display string the report already produced
/// ("yyyy-MM-dd HH:mm"); the builder re-parses it and measures the age itself rather than trusting a
/// number computed in the browser — see <see cref="Services.IncidentWarningBuilder"/>.</para>
/// </summary>
public sealed record IncidentWarningInputRow(
    [property: JsonPropertyName("seller")] string? Seller,
    [property: JsonPropertyName("orderNumber")] string? OrderNumber,
    [property: JsonPropertyName("openedOn")] string? OpenedOn,

    /// <summary>One of <see cref="IncidentLifecycle"/>.</summary>
    [property: JsonPropertyName("lifecycle")] string? Lifecycle,

    /// <summary>One of <see cref="IncidentWaitingOn"/>.</summary>
    [property: JsonPropertyName("waitingOn")] string? WaitingOn,

    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("status")] string? Status);

/// <summary>One incident that qualifies for a warning, as it will appear in the message.</summary>
public sealed record IncidentWarningLine(
    [property: JsonPropertyName("orderNumber")] string OrderNumber,

    /// <summary>When the incident was opened, "yyyy-MM-dd HH:mm".</summary>
    [property: JsonPropertyName("openedOn")] string OpenedOn,

    /// <summary>Days open, one decimal, measured against the build's reference time.</summary>
    [property: JsonPropertyName("ageDays")] double AgeDays,

    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("status")] string Status);

/// <summary>
/// One seller's chaseable incidents and the group they get messaged in. A seller with no group stays
/// in the list with <paramref name="GroupName"/> null and <paramref name="MappingProblem"/> set, the
/// same shape <see cref="LateOrderSeller"/> uses and for the same reason: two collections invite a
/// panel that renders one and forgets the other.
///
/// <para><b>There is no seller id here, and that is not an omission.</b> The Mirakl incident export
/// carries no "Seller ID" column at all — see
/// <see cref="Services.IncidentsReportBuilder.RequiredColumns"/> — so the mapping can only ever be
/// resolved on the folded seller name. Every consequence of that is reported through
/// <paramref name="MappingProblem"/> rather than guessed at.</para>
/// </summary>
public sealed record IncidentWarningSeller(
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("groupName")] string? GroupName,

    /// <summary>Why no group was resolved: unmapped, blank group, or a name mapped more than once.</summary>
    [property: JsonPropertyName("mappingProblem")] string? MappingProblem,

    /// <summary>True when <paramref name="MappingProblem"/> is an ambiguity rather than an absence —
    /// the mapping table names this seller more than once. Fixed by removing a row, not adding one.</summary>
    [property: JsonPropertyName("mappingConflict")] bool MappingConflict,

    [property: JsonPropertyName("incidentCount")] int IncidentCount,
    [property: JsonPropertyName("maxAgeDays")] double MaxAgeDays,
    [property: JsonPropertyName("incidents")] IReadOnlyList<IncidentWarningLine> Incidents);

/// <summary>
/// Where the incidents went. Each count is a terminal bucket, so the first six sum to
/// <paramref name="RowsIn"/> — the same contract as <see cref="LateOrderFunnel"/>.
/// </summary>
public sealed record IncidentWarningFunnel(
    [property: JsonPropertyName("rowsIn")] int RowsIn,

    /// <summary>Already closed. Nothing to chase.</summary>
    [property: JsonPropertyName("closed")] int Closed,

    /// <summary>The seller has answered and the closure is ours — chasing them here would warn the
    /// wrong party. See <see cref="IncidentWaitingOn"/>.</summary>
    [property: JsonPropertyName("resolvedAwaitingUs")] int ResolvedAwaitingUs,

    /// <summary>Open, but the reply is not the seller's to make right now.</summary>
    [property: JsonPropertyName("waitingOnOther")] int WaitingOnOther,

    /// <summary>"Opened on" was blank or could not be read, so the age is unknown. Surfaced for
    /// review, never chased.</summary>
    [property: JsonPropertyName("noOpenedDate")] int NoOpenedDate,

    [property: JsonPropertyName("belowThreshold")] int BelowThreshold,
    [property: JsonPropertyName("eligible")] int Eligible,

    [property: JsonPropertyName("sellers")] int Sellers,
    [property: JsonPropertyName("mappedSellers")] int MappedSellers,
    [property: JsonPropertyName("unmappedSellers")] int UnmappedSellers,

    /// <summary>
    /// Sellers held back because their name is mapped more than once. Counted apart from
    /// <paramref name="UnmappedSellers"/> on purpose: "you never mapped them" is fixed by adding a row,
    /// "the name is ambiguous" is fixed by removing one, and the incident export's missing seller-id
    /// column makes this the failure the operator will actually hit.
    /// </summary>
    [property: JsonPropertyName("nameConflictSellers")] int NameConflictSellers);

/// <summary>An incident set aside for review rather than dropped silently.</summary>
public sealed record IncidentWarningReviewRow(
    [property: JsonPropertyName("orderNumber")] string OrderNumber,
    [property: JsonPropertyName("seller")] string Seller,
    [property: JsonPropertyName("openedOn")] string OpenedOn,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record IncidentWarningData(
    /// <summary>Captured once for the whole build, "yyyy-MM-dd HH:mm". Every age is measured against it.</summary>
    [property: JsonPropertyName("referenceTime")] string ReferenceTime,

    /// <summary>The threshold actually applied, after clamping — not necessarily what was asked for.</summary>
    [property: JsonPropertyName("thresholdDays")] int ThresholdDays,

    [property: JsonPropertyName("sellers")] IReadOnlyList<IncidentWarningSeller> Sellers,
    [property: JsonPropertyName("funnel")] IncidentWarningFunnel Funnel,
    [property: JsonPropertyName("review")] IReadOnlyList<IncidentWarningReviewRow> Review,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);
