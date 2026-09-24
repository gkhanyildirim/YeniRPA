using System.Text;
using System.Text.RegularExpressions;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>
/// Fixes the letter case of well-known laptop brand/model words in a cleaned title — "THINKPAD" or
/// "thinkpad" becomes "ThinkPad" — without touching anything else in the title.
///
/// <para>This is a fixed, curated whitelist rather than a rule the operator edits: unlike an
/// attribute's alias group, a model word here is not tied to any column or cell value, it is simply
/// how the manufacturer spells its own product line. Add to <see cref="KnownTerms"/> as new brands
/// come up.</para>
///
/// <para><b>Deliberately not built on <see cref="FoldedTitle"/>.</b> That class absorbs a trailing
/// decorative mark (™, ®, a combining dot) into the original span of the character before it, which
/// is exactly right for a <em>deletion</em> — cutting the character takes the mark with it — and
/// exactly wrong for a <em>replacement</em>: translating a folded "thinkpad" match back through
/// <c>ToOriginal</c> against a title reading "ThinkPad™" would hand back the 9-character span
/// "ThinkPad™", and overwriting that with the 8-character literal "ThinkPad" would silently delete
/// the trademark mark. A plain string-to-string <see cref="Regex.Replace(string, string)"/> has no
/// span translation to get wrong.</para>
/// </summary>
public static class ModelCasing
{
    /// <summary>
    /// Known model words, in their correct casing. Longest first is enforced at use, not by this
    /// list's order, so new entries can simply be appended.
    ///
    /// <para><b>Scoped to words with a real internal capital</b> — "ThinkPad", never "Legion" or
    /// "Omen". A compound like "Thinkpad"/"THINKPAD" has exactly one correct spelling no matter how a
    /// seller writes it, so fixing it is not a judgement call. An ordinary single-word brand name has
    /// no such thing: a real seller export in this catalogue writes "OMEN" in full caps as its own
    /// stylisation, and forcing that to "Omen" would be relitigating a seller's typography choice
    /// rather than fixing a wrong spelling — exactly the kind of guess this module avoids elsewhere.
    /// A pure acronym (ROG, TUF, XPS) is the other safe case: it has one correct casing regardless of
    /// stylisation, the same as a hump word.</para>
    /// </summary>
    static readonly string[] KnownTerms =
    [
        // Lenovo
        "ThinkPad", "ThinkBook", "ThinkStation", "ThinkCentre", "IdeaPad", "IdeaCentre",
        // Asus
        "ZenBook", "VivoBook", "ExpertBook", "ProArt", "ROG", "TUF",
        // Acer
        "TravelMate", "ConceptD",
        // HP
        "EliteBook", "ProBook", "ZBook",
        // Dell
        "XPS",
        // Apple
        "MacBook Air", "MacBook Pro", "MacBook",
    ];

    static readonly IReadOnlyList<(Regex Pattern, string Canonical)> Rules = BuildRules();

    /// <summary>
    /// Rewrites every known model word in <paramref name="title"/> to its canonical casing. Everything
    /// else in the title — spacing, punctuation, unrelated words — is left exactly as it was.
    /// </summary>
    public static string Apply(string title)
    {
        if (string.IsNullOrEmpty(title))
            return title;

        var result = title;

        foreach (var (pattern, canonical) in Rules)
            result = pattern.Replace(result, canonical);

        return result;
    }

    static IReadOnlyList<(Regex, string)> BuildRules() =>
        KnownTerms
            // Longest first: "MacBook Pro" has to be tried before the plain "MacBook" it starts with,
            // or the shorter term would fire first and leave "Pro" behind unmatched.
            .OrderByDescending(t => t.Length)
            .Select(term => (BuildPattern(term), term))
            .ToList();

    /// <summary>
    /// Case-insensitive, word-boundary-safe pattern for one term. <see cref="RegexOptions.IgnoreCase"/>
    /// with <see cref="RegexOptions.CultureInvariant"/> does not fold the Turkish i-family (İ/I/ı/i) —
    /// the same problem <see cref="FoldedTitle.FoldChar"/> exists to solve elsewhere in this module —
    /// so every "i"/"I" in the term is widened into a <c>[iİıI]</c> class by hand rather than relying
    /// on the regex engine's own case folding for it.
    /// </summary>
    static Regex BuildPattern(string term)
    {
        var body = new StringBuilder();

        foreach (var ch in term)
        {
            if (ch is 'i' or 'I')
                body.Append("[iİıI]");
            else
                body.Append(Regex.Escape(ch.ToString()));
        }

        // No letter or digit on either side — so "ROG" does not fire inside "ROGUE", and a term is
        // never matched as part of some longer, unrelated word.
        var pattern = @"(?<![\p{L}\p{N}])" + body + @"(?![\p{L}\p{N}])";

        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
