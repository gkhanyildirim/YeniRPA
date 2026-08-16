using System.Text;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>The outcome of looking one seller up. Exactly one side is ever set.</summary>
public readonly record struct SellerGroupMatch(string? GroupName, string? Problem);

/// <summary>
/// An immutable snapshot of the seller → WhatsApp group mapping, built once per request from
/// <see cref="SellerGroupStore"/>. Pure and IO-free, so <see cref="LateOrderBuilder"/> stays as
/// testable as the other report builders.
///
/// <para><b>There is no fuzzy matching here, and none may ever be added.</b> Not Levenshtein, not
/// <c>Contains</c>, not "closest group". The failure mode of an 85%-similar match is posting one
/// seller's overdue order list — numbers, dates, volumes — into a <em>different</em> seller's group.
/// That is a competitor data leak delivered by our own automation, and it would look exactly like a
/// working system right up until someone complained. Every ambiguity below is reported as a problem
/// for the operator to resolve, never guessed at.</para>
/// </summary>
public sealed class SellerGroupMap
{
    readonly Dictionary<string, SellerGroupEntry> _byId;
    readonly Dictionary<string, SellerGroupEntry> _byName;
    readonly HashSet<string> _duplicateIds;
    readonly HashSet<string> _duplicateNames;

    /// <summary>Every group name in the file, folded for the send endpoint's allow-list check.</summary>
    readonly HashSet<string> _groupNames;

    SellerGroupMap(
        Dictionary<string, SellerGroupEntry> byId,
        Dictionary<string, SellerGroupEntry> byName,
        HashSet<string> duplicateIds,
        HashSet<string> duplicateNames,
        HashSet<string> groupNames,
        IReadOnlyList<string> loadWarnings)
    {
        _byId = byId;
        _byName = byName;
        _duplicateIds = duplicateIds;
        _duplicateNames = duplicateNames;
        _groupNames = groupNames;
        LoadWarnings = loadWarnings;
    }

    /// <summary>Problems that are properties of the file rather than of any one lookup — duplicate
    /// ids, duplicate names — detected once here and shown above the mapping editor.</summary>
    public IReadOnlyList<string> LoadWarnings { get; }

    public static SellerGroupMap FromEntries(IEnumerable<SellerGroupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var byId = new Dictionary<string, SellerGroupEntry>(StringComparer.Ordinal);
        var byName = new Dictionary<string, SellerGroupEntry>(StringComparer.Ordinal);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateNames = new HashSet<string>(StringComparer.Ordinal);
        var groupNames = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        foreach (var entry in entries)
        {
            var id = NormalizeSellerId(entry.SellerId);
            var name = FoldName(entry.SellerName);
            var group = entry.GroupName.Trim();

            if (group.Length > 0)
                groupNames.Add(group);

            if (id.Length > 0)
            {
                if (!byId.TryAdd(id, entry) && duplicateIds.Add(id))
                    warnings.Add($"Seller id '{id}' is mapped more than once. Rows with this id resolve to no group until one is removed.");
            }

            if (name.Length > 0)
            {
                if (!byName.TryAdd(name, entry) && duplicateNames.Add(name))
                    warnings.Add($"Seller name '{entry.SellerName.Trim()}' is mapped more than once. Rows matched by name resolve to no group until one is removed.");
            }
        }

        return new SellerGroupMap(byId, byName, duplicateIds, duplicateNames, groupNames, warnings);
    }

    /// <summary>True when this exact group name appears in the mapping file. The send endpoint uses
    /// it so the only WhatsApp groups this app can ever post to are ones the operator typed in.</summary>
    public bool HasGroup(string groupName) => _groupNames.Contains((groupName ?? "").Trim());

    /// <summary>
    /// Seller id wins over seller name: ids are stable, while a display name changes whenever a
    /// seller edits their storefront. Anything ambiguous returns a problem, never a group.
    /// </summary>
    public SellerGroupMatch Resolve(string sellerId, string sellerName)
    {
        var id = NormalizeSellerId(sellerId ?? "");
        var name = FoldName(sellerName ?? "");

        if (id.Length > 0 && _duplicateIds.Contains(id))
            return new SellerGroupMatch(null, $"Mapping conflict: seller id '{id}' is mapped more than once.");

        var idEntry = id.Length > 0 && _byId.TryGetValue(id, out var foundById) ? foundById : null;

        // The name path is only consulted when it can add something: either the id found nothing, or
        // it found something and we want to know whether the name disagrees.
        SellerGroupEntry? nameEntry = null;
        if (name.Length > 0)
        {
            if (_duplicateNames.Contains(name))
            {
                // A duplicated name only blocks the lookup when the name is what we would fall back on.
                if (idEntry is null)
                    return new SellerGroupMatch(null, $"Mapping conflict: seller name '{sellerName}' is mapped more than once.");
            }
            else if (_byName.TryGetValue(name, out var foundByName))
            {
                nameEntry = foundByName;
            }
        }

        if (idEntry is not null && nameEntry is not null &&
            !string.Equals(idEntry.GroupName.Trim(), nameEntry.GroupName.Trim(), StringComparison.Ordinal))
        {
            return new SellerGroupMatch(null,
                $"Mapping conflict: seller id '{id}' points at '{idEntry.GroupName.Trim()}' but the name '{sellerName}' points at '{nameEntry.GroupName.Trim()}'.");
        }

        var entry = idEntry ?? nameEntry;
        if (entry is null)
            return new SellerGroupMatch(null, "No WhatsApp group is mapped for this seller.");

        var group = entry.GroupName.Trim();
        if (group.Length == 0)
            return new SellerGroupMatch(null, "Mapped, but no WhatsApp group name was entered.");

        return new SellerGroupMatch(group, null);
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// The orders export writes seller ids as floats ("11616.0"); the mapping table holds "11616".
    ///
    /// Deliberately a copy of <c>ReturnListBuilder.NormalizeSellerId</c> rather than a shared helper:
    /// the README records the Create Return path as a verified verbatim port, and wiring a second
    /// consumer into it would mean a fix made here silently moved that module's figures. Two five-line
    /// methods that point at each other are the cheaper trade.
    /// </summary>
    public static string NormalizeSellerId(string raw)
    {
        var value = (raw ?? "").Trim();
        var dot = value.IndexOf('.');
        return dot >= 0 ? value[..dot] : value;
    }

    /// <summary>
    /// Folds a seller name to a comparison key: whitespace collapsed, the Turkish i-family folded to
    /// a plain <c>i</c>, lowercased.
    ///
    /// <para>The i fold is the subtle part and it is not optional — this export carries
    /// <c>FırsatKurdu</c> and <c>Altınkoza Teknolojim</c>. No built-in comparison gets them right:
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> maps <c>I</c>↔<c>i</c> but neither
    /// <c>ı</c>↔<c>I</c> nor <c>İ</c>↔<c>i</c>, so an operator who types FIRSATKURDU misses;
    /// <c>InvariantCultureIgnoreCase</c> treats <c>ı</c> as a separate letter and misses the same way;
    /// and a <c>tr-TR</c> comparison fixes <c>ı</c>↔<c>I</c> only by breaking <c>i</c>↔<c>I</c>, so
    /// BIZBIZ-E would then stop matching Bizbiz-E. Folding all four to one <c>i</c> is the only rule
    /// under which every human spelling of these names collides.</para>
    ///
    /// <para>This is a deliberate widening: two genuinely different sellers separated only by dotted
    /// vs dotless i would now collide. That is reported as a duplicate-name conflict rather than
    /// resolved to one of them, so the widening cannot silently misroute a message.</para>
    /// </summary>
    public static string FoldName(string raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0)
            return "";

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value)
        {
            // U+0307 is the combining dot above that ICU leaves behind when it lowercases "İ".
            if (ch == '̇')
                continue;

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch is 'İ' or 'I' or 'ı' or 'i' ? 'i' : ch);
        }

        return builder.ToString().ToLowerInvariant();
    }
}
