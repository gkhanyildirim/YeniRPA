using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

/// <summary>One seller row of the monthly target workbook. <see cref="Current"/> is <c>null</c> when
/// the workbook has no revenue figure for the seller yet — such a seller gets no notification.</summary>
public sealed record SellerTargetRow(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("target")] double Target,
    [property: JsonPropertyName("current")] double? Current,
    [property: JsonPropertyName("days")] int? Days = null,
    [property: JsonPropertyName("daysInMonth")] int DaysInMonth = 30);

/// <summary>What the reader found in the workbook.</summary>
public sealed record SellerTargetSheet(string Month, string MonthName, IReadOnlyList<SellerTargetRow> Rows);

/// <summary>One seller, one rendered notification per channel.</summary>
public sealed record SellerTargetMessage(
    string SellerId,
    string SellerName,
    string MailSubject,
    string MailBody,
    string WhatsAppBody,
    IReadOnlyList<string> UnknownPlaceholders,
    bool BelowThreshold);
