using System.Text.Json;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Owns <c>%LOCALAPPDATA%\YeniRPA\Mail\vat-mails.json</c> — the operator's edited subject and body,
/// the addresses they entered by hand for sellers the uploaded list does not cover, and the folder
/// the per-seller workbooks are written to.
///
/// <para>Deliberately a near-copy of <see cref="SellerMailStore"/> rather than a shared base class,
/// for the reason that class already records: the two files hold different shapes and are read by
/// different modules, and a common base would make a fix to one silently change the other — in a
/// place where a wrong row sends one seller's data to a different seller.</para>
///
/// <para>The hand-entered addresses are the only data here that cannot be rebuilt from an upload,
/// which is why <see cref="Save"/> keeps a backup generation and replaces the file atomically.</para>
/// </summary>
public sealed class VatMailStore
{
    const int CurrentVersion = 1;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Load and save are one operation each; a singleton reachable from several controller
    /// actions needs them not to interleave.</summary>
    readonly object _sync = new();

    public VatMailStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA");

        var directory = Path.Combine(root, "Mail");
        Directory.CreateDirectory(directory);

        FilePath = Path.Combine(directory, "vat-mails.json");
        BackupPath = FilePath + ".bak";

        DefaultOutputFolder = Path.Combine(root, "VatOffers");
    }

    public string FilePath { get; }
    public string BackupPath { get; }

    /// <summary>
    /// Where the generated per-seller workbooks go when the operator has not chosen a folder.
    ///
    /// <para>Under <c>%LOCALAPPDATA%</c> and emphatically not under <c>wwwroot</c>: everything there is
    /// served to the browser and copied into the build output, so a folder of 131 sellers' price and
    /// stock lists placed there would be downloadable by anyone who can reach the app.</para>
    /// </summary>
    public string DefaultOutputFolder { get; }

    /// <summary>Reads the file every call rather than caching it — a few KB, and a cache would go
    /// stale the moment someone edited the JSON by hand.</summary>
    public VatMailFile Load()
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
                    $"The VAT warning settings could not be read from {FilePath}: {ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(json))
                return Empty();

            VatMailFile? file;
            try
            {
                file = JsonSerializer.Deserialize<VatMailFile>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                // Never silently start over: that would look identical to "the addresses vanished".
                throw new InvalidOperationException(
                    $"The VAT warning settings at {FilePath} are not valid JSON ({ex.Message}). " +
                    $"The previous version is kept at {BackupPath}.", ex);
            }

            if (file is null)
                return Empty();

            return file with { Overrides = file.Overrides ?? [] };
        }
    }

    /// <summary>Writes to a temp file and moves it over the original, keeping one backup generation.
    /// A torn write here would destroy the hand-entered addresses, which exist nowhere else.</summary>
    public void Save(VatMailFile file)
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

    /// <summary>The folder the workbooks should be written under: the saved one when set, the default
    /// otherwise.</summary>
    public string ResolveOutputFolder(VatMailFile file) =>
        string.IsNullOrWhiteSpace(file.OutputFolder) ? DefaultOutputFolder : file.OutputFolder.Trim();

    VatMailFile Empty() => new(CurrentVersion, null, null, null, null, []);

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
    /// editor — the counterpart of <see cref="SellerMailStore.FindTableProblems"/>.
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
