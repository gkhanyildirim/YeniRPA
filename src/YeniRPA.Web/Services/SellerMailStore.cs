namespace YeniRPA.Web.Services;

/// <summary>
/// How an e-mail address cell is read, compared and written back, everywhere in this app.
///
/// <para>Named for what it used to be. Until Seller Offer Warnings moved to the two-upload model this
/// class also owned <c>seller-mails.json</c>, the hand-built seller → e-mail → attachment mapping that
/// module ran on. That table no longer exists — the app computes the pairing from the export instead —
/// and the persistence went with it. What is left is the part every module still needs: one rule for
/// splitting a cell that holds several addresses, one rule for joining them back, and one shallow
/// validity check.</para>
///
/// <para>These stayed here rather than moving into either mail store because both stores use them, and
/// the two stores are deliberately kept from sharing code in every other respect — see
/// <see cref="VatMailStore"/>. An address cell is the one thing they may safely agree on: it is a
/// string-handling rule with no seller in it, so no change to it can move a file into a different
/// seller's mail.</para>
/// </summary>
public static class SellerMailStore
{
    /// <summary>The comparison key for an address: trimmed and lowercased. Not a validity check —
    /// see <see cref="LooksLikeEmail"/> for that.</summary>
    public static string NormalizeEmail(string raw) => (raw ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Splits one address cell into the addresses it holds.
    ///
    /// <para>A seller often has several users in the Mirakl back office and all of them belong on one
    /// mail, so the cell holds a list. Both <c>;</c> and <c>,</c> separate, because a cell filled in by
    /// hand from Outlook uses the first and one pasted out of a spreadsheet often uses the second.</para>
    ///
    /// <para>Order is preserved and repeats <em>within the cell</em> are dropped: the same person
    /// listed twice on one seller would otherwise appear twice in the To line. Repeats across different
    /// sellers are a different question and are deliberately left alone — one agency running three
    /// storefronts is normal, and each of those mails carries a different seller's list.</para>
    /// </summary>
    public static IReadOnlyList<string> SplitAddresses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var addresses = new List<string>();

        foreach (var part in raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(NormalizeEmail(part)))
                addresses.Add(part);
        }

        return addresses;
    }

    /// <summary>How a list of addresses is written back into one cell, and into Outlook's To line.</summary>
    public static string JoinAddresses(IEnumerable<string> addresses) => string.Join("; ", addresses);

    /// <summary>
    /// Deliberately shallow: one <c>@</c>, something either side, no whitespace, a dot in the domain.
    ///
    /// <para>Not <see cref="System.Net.Mail.MailAddress"/>, which accepts <c>"a b"@c</c> and display
    /// names — shapes that are legal RFC 5322 and always a typo in a spreadsheet cell. The check exists
    /// to catch a mangled cell before Outlook is asked to send to it, not to be a parser.</para>
    /// </summary>
    public static bool LooksLikeEmail(string raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length < 5 || value.Any(char.IsWhiteSpace))
            return false;

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
            return false;

        var domain = value[(at + 1)..];
        var dot = domain.IndexOf('.');
        return dot > 0 && dot < domain.Length - 1;
    }
}
