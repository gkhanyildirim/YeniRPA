using System.Text.Json;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Owns <c>%LOCALAPPDATA%\YeniRPA\Mail\offer-warnings.json</c> — the operator's edited subject and
/// body, the addresses they entered by hand for sellers the uploaded list does not cover, and the
/// folder the per-seller workbooks are written to.
///
/// <para>Deliberately a near-copy of <see cref="VatMailStore"/> rather than a shared base class, for
/// the reason that class already records: the two files hold different shapes and are read by different
/// modules, and a common base would make a fix to one silently change the other — in a place where a
/// wrong row sends one seller's data to a different seller.</para>
///
/// <para>The hand-entered addresses are the only data here that cannot be rebuilt from an upload,
/// which is why <see cref="Save"/> keeps a backup generation and replaces the file atomically.</para>
/// </summary>
public sealed class OfferMailStore
{
    const int CurrentVersion = 1;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Load and save are one operation each; a singleton reachable from several controller
    /// actions needs them not to interleave.</summary>
    readonly object _sync = new();

    public OfferMailStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA");

        var directory = Path.Combine(root, "Mail");
        Directory.CreateDirectory(directory);

        FilePath = Path.Combine(directory, "offer-warnings.json");
        BackupPath = FilePath + ".bak";

        DefaultOutputFolder = Path.Combine(root, "OfferLeadTimes");
    }

    public string FilePath { get; }
    public string BackupPath { get; }

    /// <summary>
    /// Where the generated per-seller workbooks go when the operator has not chosen a folder.
    ///
    /// <para>Under <c>%LOCALAPPDATA%</c> and emphatically not under <c>wwwroot</c>: everything there is
    /// served to the browser and copied into the build output, so a folder of 287 sellers' offer lists
    /// placed there would be downloadable by anyone who can reach the app.</para>
    /// </summary>
    public string DefaultOutputFolder { get; }

    /// <summary>Reads the file every call rather than caching it — a few KB, and a cache would go
    /// stale the moment someone edited the JSON by hand.</summary>
    public OfferMailFile Load()
    {
        lock (_sync)
        {
            if (!File.Exists(FilePath))
                return Empty();

            string json;
            try
            {
                json = File.ReadAllText(FilePath);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"The offer warning settings could not be read from {FilePath}: {ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(json))
                return Empty();

            OfferMailFile? file;
            try
            {
                file = JsonSerializer.Deserialize<OfferMailFile>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                // Never silently start over: that would look identical to "the addresses vanished".
                throw new InvalidOperationException(
                    $"The offer warning settings at {FilePath} are not valid JSON ({ex.Message}). " +
                    $"The previous version is kept at {BackupPath}.", ex);
            }

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
    }

    /// <summary>Writes to a temp file and moves it over the original, keeping one backup generation.
    /// A torn write here would destroy the hand-entered addresses, which exist nowhere else.</summary>
    public void Save(OfferMailFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var stamped = file with
        {
            Version = CurrentVersion,
            UpdatedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'")
        };

        lock (_sync)
        {
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(stamped, JsonOptions));

            if (File.Exists(FilePath))
                File.Copy(FilePath, BackupPath, overwrite: true);

            File.Move(tempPath, FilePath, overwrite: true);
        }
    }

    /// <summary>
    /// The saved minimum offer count, reduced to one value for "no minimum".
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

    OfferMailFile Empty() => new(CurrentVersion, null, null, null, null, null, null, null, []);

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
