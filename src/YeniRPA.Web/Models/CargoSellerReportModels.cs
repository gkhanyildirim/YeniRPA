using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Cargo Seller Report
//
// Matches the cargo-invoice export, grouped by every seller it holds, against the Marketplace
// return/exchange export and the MM Pazaryeri cargo data export, by tracking code, to recover each
// shipment's order number. Run once a month over every seller at once rather than one at a time, so
// the output is one workbook per seller, zipped together. Consumed by wwwroot/js/cargo-seller-report.js.
// ---------------------------------------------------------------------------

/// <summary>What <c>analyze</c> found in the uploaded cargo-invoice file.</summary>
public sealed record CargoSellerReportAnalyzeResult(
    [property: JsonPropertyName("headers")] IReadOnlyList<string> Headers,
    [property: JsonPropertyName("trackingColumn")] string TrackingColumn,
    [property: JsonPropertyName("sellerColumn")] string? SellerColumn,
    [property: JsonPropertyName("sellerColumnResolved")] bool SellerColumnResolved,
    [property: JsonPropertyName("sellers")] IReadOnlyList<string> Sellers);

/// <summary>One row of the selected seller's cargo records, with its resolved order number.</summary>
public sealed record CargoSellerReportRow(
    [property: JsonPropertyName("cells")] IReadOnlyList<string> Cells,
    [property: JsonPropertyName("trackingCode")] string TrackingCode,
    [property: JsonPropertyName("orderNumber")] string OrderNumber,
    [property: JsonPropertyName("matchSource")] string MatchSource,
    [property: JsonPropertyName("matchStatus")] string MatchStatus);

/// <summary>A cargo row whose tracking code was not found in either lookup file.</summary>
public sealed record CargoSellerReportUnmatchedRow(
    [property: JsonPropertyName("trackingCode")] string TrackingCode,
    [property: JsonPropertyName("seller")] string Seller,
    [property: JsonPropertyName("cells")] IReadOnlyList<string> Cells);

/// <summary>A tracking code that resolved to more than one distinct order number in one lookup file.</summary>
public sealed record CargoSellerReportConflictRow(
    [property: JsonPropertyName("trackingCode")] string TrackingCode,
    [property: JsonPropertyName("matchSource")] string MatchSource,
    [property: JsonPropertyName("orderNumbers")] IReadOnlyList<string> OrderNumbers);

/// <summary>The counts and totals shown on screen and on the report's Özet sheet.</summary>
public sealed record CargoSellerReportSummary(
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("totalRecords")] int TotalRecords,
    [property: JsonPropertyName("matchedViaMarketplace")] int MatchedViaMarketplace,
    [property: JsonPropertyName("matchedViaMmCargoData")] int MatchedViaMmCargoData,
    [property: JsonPropertyName("totalMatched")] int TotalMatched,
    [property: JsonPropertyName("unmatched")] int Unmatched,
    [property: JsonPropertyName("conflicted")] int Conflicted,
    [property: JsonPropertyName("totalCargoFee")] decimal? TotalCargoFee,
    [property: JsonPropertyName("totalVat")] decimal? TotalVat,
    [property: JsonPropertyName("totalCargoFeePlusVat")] decimal? TotalCargoFeePlusVat,
    [property: JsonPropertyName("totalInvoiceAmount")] decimal? TotalInvoiceAmount,
    [property: JsonPropertyName("delivered")] int? Delivered,
    [property: JsonPropertyName("returned")] int? Returned,
    [property: JsonPropertyName("canceled")] int? Canceled);

/// <summary>
/// One seller's computed report, held by <see cref="Services.CargoSellerReportStore"/> — as one of a
/// list, one per seller found in the cargo-invoice file — between <c>generate</c> (which builds the
/// list) and <c>download</c> (which reads it back to zip one workbook per seller).
/// </summary>
public sealed record CargoSellerReportResult(
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("headers")] IReadOnlyList<string> Headers,
    [property: JsonPropertyName("trackingColumnIndex")] int TrackingColumnIndex,
    [property: JsonPropertyName("optionalColumns")] IReadOnlyDictionary<string, int> OptionalColumns,
    [property: JsonPropertyName("rows")] IReadOnlyList<CargoSellerReportRow> Rows,
    [property: JsonPropertyName("unmatched")] IReadOnlyList<CargoSellerReportUnmatchedRow> Unmatched,
    [property: JsonPropertyName("conflicts")] IReadOnlyList<CargoSellerReportConflictRow> Conflicts,
    [property: JsonPropertyName("summary")] CargoSellerReportSummary Summary);

/// <summary>One row of the per-seller review table shown after <c>generate</c>, before download.</summary>
public sealed record CargoSellerReportSellerTotal(
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("totalRecords")] int TotalRecords,
    [property: JsonPropertyName("totalMatched")] int TotalMatched,
    [property: JsonPropertyName("unmatched")] int Unmatched,
    [property: JsonPropertyName("conflicted")] int Conflicted);

/// <summary>The counts shown on screen right after <c>generate</c>, across every seller at once.</summary>
public sealed record CargoSellerReportBatchSummary(
    [property: JsonPropertyName("sellerCount")] int SellerCount,
    [property: JsonPropertyName("totalRecords")] int TotalRecords,
    [property: JsonPropertyName("matchedViaMarketplace")] int MatchedViaMarketplace,
    [property: JsonPropertyName("matchedViaMmCargoData")] int MatchedViaMmCargoData,
    [property: JsonPropertyName("totalMatched")] int TotalMatched,
    [property: JsonPropertyName("unmatched")] int Unmatched,
    [property: JsonPropertyName("conflicted")] int Conflicted,
    [property: JsonPropertyName("sellers")] IReadOnlyList<CargoSellerReportSellerTotal> Sellers);
