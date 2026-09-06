using System.Text.Json;
using System.Text.Json.Serialization;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Web.Services;

/// <summary>
/// Moves every LiteDB-backed store's data to and from one JSON file, so it can travel to a different
/// machine's <c>%LOCALAPPDATA%\YeniRPA\database.db</c> — a different install, not a second connection
/// to the same file, which <see cref="ILiteDbContext"/>'s <c>ConnectionType.Shared</c> already covers.
///
/// <para>Reuses each store's own <c>Load</c>/<c>Save</c> rather than touching LiteDB directly: those
/// are already the safe, atomic way to read or replace one store's document, and duplicating that
/// here would be a second implementation of the same guarantee.</para>
///
/// <para><b>Import replaces a section whole, it does not merge it</b> — the same contract
/// <c>Save</c> has always had on every store. "Add if missing, update if present" in the request that
/// asked for this is exactly what LiteDB's <c>Upsert</c> already does inside each store's
/// <c>Save</c>; there is no per-row conflict to resolve underneath a single settings document.</para>
/// </summary>
public sealed class DatabaseBackupService(
    ISellerGroupStore sellerGroups,
    ITitleRuleStore titleRules,
    ICategoryRuleStore categoryRules,
    ITitleReferenceStore titleReferences,
    IOfferMailStore offerMail,
    IVatMailStore vatMail)
{
    public const int CurrentVersion = 1;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Every store's current document, as one JSON payload.</summary>
    public byte[] Export()
    {
        var backup = new DatabaseBackup(
            CurrentVersion,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            sellerGroups.Load(),
            titleRules.Load(),
            categoryRules.Load(),
            titleReferences.Load(),
            offerMail.Load(),
            vatMail.Load());

        return JsonSerializer.SerializeToUtf8Bytes(backup, JsonOptions);
    }

    /// <summary>
    /// Reads a backup file and writes every section it carries.
    ///
    /// <para>All-or-nothing at the file level: <see cref="JsonSerializer.Deserialize{TValue}(Stream, JsonSerializerOptions?)"/>
    /// either parses the whole payload or throws, so a truncated or hand-edited-into-invalid-JSON
    /// upload writes nothing rather than half the sections. Within a section that does parse, nothing
    /// further is validated here — the same as loading that section from LiteDB directly, which never
    /// re-validated its own contents either. A backup from this app's own Export is exactly that: data
    /// this app already accepted once.</para>
    /// </summary>
    public DatabaseImportResult Import(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        DatabaseBackup? backup;
        try
        {
            backup = JsonSerializer.Deserialize<DatabaseBackup>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The backup file is not valid JSON: {ex.Message}", ex);
        }

        if (backup is null)
            throw new InvalidOperationException("The backup file is empty.");

        var sections = new List<string>();

        if (backup.SellerGroups is not null)
        {
            sellerGroups.Save(backup.SellerGroups);
            sections.Add("Seller / WhatsApp group mapping");
        }

        if (backup.TitleRules is not null)
        {
            titleRules.Save(backup.TitleRules);
            sections.Add("Title Cleaner rule sets");
        }

        if (backup.CategoryRules is not null)
        {
            categoryRules.Save(backup.CategoryRules);
            sections.Add("Title Cleaner category rules");
        }

        if (backup.TitleReferenceLists is not null)
        {
            titleReferences.Save(backup.TitleReferenceLists);
            sections.Add("Title Cleaner reference lists");
        }

        if (backup.OfferMail is not null)
        {
            offerMail.Save(backup.OfferMail);
            sections.Add("Seller Offer Warnings settings");
        }

        if (backup.VatMail is not null)
        {
            vatMail.Save(backup.VatMail);
            sections.Add("Seller VAT Warnings settings");
        }

        if (sections.Count == 0)
        {
            throw new InvalidOperationException(
                "The backup file carries none of this app's known sections — nothing was imported.");
        }

        return new DatabaseImportResult(backup.ExportedUtc, sections);
    }
}
