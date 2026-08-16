using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Late Order Warnings — overdue orders, grouped by seller, ready to be messaged.
//
// Turns a Mirakl orders export into "who is overdue, by how much, and which WhatsApp group do they
// belong to". Consumed by wwwroot/js/late-orders.js.
// ---------------------------------------------------------------------------

/// <summary>What the operator chose on the prepare form.</summary>
public sealed record LateOrderOptions(
    /// <summary>
    /// Hours added to every shipping deadline before it is compared to now. Present because it is not
    /// yet settled whether the export writes the deadline in UTC or in local time — see the panel note
    /// and <see cref="Services.LateOrderBuilder"/>. Never applied to the reference time.
    /// </summary>
    double OffsetHours);

/// <summary>One overdue order. The raw and the offset deadline both travel so the operator can see
/// what the offset did to every single row rather than trusting the number.</summary>
public sealed record LateOrderLine(
    [property: JsonPropertyName("orderNumber")] string OrderNumber,
    [property: JsonPropertyName("status")] string Status,

    /// <summary>The deadline cell exactly as it appeared in the file, for eyeballing.</summary>
    [property: JsonPropertyName("deadlineRaw")] string DeadlineRaw,

    /// <summary>The deadline after the offset, "yyyy-MM-dd HH:mm".</summary>
    [property: JsonPropertyName("deadlineEffective")] string DeadlineEffective,

    /// <summary>Whole days, floored, never negative. Zero for anything under 24 hours.</summary>
    [property: JsonPropertyName("daysLate")] int DaysLate,

    [property: JsonPropertyName("hoursLate")] double HoursLate,
    [property: JsonPropertyName("dateCreated")] string? DateCreated,
    [property: JsonPropertyName("acceptanceDate")] string? AcceptanceDate,
    [property: JsonPropertyName("shippingCompany")] string? ShippingCompany,

    /// <summary>How many export rows collapsed into this order — the export is one row per line.</summary>
    [property: JsonPropertyName("lineCount")] int LineCount);

/// <summary>
/// One seller's overdue orders and the group they get messaged in. A seller with no group stays in
/// the list with <paramref name="GroupName"/> null and <paramref name="MappingProblem"/> set —
/// there is deliberately no separate "unmapped" collection, because two arrays invite a panel that
/// renders one and forgets the other.
/// </summary>
public sealed record LateOrderSeller(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("groupName")] string? GroupName,

    /// <summary>Why no group was resolved: unmapped, blank group, or a conflict in the mapping file.</summary>
    [property: JsonPropertyName("mappingProblem")] string? MappingProblem,

    [property: JsonPropertyName("orderCount")] int OrderCount,
    [property: JsonPropertyName("maxDaysLate")] int MaxDaysLate,
    [property: JsonPropertyName("orders")] IReadOnlyList<LateOrderLine> Orders);

/// <summary>
/// Where the rows in the file went. Each count is a terminal bucket, so they sum to
/// <paramref name="RowsInFile"/> — unlike the Create Return funnel, which reports survivors.
/// </summary>
public sealed record LateOrderFunnel(
    [property: JsonPropertyName("rowsInFile")] int RowsInFile,
    [property: JsonPropertyName("alreadyShipped")] int AlreadyShipped,

    /// <summary>Status is not one the seller can still act on. Surfaced, never messaged.</summary>
    [property: JsonPropertyName("statusNotChaseable")] int StatusNotChaseable,

    [property: JsonPropertyName("noDeadline")] int NoDeadline,
    [property: JsonPropertyName("unreadableDeadline")] int UnreadableDeadline,
    [property: JsonPropertyName("notYetLate")] int NotYetLate,

    /// <summary>Overdue export rows, before they are collapsed into distinct orders.</summary>
    [property: JsonPropertyName("overdueRows")] int OverdueRows,

    [property: JsonPropertyName("overdueOrders")] int OverdueOrders,
    [property: JsonPropertyName("sellers")] int Sellers,
    [property: JsonPropertyName("mappedSellers")] int MappedSellers,
    [property: JsonPropertyName("unmappedSellers")] int UnmappedSellers);

/// <summary>A row that was set aside for review rather than dropped silently.</summary>
public sealed record LateOrderReviewRow(
    [property: JsonPropertyName("orderNumber")] string OrderNumber,
    [property: JsonPropertyName("seller")] string Seller,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("deadlineRaw")] string DeadlineRaw,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record LateOrderData(
    /// <summary>Captured once for the whole build, "yyyy-MM-dd HH:mm".</summary>
    [property: JsonPropertyName("referenceTime")] string ReferenceTime,

    [property: JsonPropertyName("offsetHours")] double OffsetHours,
    [property: JsonPropertyName("sellers")] IReadOnlyList<LateOrderSeller> Sellers,
    [property: JsonPropertyName("funnel")] LateOrderFunnel Funnel,
    [property: JsonPropertyName("review")] IReadOnlyList<LateOrderReviewRow> Review,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

// ---------------------------------------------------------------------------
// Seller -> WhatsApp group mapping
// ---------------------------------------------------------------------------

/// <summary>One line of the mapping table. A blank <paramref name="GroupName"/> means the operator
/// has seen this seller and not finished, which is reported differently from "never mapped".</summary>
public sealed record SellerGroupEntry(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("groupName")] string GroupName);

/// <summary>
/// The whole of seller-groups.json. The two templates live here rather than in a second file because
/// they are operator-owned copy of the same kind as the mappings — one file to back up, one file to
/// delete to start over. Absent or blank falls back to the const default.
/// </summary>
public sealed record SellerGroupFile(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("updatedUtc")] string? UpdatedUtc,
    [property: JsonPropertyName("messageTemplate")] string? MessageTemplate,
    [property: JsonPropertyName("orderLineTemplate")] string? OrderLineTemplate,
    [property: JsonPropertyName("entries")] IReadOnlyList<SellerGroupEntry> Entries);

// ---------------------------------------------------------------------------
// Rendered messages
// ---------------------------------------------------------------------------

/// <summary>One message, as it will be typed. Body is the exact text — the preview, the payload
/// posted back and the keystrokes are all this same string.</summary>
public sealed record RenderedMessage(
    [property: JsonPropertyName("groupName")] string GroupName,
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("orderCount")] int OrderCount,

    /// <summary>True when the seller had more orders than one message carries.</summary>
    [property: JsonPropertyName("truncated")] bool Truncated,

    /// <summary>Placeholders in the template that are not recognised, so the panel can point at the
    /// typo instead of shipping "Merhaba ," to a seller.</summary>
    [property: JsonPropertyName("unknownPlaceholders")] IReadOnlyList<string> UnknownPlaceholders);
