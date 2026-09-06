using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Web.Services;

/// <summary>
/// Runs once at startup and carries any pre-LiteDB JSON settings file into the shared LiteDB database,
/// store by store.
///
/// <para>Each store, not this class, knows where its own legacy file used to live and what its own
/// JSON shape is — see <c>MigrateLegacyJson</c> on <see cref="ISellerGroupStore"/>,
/// <see cref="ITitleRuleStore"/>, <see cref="ICategoryRuleStore"/>, <see cref="ITitleReferenceStore"/>,
/// <see cref="IOfferMailStore"/> and <see cref="IVatMailStore"/>. This class only orchestrates calling
/// all six and logging what happened; it is safe to run on every startup because each store's own
/// import is a no-op once it already holds a LiteDB document — a JSON file left behind by an old
/// install is read exactly once, on the first run after upgrading, and never again.</para>
///
/// <para><b>Not migrated on purpose:</b> <c>OfferBatchStore</c>, <c>VatBatchStore</c> and
/// <c>ProductStatusStore</c> were never JSON files — they are documented as in-memory-only precisely so
/// a restart invalidates them (a batch or a scrape result surviving a restart would be silently stale,
/// see their own class docs). Moving them into LiteDB would remove that guarantee, so they are left as
/// they are.</para>
/// </summary>
public sealed class JsonToLiteDbMigrator(
    ISellerGroupStore sellerGroups,
    ITitleRuleStore titleRules,
    ICategoryRuleStore categoryRules,
    ITitleReferenceStore titleReferences,
    IOfferMailStore offerMail,
    IVatMailStore vatMail,
    ILogger<JsonToLiteDbMigrator> logger)
{
    /// <summary>Imports every store's legacy JSON file, if any, into LiteDB. Call once at startup,
    /// before the app starts accepting requests.</summary>
    public void Run()
    {
        Migrate("seller/group mapping", sellerGroups.MigrateLegacyJson);
        Migrate("title cleaner rule sets", titleRules.MigrateLegacyJson);
        Migrate("title cleaner category rules", categoryRules.MigrateLegacyJson);
        Migrate("title cleaner reference lists", titleReferences.MigrateLegacyJson);
        Migrate("offer warnings settings", offerMail.MigrateLegacyJson);
        Migrate("VAT warnings settings", vatMail.MigrateLegacyJson);
    }

    void Migrate(string what, Action migrate)
    {
        try
        {
            migrate();
        }
        catch (Exception ex)
        {
            // A failed import must not stop the app from starting: the store still works with an
            // empty LiteDB document, exactly as it would on a machine with no legacy file at all, and
            // the old JSON is left untouched on disk for a human to look at.
            logger.LogWarning(ex, "Could not import the legacy JSON file for {What} into LiteDB.", what);
        }
    }
}
