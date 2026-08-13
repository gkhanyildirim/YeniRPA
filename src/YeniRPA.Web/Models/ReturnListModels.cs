using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Create Return — prepared list
//
// Turns the two return templates, the returns export and the orders export into the two-column list
// the Create Return automation runs on. Consumed by wwwroot/js/create-return.js.
// ---------------------------------------------------------------------------

/// <summary>What the operator chose on the prepare form.</summary>
public sealed record ReturnListOptions(
    DateTime? From,
    DateTime? To,
    /// <summary>Drops "Değişim" (exchange) rows from template A. Template B carries no request type.</summary>
    bool ReturnsOnly);

/// <summary>
/// One order the automation will file a return for. <paramref name="OrderNumber"/> and
/// <paramref name="TrackingNumber"/> are the only two the run needs — everything else is there so
/// the list can be reviewed before it writes to the marketplace.
/// </summary>
public sealed record ReturnListRow(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("orderNumber")] string OrderNumber,
    [property: JsonPropertyName("trackingNumber")] string TrackingNumber,

    /// <summary>The order number as it appeared in the template, before the orders lookup.</summary>
    [property: JsonPropertyName("sourceOrderNo")] string SourceOrderNo,

    [property: JsonPropertyName("seller")] string Seller,
    [property: JsonPropertyName("sellerId")] string SellerId,

    /// <summary>Talep Tipi on template A, State on template B — different fields, same review purpose.</summary>
    [property: JsonPropertyName("typeOrState")] string TypeOrState,

    [property: JsonPropertyName("requestDate")] string? RequestDate);

/// <summary>A candidate that was dropped, and why. Only rows that survived the tracking-code and
/// date filters appear here; the two bulk filters are reported as counts on the funnel instead.</summary>
public sealed record ReturnListExcludedRow(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("sourceOrderNo")] string SourceOrderNo,
    [property: JsonPropertyName("trackingNumber")] string TrackingNumber,
    [property: JsonPropertyName("seller")] string Seller,
    [property: JsonPropertyName("typeOrState")] string TypeOrState,
    [property: JsonPropertyName("requestDate")] string? RequestDate,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// The funnel for one template, mirroring the steps the operator used to perform by hand. Each count
/// is what survived that step, so the drops are the differences between them.
/// </summary>
public sealed record ReturnListFunnel(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("withTracking")] int WithTracking,
    [property: JsonPropertyName("inDateRange")] int InDateRange,
    [property: JsonPropertyName("afterDuplicates")] int AfterDuplicates,

    /// <summary>Equal to <paramref name="AfterDuplicates"/> unless the "only İade" filter is on.</summary>
    [property: JsonPropertyName("afterRequestType")] int AfterRequestType,

    [property: JsonPropertyName("afterExistingReturns")] int AfterExistingReturns,

    /// <summary>Survived the cross-template check and got a full order number out of the orders export.</summary>
    [property: JsonPropertyName("ready")] int Ready);

public sealed record ReturnListData(
    [property: JsonPropertyName("rows")] IReadOnlyList<ReturnListRow> Rows,
    [property: JsonPropertyName("excluded")] IReadOnlyList<ReturnListExcludedRow> Excluded,
    [property: JsonPropertyName("funnels")] IReadOnlyList<ReturnListFunnel> Funnels);
