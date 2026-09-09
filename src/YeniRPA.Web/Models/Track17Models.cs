namespace YeniRPA.Web.Models;

/// <summary>One order line whose canonicalised shipping company is one of the three carriers this
/// module tracks, together with the tracking number the export carried for it.</summary>
public sealed record Track17Row(string OrderNumber, string TrackingNumber, string ShippingCompanyRaw, string Carrier);

/// <summary>What one uploaded file became: the matched rows, grouped for the operator to read back
/// before the scrape starts. Mirrors <c>ProductStatusIntake</c>'s reasoning — the row count has to be
/// reconcilable with the spreadsheet it came from.</summary>
public sealed record Track17PrepareResult(
    string BatchId,
    int TotalRows,
    IReadOnlyList<Track17Row> MatchedRows,
    IReadOnlyDictionary<string, int> MatchedByCarrier,
    int DistinctTrackingNumbers,
    int BatchCount,
    int SkippedMalformedTracking);

/// <summary>The prepared batch <c>Track17BatchStore</c> holds between <c>prepare</c> and <c>start</c>.</summary>
public sealed record Track17Batch(string BatchId, DateTimeOffset CreatedUtc, IReadOnlyList<Track17Row> Rows);

/// <summary>One delivered order line, ready for the results table.</summary>
public sealed record Track17DeliveredRow(
    string OrderNumber,
    string TrackingNumber,
    string Carrier,
    string LastEventText,
    string? LastEventTime,
    string? DeliveredOn);

/// <summary>What a finished Track 17 run produced.</summary>
public sealed record Track17RunResult(
    DateTimeOffset CompletedUtc,
    int TrackingNumbersChecked,
    IReadOnlyList<Track17DeliveredRow> Delivered,
    int BatchesTotal,
    IReadOnlyList<int> FailedBatches);
