namespace YeniRPA.Web.Services;

/// <summary>
/// Normalized Levenshtein similarity ratio for comparing two shipping addresses, used only to flag
/// two orders in the same upload as "fraud-suspect — highly similar shipping address" for a human
/// fraud reviewer to look at.
///
/// <para><b>This is a deliberate, approved exception</b> to the repo-wide ban on fuzzy/approximate
/// string matching stated in <see cref="CarrierNames"/>, <see cref="SellerGroupMap"/> and
/// <see cref="TitleCleaner.AttributeMatcher"/>. Those bans exist because a wrong automatic match at
/// those call sites silently merges data or deletes title text — an invisible failure that looks
/// like a working report. Here the output only ever populates an advisory dashboard row that a
/// person reviews before any action is taken; nothing here merges records, changes routing, or
/// otherwise acts on the result automatically. If this similarity output is ever wired into
/// something that acts without a human in the loop, this exception no longer holds and must be
/// re-reviewed.</para>
/// </summary>
public static class AddressSimilarity
{
    /// <summary>
    /// Concatenates street 1 + street 2 + zip + city + country into one comparison string and folds
    /// it with <see cref="CarrierNames.Fold"/> — the same case/diacritic/punctuation/whitespace
    /// folding already used for carrier names, reused here rather than inventing a second folding
    /// utility (it operates on plain text and has no carrier-specific behavior).
    /// </summary>
    public static string Normalize(string street1, string street2, string zip, string city, string country) =>
        CarrierNames.Fold(string.Join(' ',
            new[] { street1, street2, zip, city, country }.Where(s => !string.IsNullOrWhiteSpace(s))));

    /// <summary>
    /// Similarity ratio in [0,1]: 1 - (edit distance / longer string's length). Both inputs are
    /// expected to already be normalized via <see cref="Normalize"/>. Two empty strings are treated
    /// as maximally dissimilar (0), not identical — callers must not present two blank addresses as
    /// a match.
    /// </summary>
    public static double Ratio(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;

        var distance = LevenshteinDistance(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    /// <summary>Classic O(n*m) edit distance, two-row DP (no need for the full matrix — addresses
    /// are short strings, at most a couple hundred characters).</summary>
    static int LevenshteinDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }
}
