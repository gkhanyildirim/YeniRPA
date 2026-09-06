using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

/// <summary>
/// One recorded operation: an automation run (Playwright, Outlook) or a report/upload action.
/// A LiteDB document in its own right — unlike the settings stores, this one is genuinely many rows,
/// which is what makes <c>EnsureIndex</c> on <see cref="TimestampUtc"/> a real index rather than
/// scaffolding.
/// </summary>
public sealed class SystemLog
{
    public int Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Category { get; set; } = "";
    public string Operation { get; set; } = "";

    /// <summary><see cref="SystemLogStatus.Success"/> or <see cref="SystemLogStatus.Error"/>, stored
    /// as text so the LiteDB file reads sensibly if opened by hand.</summary>
    public string Status { get; set; } = "";

    public long DurationMs { get; set; }
    public string? Detail { get; set; }
}

/// <summary>The two values <see cref="SystemLog.Status"/> ever holds.</summary>
public static class SystemLogStatus
{
    public const string Success = "Success";
    public const string Error = "Error";
}

public sealed record SystemLogSummary(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("success")] int Success,
    [property: JsonPropertyName("error")] int Error,
    [property: JsonPropertyName("successRatePercent")] double SuccessRatePercent);

public sealed record SystemLogPage(
    [property: JsonPropertyName("items")] IReadOnlyList<SystemLog> Items,
    [property: JsonPropertyName("totalCount")] int TotalCount);
