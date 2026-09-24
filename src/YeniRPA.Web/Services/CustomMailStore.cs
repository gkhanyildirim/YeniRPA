using LiteDB;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>The instance surface <see cref="CustomMailStore"/> exposes through DI. The override
/// helpers (<see cref="CustomMailStore.FindOverride"/>, <see cref="CustomMailStore.FindOverrideProblems"/>)
/// stay static — they are pure functions over data the caller already has, same as
/// <see cref="OfferMailStore"/>'s equivalents.</summary>
public interface ICustomMailStore
{
    string FilePath { get; }

    CustomMailFile Load();
    void Save(CustomMailFile file);
}

/// <summary>
/// Owns the addresses the operator entered by hand for sellers the uploaded directory does not cover
/// — in the <c>customMail</c> collection of the shared LiteDB database (<see cref="ILiteDbContext"/>).
///
/// <para>Deliberately smaller than <see cref="OfferMailStore"/>: Custom Mail has no template, no
/// output folder and no CC to remember between runs — the subject, body and CC/BCC are typed fresh
/// for every campaign. The hand-entered addresses are the only thing here that cannot be rebuilt from
/// an upload, which is exactly why they are the one thing worth persisting.</para>
/// </summary>
public sealed class CustomMailStore : ICustomMailStore
{
    const int DocumentId = 1;

    public sealed class Document
    {
        public int Id { get; set; }
        public CustomMailFile Data { get; set; } = null!;
    }

    readonly ILiteCollection<Document> _collection;

    public CustomMailStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        FilePath = context.DatabasePath;
        _collection = context.GetCollection<Document>("customMail");
        _collection.EnsureIndex(x => x.Id, unique: true);
    }

    public string FilePath { get; }

    public CustomMailFile Load()
    {
        var file = _collection.FindById(DocumentId)?.Data;
        return file is null ? new CustomMailFile(null, []) : file with { Overrides = file.Overrides ?? [] };
    }

    public void Save(CustomMailFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var stamped = file with
        {
            UpdatedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            Overrides = file.Overrides ?? []
        };

        _collection.Upsert(new Document { Id = DocumentId, Data = stamped });
    }

    // ---------------------------------------------------------------------
    // Overrides
    // ---------------------------------------------------------------------

    /// <summary>
    /// The hand-entered address for one seller, or <c>null</c> when there is none.
    ///
    /// <para>Matched by <see cref="CustomMailSellerListReader.SellerKey"/> — id first, folded name
    /// otherwise — the same precedence <see cref="SellerMailDirectory.Find"/> applies to the uploaded
    /// directory, so an override entered against a row with an id is not reachable by name alone.</para>
    ///
    /// <para>The <b>last</b> matching row wins, the same rule the save path applies when it collapses
    /// duplicates — see <see cref="OfferMailStore.FindOverride"/>, which this mirrors exactly.</para>
    /// </summary>
    public static string? FindOverride(
        IReadOnlyList<CustomMailOverrideEntry> overrides, string sellerId, string sellerName)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var key = CustomMailSellerListReader.SellerKey(sellerId, sellerName);
        string? found = null;

        foreach (var entry in overrides)
        {
            if (CustomMailSellerListReader.SellerKey(entry.SellerId, entry.SellerName) != key)
                continue;

            var email = SellerMailStore.JoinAddresses(SellerMailStore.SplitAddresses(entry.Email));
            if (email.Length > 0)
                found = email;
        }

        return found;
    }

    /// <summary>Problems that are properties of the saved list rather than of any one lookup, shown
    /// above the editor. Mirrors <see cref="OfferMailStore.FindOverrideProblems"/>.</summary>
    public static IReadOnlyList<string> FindOverrideProblems(IReadOnlyList<CustomMailOverrideEntry> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var warnings = new List<string>();

        foreach (var group in overrides
            .GroupBy(e => CustomMailSellerListReader.SellerKey(e.SellerId, e.SellerName), StringComparer.Ordinal)
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
