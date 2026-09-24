using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Stockout Warnings — out-of-stock products from the Partner Manager export,
// filtered by GMV and grouped by seller, ready to be messaged.
//
// Consumed by wwwroot/js/stockout-warnings.js. Follows the same prepare -> messages -> send split
// as Late Order Warnings and Incident Warnings, and resolves sellers through the same shared
// ISellerGroupStore mapping table (the export has no seller id column, only a name — same
// situation Incident Warnings already handles).
// ---------------------------------------------------------------------------

/// <summary>What the operator chose on the prepare form.</summary>
public sealed record StockoutWarningOptions(
    /// <summary>Only products whose GMV is at or above this are eligible. Editable on the panel,
    /// defaults to the value saved in <see cref="SellerGroupFile.StockoutGmvThreshold"/>.</summary>
    double GmvThreshold);

/// <summary>One stockout product line, carrying every field a template placeholder can reference.</summary>
public sealed record StockoutProductLine(
    [property: JsonPropertyName("gtin")] string Gtin,
    [property: JsonPropertyName("productId")] string? ProductId,
    [property: JsonPropertyName("productName")] string? ProductName,
    [property: JsonPropertyName("brand")] string? Brand,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("offerCondition")] string? OfferCondition,
    [property: JsonPropertyName("gmv")] double Gmv,
    [property: JsonPropertyName("soldItemsAccepted")] string SoldItemsAccepted);

/// <summary>
/// One seller's stockout products and the group they get messaged in. A seller with no group stays
/// in the list with <paramref name="GroupName"/> null and <paramref name="MappingProblem"/> set —
/// same "no separate unmapped collection" choice as <c>LateOrderSeller</c>.
/// </summary>
public sealed record StockoutWarningSeller(
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("groupName")] string? GroupName,

    /// <summary>Why no group was resolved: unmapped, blank group, or a conflict in the mapping file.</summary>
    [property: JsonPropertyName("mappingProblem")] string? MappingProblem,

    [property: JsonPropertyName("productCount")] int ProductCount,
    [property: JsonPropertyName("totalGmv")] double TotalGmv,
    [property: JsonPropertyName("products")] IReadOnlyList<StockoutProductLine> Products);

/// <summary>Where the rows in the file went. Each count is a terminal bucket, so they sum to
/// <paramref name="RowsInFile"/> — same accounting style as <c>LateOrderFunnel</c>.</summary>
public sealed record StockoutWarningFunnel(
    [property: JsonPropertyName("rowsInFile")] int RowsInFile,
    [property: JsonPropertyName("missingGtin")] int MissingGtin,
    [property: JsonPropertyName("missingSeller")] int MissingSeller,
    [property: JsonPropertyName("unreadableGmv")] int UnreadableGmv,
    [property: JsonPropertyName("belowThreshold")] int BelowThreshold,
    [property: JsonPropertyName("eligible")] int Eligible,
    [property: JsonPropertyName("sellers")] int Sellers,
    [property: JsonPropertyName("mappedSellers")] int MappedSellers,
    [property: JsonPropertyName("unmappedSellers")] int UnmappedSellers);

public sealed record StockoutWarningData(
    [property: JsonPropertyName("gmvThreshold")] double GmvThreshold,

    /// <summary>Captured once for the whole build, "yyyy-MM-dd HH:mm".</summary>
    [property: JsonPropertyName("referenceTime")] string ReferenceTime,

    [property: JsonPropertyName("sellers")] IReadOnlyList<StockoutWarningSeller> Sellers,
    [property: JsonPropertyName("funnel")] StockoutWarningFunnel Funnel,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);
