using System.Text.Json;
using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Order report (Late Shipment & Cancellation)
// ---------------------------------------------------------------------------

/// <summary>
/// One order line as consumed by <c>wwwroot/js/order-report.js</c>. The field names are deliberately
/// terse — a full export is well over 100k rows and these names are repeated on every one of them.
/// The JS reads each field by name, so renaming any of them silently breaks the dashboard.
///
/// The first ten fields are the original payload and are always written. Everything after them
/// backs the extended sections (cancellation breakdown, lead-time/SLA, category, data quality) and is
/// omitted from the JSON when it holds its default value — on a large export most rows carry no
/// cancellation request and no reason code, so skipping those keys measurably shrinks the response.
/// The JS therefore has to treat a missing key as zero / empty, never as an error.
///
/// High-cardinality-but-repetitive labels (category, brand, city) are sent as indexes into the
/// dictionaries on <see cref="OrderReportData"/> rather than as strings on every row.
/// </summary>
public sealed record OrderReportRow(
    [property: JsonPropertyName("s")] string s,
    [property: JsonPropertyName("dc")] string? dc,
    [property: JsonPropertyName("st")] string st,
    [property: JsonPropertyName("amt")] double amt,
    [property: JsonPropertyName("cur")] string cur,
    [property: JsonPropertyName("sd")] string? sd,
    [property: JsonPropertyName("sh")] string? sh,
    [property: JsonPropertyName("rd")] string? rd,
    [property: JsonPropertyName("rsn")] string rsn,
    [property: JsonPropertyName("sc")] string sc,

    /// <summary>Order number. Needed by the cancellation detail list so a finding can be acted on.</summary>
    [property: JsonPropertyName("ord"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? ord = null,

    /// <summary>Acceptance date (ISO) — start of the seller's own preparation clock.</summary>
    [property: JsonPropertyName("ac"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? ac = null,

    /// <summary>Cancellation request outcome, compressed: "A" = accepted, "R" = rejected, absent = no request.</summary>
    [property: JsonPropertyName("crs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? crs = null,

    /// <summary>Cancellation reason code lifted out of the request payload JSON (e.g. CSTWSH).</summary>
    [property: JsonPropertyName("crr"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? crr = null,

    /// <summary>Promised handling time in days ("Lead time to ship").</summary>
    [property: JsonPropertyName("lt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double lt = 0,

    /// <summary>
    /// Amount transferred to the seller, exactly as exported. On canceled lines the export leaves the
    /// would-have-been payout in this field, so it must never be summed without excluding them.
    /// </summary>
    [property: JsonPropertyName("pay"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double pay = 0,

    /// <summary>Total canceled amount including taxes.</summary>
    [property: JsonPropertyName("can"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double can = 0,

    /// <summary>Total refunded amount including taxes.</summary>
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double @ref = 0,

    /// <summary>True when an invoice was issued for the order.</summary>
    [property: JsonPropertyName("inv"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool inv = false,

    /// <summary>Index into <see cref="OrderReportData.Categories"/>; absent when unknown.</summary>
    [property: JsonPropertyName("ci"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ci = 0,

    /// <summary>Index into <see cref="OrderReportData.Brands"/>; absent when unknown.</summary>
    [property: JsonPropertyName("bi"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int bi = 0,

    /// <summary>Index into <see cref="OrderReportData.Cities"/>; absent when unknown.</summary>
    [property: JsonPropertyName("yi"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int yi = 0);

/// <summary>
/// Cancellation reason codes carry no human-readable text in the export, so the mapping lives here
/// and travels with the payload — the dashboard never hard-codes it.
/// </summary>
public sealed record CancellationReasonLabel(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("action")] string Action);

public sealed record OrderReportData(
    [property: JsonPropertyName("rows")] IReadOnlyList<OrderReportRow> Rows,
    [property: JsonPropertyName("carrierKeywords")] IReadOnlyList<string> CarrierKeywords,
    [property: JsonPropertyName("canceledStatus")] string CanceledStatus,

    /// <summary>Status that marks a line as refunded after delivery, as opposed to canceled before it.</summary>
    [property: JsonPropertyName("refundedStatus")] string RefundedStatus,

    /// <summary>Status of a line the customer received. Counted for the Key metrics row.</summary>
    [property: JsonPropertyName("receivedStatus")] string ReceivedStatus,

    /// <summary>Status of a line the seller turned down. Counted for the Key metrics row.</summary>
    [property: JsonPropertyName("rejectedStatus")] string RejectedStatus,

    /// <summary>
    /// Reason value the platform writes when it closes a line as delivered without a real carrier
    /// notification. Delivery-time metrics exclude these rows.
    /// </summary>
    [property: JsonPropertyName("autoReceivedReason")] string AutoReceivedReason,

    /// <summary>Dictionary for <see cref="OrderReportRow.ci"/>. Index 0 is always the "unknown" slot.</summary>
    [property: JsonPropertyName("categories")] IReadOnlyList<string> Categories,

    /// <summary>Dictionary for <see cref="OrderReportRow.bi"/>. Index 0 is always the "unknown" slot.</summary>
    [property: JsonPropertyName("brands")] IReadOnlyList<string> Brands,

    /// <summary>Dictionary for <see cref="OrderReportRow.yi"/>. Index 0 is always the "unknown" slot.</summary>
    [property: JsonPropertyName("cities")] IReadOnlyList<string> Cities,

    [property: JsonPropertyName("reasonLabels")] IReadOnlyList<CancellationReasonLabel> ReasonLabels,

    /// <summary>Minimum shipped lines before a seller is ranked in the best/worst on-time lists.</summary>
    [property: JsonPropertyName("minSampleSize")] int MinSampleSize,

    /// <summary>Minimum shipped lines before a lead-time change is suggested for a seller.</summary>
    [property: JsonPropertyName("minLeadTimeSample")] int MinLeadTimeSample,

    /// <summary>
    /// Optional columns that were not present in the uploaded file. The dashboard names them in the
    /// empty state of whichever section they feed, so a missing column reads as a data-source gap
    /// rather than as a broken report.
    /// </summary>
    [property: JsonPropertyName("missingColumns")] IReadOnlyList<string> MissingColumns);

// ---------------------------------------------------------------------------
// Return SLA report
// ---------------------------------------------------------------------------

/// <summary>One matched return candidate: a template row that carries a tracking number.</summary>
public sealed record ReturnSlaRow(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("orderNumber")] string OrderNumber,
    [property: JsonPropertyName("seller")] string Seller,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("shippedToSellerDate")] string? ShippedToSellerDate,
    [property: JsonPropertyName("elapsedDays")] double? ElapsedDays,
    [property: JsonPropertyName("slaDays")] int SlaDays,
    [property: JsonPropertyName("isConfirmedReturn")] bool IsConfirmedReturn,
    [property: JsonPropertyName("slaMissed")] bool SlaMissed,
    [property: JsonPropertyName("pastWarning")] bool PastWarning,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// Refund timing for canceled/refunded orders. Computed over the whole orders file, not just the
/// rows present in the return templates.
/// </summary>
public sealed record ReturnSlaPaymentRow(
    [property: JsonPropertyName("orderNumber")] string OrderNumber,
    [property: JsonPropertyName("seller")] string Seller,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("amount")] double Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("dateCreated")] string DateCreated,
    [property: JsonPropertyName("debitDate")] string DebitDate,
    [property: JsonPropertyName("paymentDays")] double PaymentDays);

public sealed record ReturnSlaData(
    [property: JsonPropertyName("rows")] IReadOnlyList<ReturnSlaRow> Rows,
    [property: JsonPropertyName("payments")] IReadOnlyList<ReturnSlaPaymentRow> Payments);

// ---------------------------------------------------------------------------
// Per-section table export
// ---------------------------------------------------------------------------

/// <summary>One column of an exported section. <paramref name="Numeric"/> only drives alignment;
/// whether a cell is written as a number is decided by the JSON type of the value itself.</summary>
public sealed record TableExportColumn(
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("numeric")] bool Numeric = false);

/// <summary>
/// A single dashboard section on its way to Excel. The dashboard aggregates in the browser and the
/// date filter changes what is on screen, so the rows are posted from the client rather than
/// recomputed here — what downloads is exactly what the user is looking at.
///
/// Cell values keep their JSON type: a number lands in Excel as a number, everything else as text.
/// </summary>
public sealed record TableExportRequest(
    [property: JsonPropertyName("fileName")] string? FileName,
    [property: JsonPropertyName("sheetName")] string? SheetName,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("columns")] IReadOnlyList<TableExportColumn>? Columns,
    [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyList<JsonElement>>? Rows);
