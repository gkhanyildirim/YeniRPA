using System.Text.Json;
using LiteDB;
using YeniRPA.Web.Models;
// LiteDB also declares a JsonSerializer type; this app's JSON is always System.Text.Json's.
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace YeniRPA.Web.Services;

/// <summary>The instance surface <see cref="VatMailStore"/> exposes through DI. The address/CC helpers
/// (<see cref="VatMailStore.NormalizeMinimum"/>, <see cref="VatMailStore.NormalizeCc"/>,
/// <see cref="VatMailStore.FindOverride"/>, <see cref="VatMailStore.FindOverrideProblems"/>) stay
/// static — they are pure functions over data the caller already has.</summary>
public interface IVatMailStore
{
    /// <summary>Where the data now lives — the shared LiteDB file. Kept on the interface because the
    /// settings panel shows it.</summary>
    string FilePath { get; }

    string DefaultOutputFolder { get; }

    VatMailFile Load();
    void Save(VatMailFile file);
    string ResolveOutputFolder(VatMailFile file);

    /// <summary>One-time import from <c>Mail\vat-mails.json</c>, run by
    /// <see cref="JsonToLiteDbMigrator"/> at startup. A no-op once this store already holds a LiteDB
    /// document.</summary>
    void MigrateLegacyJson();
}

/// <summary>
/// Owns the operator's edited subject and body, the addresses they entered by hand for sellers the
/// uploaded list does not cover, and the folder the per-seller workbooks are written to — in the
/// <c>vatMail</c> collection of the shared LiteDB database (<see cref="ILiteDbContext"/>).
///
/// <para>Deliberately a near-copy of <see cref="OfferMailStore"/> rather than a shared base class. The
/// two files hold different shapes and are read by different modules, and a common base would make a
/// fix to one silently change the other — in a place where a wrong row sends one seller's data to a
/// different seller. The one thing they do share is <see cref="SellerMailStore"/>'s address-cell rules,
/// which carry no seller in them and so cannot move a file into the wrong mail.</para>
///
/// <para>The hand-entered addresses are the only data here that cannot be rebuilt from an upload. They
/// used to get their own atomic-write-plus-backup JSON file for exactly that reason; LiteDB's own
/// write-ahead log now gives the same crash-safety without this class hand-rolling it.</para>
/// </summary>
public sealed class VatMailStore : IVatMailStore
{
    const int CurrentVersion = 1;
    const int DocumentId = 1;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public sealed class Document
    {
        public int Id { get; set; }
        public VatMailFile Data { get; set; } = null!;
    }

    readonly ILiteCollection<Document> _collection;

    public VatMailStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA");

        FilePath = context.DatabasePath;
        DefaultOutputFolder = Path.Combine(root, "VatOffers");

        _collection = context.GetCollection<Document>("vatMail");
        _collection.EnsureIndex(x => x.Id, unique: true);
    }

    public string FilePath { get; }

    /// <summary>
    /// Where the generated per-seller workbooks go when the operator has not chosen a folder.
    ///
    /// <para>Under <c>%LOCALAPPDATA%</c> and emphatically not under <c>wwwroot</c>: everything there is
    /// served to the browser and copied into the build output, so a folder of 131 sellers' price and
    /// stock lists placed there would be downloadable by anyone who can reach the app.</para>
    /// </summary>
    public string DefaultOutputFolder { get; }

    public VatMailFile Load()
    {
        var file = _collection.FindById(DocumentId)?.Data;
        if (file is null)
            return Empty();

        // The CC is left exactly as stored, malformed or not: refusing to load the whole settings
        // file over a typo in one informational field would take the hand-entered addresses down
        // with it. NormalizeCc is applied where the value is used — on save and on prepare.
        return file with
        {
            Overrides = file.Overrides ?? [],
            MinOfferCount = NormalizeMinimum(file.MinOfferCount)
        };
    }

    public void Save(VatMailFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var stamped = file with
        {
            Version = CurrentVersion,
            UpdatedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'")
        };

        _collection.Upsert(new Document { Id = DocumentId, Data = stamped });
    }

    /// <summary>The pre-LiteDB location, read once at startup and never again.</summary>
    internal static string LegacyJsonPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YeniRPA", "Mail", "vat-mails.json");

    public void MigrateLegacyJson()
    {
        if (_collection.Count() > 0)
            return;

        var path = LegacyJsonPath();
        if (!File.Exists(path))
            return;

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return;

        VatMailFile? file;
        try
        {
            file = JsonSerializer.Deserialize<VatMailFile>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // A legacy file that no longer parses is not a reason to fail startup: Load() would have
            // refused it under the old code too, and there is nothing here worth carrying over.
            return;
        }

        if (file is not null)
            Save(file);
    }

    /// <summary>
    /// The saved minimum product count, reduced to one value for "no minimum".
    ///
    /// <para>Zero, a negative number and a missing field are three ways of writing the same thing: mail
    /// every seller. Collapsed to <c>null</c> in one place so no caller has to remember to test for all
    /// three — the one that forgot would refuse to mail anybody.</para>
    /// </summary>
    public static int? NormalizeMinimum(int? value) => value is > 0 ? value : null;

    /// <summary>
    /// The CC line, cleaned, or the reason it cannot be used.
    ///
    /// <para>Split, de-duplicated and re-joined by the same three helpers that handle a seller's own
    /// address, so a CC cell behaves exactly like every other address cell in the app. A bad address is
    /// named rather than dropped: silently mailing 130 sellers with no copy going anywhere is the
    /// failure this returns a problem to prevent.</para>
    /// </summary>
    public static (string? Cc, string? Problem) NormalizeCc(string? raw)
    {
        var addresses = SellerMailStore.SplitAddresses(raw);
        if (addresses.Count == 0)
            return (null, null);

        var bad = addresses.FirstOrDefault(a => !SellerMailStore.LooksLikeEmail(a));
        if (bad is not null)
            return (null, $"'{bad}' does not look like an e-mail address.");

        return (SellerMailStore.JoinAddresses(addresses), null);
    }

    /// <summary>The folder the workbooks should be written under: the saved one when set, the default
    /// otherwise.</summary>
    public string ResolveOutputFolder(VatMailFile file) =>
        string.IsNullOrWhiteSpace(file.OutputFolder) ? DefaultOutputFolder : file.OutputFolder.Trim();

    VatMailFile Empty() => new(CurrentVersion, null, null, null, null, null, null, null, []);

    // ---------------------------------------------------------------------
    // Overrides
    // ---------------------------------------------------------------------

    /// <summary>
    /// The hand-entered address for one seller, or <c>null</c> when there is none.
    ///
    /// <para>Matched on the id when the seller has one and on the folded name otherwise — the same
    /// precedence <see cref="SellerGroupMap.Resolve"/> applies, so an address entered against a row
    /// with an id is not reachable by name alone.</para>
    ///
    /// <para>The <b>last</b> matching row wins, which is the same rule the save path applies when it
    /// collapses duplicates. Saving cannot leave two rows for one seller, so this only ever matters
    /// for a file edited by hand — and there the two must not disagree about which row is live.</para>
    /// </summary>
    public static string? FindOverride(
        IReadOnlyList<VatOverrideEntry> overrides, string sellerId, string sellerName)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var key = VatSplitBuilder.SellerKey(sellerId, sellerName);
        string? found = null;

        foreach (var entry in overrides)
        {
            if (VatSplitBuilder.SellerKey(entry.SellerId, entry.SellerName) != key)
                continue;

            var email = SellerMailStore.JoinAddresses(SellerMailStore.SplitAddresses(entry.Email));
            if (email.Length > 0)
                found = email;
        }

        return found;
    }

    /// <summary>
    /// Problems that are properties of the saved list rather than of any one lookup, shown above the
    /// editor.
    /// </summary>
    public static IReadOnlyList<string> FindOverrideProblems(IReadOnlyList<VatOverrideEntry> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var warnings = new List<string>();

        // Saving collapses these, so reaching here means the JSON was edited by hand. Worth saying:
        // only one of the rows is live, and it is not the one nearest the top.
        foreach (var group in overrides
            .GroupBy(e => VatSplitBuilder.SellerKey(e.SellerId, e.SellerName), StringComparer.Ordinal)
            .Where(g => g.Count() > 1))
        {
            var label = group.First().SellerName.Trim();
            warnings.Add(
                $"'{(label.Length > 0 ? label : group.Key)}' has {group.Count()} hand-entered rows. " +
                "Only the last is used — remove the others.");
        }

        foreach (var entry in overrides)
        {
            var bad = SellerMailStore.SplitAddresses(entry.Email)
                .FirstOrDefault(a => !SellerMailStore.LooksLikeEmail(a));

            if (bad is not null)
            {
                var label = entry.SellerName.Trim();
                warnings.Add($"'{bad}' on {(label.Length > 0 ? $"'{label}'" : "a hand-entered row")} does not look like an e-mail address.");
            }
        }

        return warnings;
    }
}
