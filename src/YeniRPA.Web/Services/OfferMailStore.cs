using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiteDB;
using YeniRPA.Web.Models;
// LiteDB also declares a JsonSerializer type; this app's JSON is always System.Text.Json's.
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace YeniRPA.Web.Services;

/// <summary>The instance surface <see cref="OfferMailStore"/> exposes through DI. The address/CC
/// helpers (<see cref="OfferMailStore.NormalizeMinimum"/>, <see cref="OfferMailStore.NormalizeCc"/>,
/// <see cref="OfferMailStore.FindOverride"/>, <see cref="OfferMailStore.FindOverrideProblems"/>) stay
/// static — they are pure functions over data the caller already has.</summary>
public interface IOfferMailStore
{
    /// <summary>Where the data now lives — the shared LiteDB file. Kept on the interface because the
    /// settings panel shows it.</summary>
    string FilePath { get; }

    string DefaultOutputFolder { get; }

    OfferMailFile Load();
    void Save(OfferMailFile file);
    string ResolveOutputFolder(OfferMailFile file);

    /// <summary>One-time import from <c>Mail\offer-warnings.json</c>, run by
    /// <see cref="JsonToLiteDbMigrator"/> at startup. A no-op once this store already holds a LiteDB
    /// document.</summary>
    void MigrateLegacyJson();
}

/// <summary>
/// Owns the operator's edited subject and body, the addresses they entered by hand for sellers the
/// uploaded list does not cover, and the folder the per-seller workbooks are written to — in the
/// <c>offerMail</c> collection of the shared LiteDB database (<see cref="ILiteDbContext"/>).
///
/// <para>Deliberately a near-copy of <see cref="VatMailStore"/> rather than a shared base class, for
/// the reason that class already records: the two files hold different shapes and are read by different
/// modules, and a common base would make a fix to one silently change the other — in a place where a
/// wrong row sends one seller's data to a different seller.</para>
///
/// <para>The hand-entered addresses are the only data here that cannot be rebuilt from an upload. They
/// used to get their own atomic-write-plus-backup JSON file for exactly that reason; LiteDB's own
/// write-ahead log now gives the same crash-safety without this class hand-rolling it.</para>
/// </summary>
public sealed class OfferMailStore : IOfferMailStore
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
        public OfferMailFile Data { get; set; } = null!;
    }

    readonly ILiteCollection<Document> _collection;

    public OfferMailStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA");

        FilePath = context.DatabasePath;
        DefaultOutputFolder = Path.Combine(root, "OfferLeadTimes");

        _collection = context.GetCollection<Document>("offerMail");
        _collection.EnsureIndex(x => x.Id, unique: true);
    }

    public string FilePath { get; }

    /// <summary>
    /// Where the generated per-seller workbooks go when the operator has not chosen a folder.
    ///
    /// <para>Under <c>%LOCALAPPDATA%</c> and emphatically not under <c>wwwroot</c>: everything there is
    /// served to the browser and copied into the build output, so a folder of 287 sellers' offer lists
    /// placed there would be downloadable by anyone who can reach the app.</para>
    /// </summary>
    public string DefaultOutputFolder { get; }

    public OfferMailFile Load()
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
            MinOfferCount = NormalizeMinimum(file.MinOfferCount),
            LeadTimes = NormalizeLeadTimes(file.LeadTimes),
            SubjectTemplate = DropSuperseded(file.SubjectTemplate, OfferMailBuilder.SupersededSubjectTemplates),
            BodyTemplate = DropSuperseded(file.BodyTemplate, OfferMailBuilder.SupersededBodyTemplates)
        };
    }

    public void Save(OfferMailFile file)
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
        "YeniRPA", "Mail", "offer-warnings.json");

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

        OfferMailFile? file;
        try
        {
            file = JsonSerializer.Deserialize<OfferMailFile>(json, JsonOptions);
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
    /// The saved minimum offer count, reduced to one value for "no minimum".
    ///
    /// <para>Zero, a negative number and a missing field are three ways of writing the same thing: mail
    /// every seller. Collapsed to <c>null</c> in one place so no caller has to remember to test for all
    /// three — the one that forgot would refuse to mail anybody.</para>
    /// </summary>
    public static int? NormalizeMinimum(int? value) => value is > 0 ? value : null;

    /// <summary>The most days a lead time may name, and the most days one run may warn about. Both are
    /// sanity bounds on a hand-typed box, not marketplace rules.</summary>
    public const int MaxLeadTime = 30;
    public const int MaxLeadTimeCount = 6;

    /// <summary>
    /// The lead times to warn about, as typed by the operator, or the reason the box cannot be used.
    ///
    /// <para>Follows <see cref="NormalizeCc"/>'s shape — a cleaned value or a stated problem — and for
    /// the same reason: this is the moment the operator is looking at what they typed. Discovering
    /// after a build that the filter was empty, or that "1O" was read as nothing, is the wrong moment.</para>
    ///
    /// <para>Blank is <c>null</c> and not a problem: it means "use the default", which is what an
    /// operator who never opened this box has always had.</para>
    /// </summary>
    public static (int[]? LeadTimes, string? Problem) NormalizeLeadTimes(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return (null, null);

        var parts = text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var days = new List<int>(parts.Length);

        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day))
                return (null, $"'{part}' is not a whole number of days.");

            if (day < 0 || day > MaxLeadTime)
                return (null, $"'{part}' is not a lead time between 0 and {MaxLeadTime} days.");

            // Silently deduplicated rather than refused: "0, 0, 1" says the same thing as "0, 1", and
            // there is nothing for the operator to decide about it.
            if (!days.Contains(day))
                days.Add(day);
        }

        if (days.Count == 0)
            return (null, "No lead time was recognised in that.");

        if (days.Count > MaxLeadTimeCount)
        {
            return (null,
                $"{days.Count} lead times is more than the {MaxLeadTimeCount} this module warns about " +
                "at once. Warning about most of the export is not a warning.");
        }

        days.Sort();
        return ([.. days], null);
    }

    /// <summary>The stored lead times, cleaned the same way, or <c>null</c> for "use the default".</summary>
    public static int[]? NormalizeLeadTimes(int[]? stored)
    {
        if (stored is null || stored.Length == 0)
            return null;

        var days = stored
            .Where(d => d >= 0 && d <= MaxLeadTime)
            .Distinct()
            .Order()
            .Take(MaxLeadTimeCount)
            .ToArray();

        return days.Length > 0 ? days : null;
    }

    /// <summary>The lead times a run should use: the operator's when they have set any, the default
    /// otherwise. One place, so no caller can forget the fallback and filter on nothing.</summary>
    public static IReadOnlyList<int> ResolveLeadTimes(OfferMailFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return NormalizeLeadTimes(file.LeadTimes) ?? OfferSplitBuilder.DefaultWarnedLeadTimes;
    }

    /// <summary>
    /// A saved template that is byte-for-byte one of this module's own earlier defaults, dropped so the
    /// current default takes its place.
    ///
    /// <para>Saving the panel's boxes stores the default text rather than a null, so an operator who
    /// never edited a word still ends up with a copy of it frozen in their settings file. When the
    /// default then changes — as it did when the two fixed lead-time lines became one breakdown — that
    /// frozen copy would keep rendering a placeholder this build no longer knows, and the mail would
    /// leave with <c>{leadTime2}</c> in it.</para>
    ///
    /// <para>Only an exact match is dropped. A template the operator changed by so much as a character
    /// is theirs, and is returned untouched.</para>
    /// </summary>
    internal static string? DropSuperseded(string? saved, IReadOnlyList<string> superseded)
    {
        if (string.IsNullOrWhiteSpace(saved))
            return saved;

        var text = NormalizeLineEndings(saved);

        return superseded.Any(old => string.Equals(NormalizeLineEndings(old), text, StringComparison.Ordinal))
            ? null
            : saved;
    }

    /// <summary>
    /// Collapses any run of one or more <c>\r</c> — optionally followed by <c>\n</c> — to a single
    /// <c>\n</c>. A plain <c>"\r\n" → "\n"</c> replace is not enough: an editor that blindly inserts a
    /// <c>\r</c> before every <c>\n</c> without checking whether one is already there turns an existing
    /// <c>\r\n</c> into <c>\r\r\n</c>, and a two-step <c>Replace</c> leaves that as <c>\n\n</c> instead of
    /// <c>\n</c> — the same text then compares as different, which is the one case this exists to catch.
    /// </summary>
    static string NormalizeLineEndings(string text) => Regex.Replace(text, "\r+\n?", "\n");

    /// <summary>
    /// The CC line, cleaned, or the reason it cannot be used.
    ///
    /// <para>Split, de-duplicated and re-joined by the same three helpers that handle a seller's own
    /// address, so a CC cell behaves exactly like every other address cell in the app. A bad address is
    /// named rather than dropped: silently mailing 287 sellers with no copy going anywhere is the
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
    public string ResolveOutputFolder(OfferMailFile file) =>
        string.IsNullOrWhiteSpace(file.OutputFolder) ? DefaultOutputFolder : file.OutputFolder.Trim();

    OfferMailFile Empty() => new(CurrentVersion, null, null, null, null, null, null, null, null, []);

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
    /// collapses duplicates. Saving cannot leave two rows for one seller, so this only ever matters for
    /// a file edited by hand — and there the two must not disagree about which row is live.</para>
    /// </summary>
    public static string? FindOverride(
        IReadOnlyList<OfferOverrideEntry> overrides, string sellerId, string sellerName)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var key = OfferSplitBuilder.SellerKey(sellerId, sellerName);
        string? found = null;

        foreach (var entry in overrides)
        {
            if (OfferSplitBuilder.SellerKey(entry.SellerId, entry.SellerName) != key)
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
    public static IReadOnlyList<string> FindOverrideProblems(IReadOnlyList<OfferOverrideEntry> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var warnings = new List<string>();

        // Saving collapses these, so reaching here means the JSON was edited by hand. Worth saying:
        // only one of the rows is live, and it is not the one nearest the top.
        foreach (var group in overrides
            .GroupBy(e => OfferSplitBuilder.SellerKey(e.SellerId, e.SellerName), StringComparer.Ordinal)
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
                warnings.Add(
                    $"'{bad}' on {(label.Length > 0 ? $"'{label}'" : "a hand-entered row")} does not " +
                    "look like an e-mail address.");
            }
        }

        return warnings;
    }
}
