using System.Text.Json.Serialization;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Web.Models;

/// <summary>
/// Everything <see cref="Services.DatabaseBackupService"/> moves between machines, one field per
/// LiteDB-backed store. Every field is optional on the way in: a backup taken by an older build, or
/// one that only ever touched some of these modules, still imports the sections it does carry.
/// </summary>
public sealed record DatabaseBackup(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("exportedUtc")] string ExportedUtc,
    [property: JsonPropertyName("sellerGroups")] SellerGroupFile? SellerGroups,
    [property: JsonPropertyName("titleRules")] TitleRuleFile? TitleRules,
    [property: JsonPropertyName("categoryRules")] CategoryRuleFile? CategoryRules,
    [property: JsonPropertyName("titleReferenceLists")] TitleReferenceFile? TitleReferenceLists,
    [property: JsonPropertyName("offerMail")] OfferMailFile? OfferMail,
    [property: JsonPropertyName("vatMail")] VatMailFile? VatMail);

/// <summary>What <see cref="Services.DatabaseBackupService.Import"/> actually did, for the panel to
/// report back to the operator.</summary>
public sealed record DatabaseImportResult(
    [property: JsonPropertyName("exportedUtc")] string? ExportedUtc,
    [property: JsonPropertyName("sections")] IReadOnlyList<string> Sections);
