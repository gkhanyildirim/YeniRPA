using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Incidents report
//
// The Mirakl incident panel exports open and closed incidents as two separate downloads that cannot
// be pulled together, so this module reads both and reports over the union. Both exports carry the
// same 23-column header; every row below is one line of either file.
//
// Consumed by wwwroot/js/incidents-report.js, which does all the grouping — the builder sends flat
// rows so that every scorecard on the dashboard reflects whatever filter is active.
// ---------------------------------------------------------------------------

/// <summary>
/// Where an incident stands. Derived from the dates on the row rather than from the file it arrived
/// in or from <see cref="IncidentRow.Status"/>, which is free text Mirakl can extend at any time.
/// </summary>
public static class IncidentLifecycle
{
    /// <summary>"Closed on" carries a date.</summary>
    public const string Closed = "closed";

    /// <summary>
    /// No closing date, but a closing reason is already written — the seller has answered and Mirakl
    /// has not stamped the closure yet. Held out of the open backlog and the aging tables, and
    /// counted on its own so it never disappears between the two.
    /// </summary>
    public const string Resolved = "resolved";

    public const string Open = "open";
}

/// <summary>
/// Who owes the next move.
///
/// <para><b>An open incident is never ours.</b> While it is open the thread runs between the customer
/// and the seller, and our team has nothing to answer. Our work starts the moment the seller marks the
/// incident resolved: from then on the verification and the closure are ours, which is what
/// <see cref="Us"/> counts. Reading this off "Last action by" alone — treating a waiting customer
/// message as our queue — produces exactly the wrong worklist.</para>
/// </summary>
public static class IncidentWaitingOn
{
    /// <summary>The seller says it is solved; we still have to verify it and close it.</summary>
    public const string Us = "us";

    /// <summary>Still open and the customer spoke last, so the seller owes the reply.</summary>
    public const string Seller = "seller";

    /// <summary>Still open and the seller spoke last, so the thread is with the customer.</summary>
    public const string Customer = "customer";

    /// <summary>Still open and an operator acted last — the ball is back with the other two.</summary>
    public const string OperatorActed = "operator-acted";

    public const string None = "none";
}

/// <summary>
/// What kind of account performed an action. The export writes a role ("Customer" / "Seller" /
/// "Operator") and, separately, a user — and the user is what distinguishes a person at the
/// marketplace operator from the automation posting under the same role.
/// </summary>
public static class IncidentActorKind
{
    /// <summary>No user on the row: the export leaves it blank for customer actions.</summary>
    public const string Customer = "customer";

    /// <summary>A mailbox at <see cref="Services.IncidentsReportBuilder.OperatorMailDomain"/>.</summary>
    public const string Internal = "internal";

    /// <summary>The literal <c>Operator API</c> user — an action posted by automation, not a person.</summary>
    public const string Automation = "automation";

    /// <summary>Any other mailbox, which is a seller's own address.</summary>
    public const string Seller = "seller";
}

/// <summary>Which of the two uploads a row came from.</summary>
public static class IncidentSource
{
    public const string Open = "open";
    public const string Closed = "closed";
}

/// <summary>
/// One incident. The first block is the export's own columns, unchanged; the second is everything
/// derived from them, computed once here so the dashboard and the Excel exports can never disagree
/// about an age or a verdict.
/// </summary>
public sealed record IncidentRow(
    [property: JsonPropertyName("orderNumber")] string OrderNumber,

    /// <summary>"01259_326674352-A" reduced to "326674352" — the customer order behind a split line.</summary>
    [property: JsonPropertyName("orderCore")] string OrderCore,

    /// <summary>The per-seller split suffix ("A", "B", "C"), empty when the number carries none.</summary>
    [property: JsonPropertyName("splitSuffix")] string SplitSuffix,

    [property: JsonPropertyName("orderCreatedOn")] string? OrderCreatedOn,
    [property: JsonPropertyName("customerName")] string CustomerName,
    [property: JsonPropertyName("seller")] string Seller,

    /// <summary>Mirakl's own status text, kept as its own dimension beside <see cref="Lifecycle"/>.</summary>
    [property: JsonPropertyName("status")] string Status,

    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("openedBy")] string OpenedBy,
    [property: JsonPropertyName("openedByUser")] string OpenedByUser,
    [property: JsonPropertyName("openedOn")] string? OpenedOn,

    /// <summary>Day key of <see cref="OpenedOn"/> as yyyy-MM-dd, for the date filter and the trend.</summary>
    [property: JsonPropertyName("openedDay")] string? OpenedDay,

    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("closedBy")] string ClosedBy,
    [property: JsonPropertyName("closedByUser")] string ClosedByUser,
    [property: JsonPropertyName("closedOn")] string? ClosedOn,
    [property: JsonPropertyName("closedDay")] string? ClosedDay,
    [property: JsonPropertyName("closingReason")] string ClosingReason,
    [property: JsonPropertyName("productSku")] string ProductSku,
    [property: JsonPropertyName("product")] string Product,

    // The export also carries Quantity, the order total and the currency. This report is an incident
    // queue, not a financial one, so those columns are deliberately not read: see the class remarks on
    // IncidentsReportBuilder.RequiredColumns.

    [property: JsonPropertyName("lastActionBy")] string LastActionBy,
    [property: JsonPropertyName("lastActionByUser")] string LastActionByUser,
    [property: JsonPropertyName("lastActionDate")] string? LastActionDate,
    [property: JsonPropertyName("lastActionDay")] string? LastActionDay,
    [property: JsonPropertyName("lastAction")] string LastAction,

    /// <summary>One of <see cref="IncidentSource"/>.</summary>
    [property: JsonPropertyName("source")] string Source,

    /// <summary>One of <see cref="IncidentLifecycle"/>.</summary>
    [property: JsonPropertyName("lifecycle")] string Lifecycle,

    /// <summary>One of <see cref="IncidentWaitingOn"/>.</summary>
    [property: JsonPropertyName("waitingOn")] string WaitingOn,

    [property: JsonPropertyName("openedByKind")] string OpenedByKind,
    [property: JsonPropertyName("lastActorKind")] string LastActorKind,
    [property: JsonPropertyName("closedByKind")] string? ClosedByKind,

    /// <summary>Days the incident has been open. Null once it is closed, and null on an unreadable date.</summary>
    [property: JsonPropertyName("ageDays")] double? AgeDays,

    /// <summary>Opened to closed, in days. Set on closed incidents only.</summary>
    [property: JsonPropertyName("resolutionDays")] double? ResolutionDays,

    /// <summary>Order placed to incident opened — how long the purchase survived before it went wrong.</summary>
    [property: JsonPropertyName("orderToIncidentDays")] double? OrderToIncidentDays,

    /// <summary>Days since anyone touched the thread. Still meaningful on a closed row, as its age.</summary>
    [property: JsonPropertyName("silenceDays")] double? SilenceDays,

    /// <summary>Data problems found on this row; empty for a clean one. Drives the review table.</summary>
    [property: JsonPropertyName("issues")] IReadOnlyList<string> Issues);

/// <summary>
/// The dashboard payload. Thresholds travel with the data so the browser never restates a number
/// the builder owns.
/// </summary>
public sealed record IncidentsData(
    [property: JsonPropertyName("rows")] IReadOnlyList<IncidentRow> Rows,

    /// <summary>Rows read from each upload, so an obviously short file is visible on the dashboard.</summary>
    [property: JsonPropertyName("openFileRows")] int OpenFileRows,
    [property: JsonPropertyName("closedFileRows")] int ClosedFileRows,

    /// <summary>The instant every age on every row was measured against.</summary>
    [property: JsonPropertyName("referenceTime")] string ReferenceTime,

    /// <summary>Latest action seen anywhere in the uploads — how fresh the export itself is.</summary>
    [property: JsonPropertyName("dataAsOf")] string? DataAsOf,

    [property: JsonPropertyName("warningDays")] int WarningDays,
    [property: JsonPropertyName("breachDays")] int BreachDays,
    [property: JsonPropertyName("staleDays")] int StaleDays,

    /// <summary>
    /// Default lower bound for closed incidents, as yyyy-MM-dd. The closed export is a full history
    /// dump; the dashboard pre-fills this into its "Closed from" input rather than the builder
    /// dropping the rows, so the older ones stay one date change away.
    /// </summary>
    [property: JsonPropertyName("closedFrom")] string ClosedFrom,

    /// <summary>Below this many closed incidents a seller is left out of the speed ranking.</summary>
    [property: JsonPropertyName("minSampleSize")] int MinSampleSize,

    /// <summary>File-level notes for the operator — never a reason to fail the upload.</summary>
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);
