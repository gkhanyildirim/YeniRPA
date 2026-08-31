using System.Globalization;
using System.Text.RegularExpressions;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>
/// One place in the title that expresses a value of some attribute's kind.
/// </summary>
/// <param name="Start">Index in the <b>original</b> title.</param>
/// <param name="End">Exclusive end in the original title.</param>
/// <param name="Text">The exact original text at that span, as the title spells it.</param>
/// <param name="Canonical">How that value is written in canonical form ("16 GB", "Windows 11 Pro").</param>
/// <param name="Key">Comparison key: two matches carrying the same key say the same thing, however
/// differently they are spelled.</param>
/// <param name="Quantity">The number this match carries, for a measured value. Kept apart from the
/// key because a cell holding a bare "16" has to be compared on the number alone — the unit is what
/// the title is being asked for.</param>
/// <param name="Parts">
/// Where the match actually sits, when the title does not write it as one stretch — "Rustik siyah"
/// against a title reading "Rustik 60 cm Siyah".
///
/// <para>Null for the ordinary case, where <paramref name="Start"/>..<paramref name="End"/> is the
/// whole of it. Where it is set those two are the outer reach — what the match <em>occupies</em>,
/// for deciding overlap — while <see cref="Parts"/> is what may be cut. The two differ precisely
/// because the text in between belongs to some other attribute.</para>
/// </param>
public sealed record TitleMatch(
    int Start, int End, string Text, string Canonical, string Key, double? Quantity = null,
    IReadOnlyList<(int Start, int End)>? Parts = null)
{
    public int Length => End - Start;

    /// <summary>The stretches this match is made of: its parts, or itself.</summary>
    public IReadOnlyList<(int Start, int End)> Spans => Parts ?? [(Start, End)];
}

/// <summary>What one attribute cell holds, read through its rule.</summary>
/// <param name="BareQuantity">Set only when a <see cref="TitleAttributeKind.Measure"/> cell carries a
/// number with no unit ("16"). The unit is then whatever the title supplies, which is the case the
/// whole "fix the attribute" half of this module exists for.</param>
public sealed record AttributeValue(string Canonical, string Key, double? BareQuantity = null);

/// <summary>Quantity parsing, comparison keys and canonical formatting for measured attributes.</summary>
public static class Measures
{
    /// <summary>Accepts both decimal separators: a screen size reaches us as "15,6" from a Turkish
    /// sheet and as "15.6" from the marketplace, and those are one value.</summary>
    public static bool TryParseQuantity(string text, out double value) =>
        double.TryParse(
            (text ?? "").Trim().Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    /// <summary>
    /// The key two measured values are compared on. Where the unit carries a
    /// <see cref="MeasureUnit.Factor"/> the comparison happens in the base unit, so a cell reading
    /// "1024 GB" is not reported as a conflict against a title reading "1TB". Without a factor the
    /// unit has to match exactly — no conversion is ever invented.
    /// </summary>
    public static string Key(double quantity, MeasureUnit unit) =>
        unit.Factor > 0
            ? "#" + (quantity * unit.Factor).ToString("R", CultureInfo.InvariantCulture)
            : quantity.ToString("R", CultureInfo.InvariantCulture) + "|" + FoldedTitle.Fold(unit.Canonical);

    /// <summary>
    /// Canonical spelling of a measured value. Whether a space separates the number from the unit is
    /// derived from the unit itself rather than configured: a unit that starts with a letter reads
    /// "16 GB", a punctuation unit reads with the mark against the number.
    /// </summary>
    public static string Format(double quantity, MeasureUnit unit, string decimalSeparator)
    {
        var number = quantity.ToString("0.####", CultureInfo.InvariantCulture);
        if (decimalSeparator == ",")
            number = number.Replace('.', ',');

        var canonical = unit.Canonical ?? "";
        var space = canonical.Length > 0 && char.IsLetter(canonical[0]) ? " " : "";
        return number + space + canonical;
    }
}

/// <summary>One attribute rule with its scanning machinery built once for the whole file.</summary>
public sealed class CompiledAttribute
{
    internal CompiledAttribute(
        TitleAttributeRule rule,
        int index,
        Regex? measureRegex,
        IReadOnlyDictionary<string, MeasureUnit> unitBySpelling,
        IReadOnlyList<(string Folded, string Canonical, string Key)> aliasSpellings)
    {
        Rule = rule;
        Index = index;
        MeasureRegex = measureRegex;
        UnitBySpelling = unitBySpelling;
        AliasSpellings = aliasSpellings;
    }

    public TitleAttributeRule Rule { get; }

    /// <summary>Position in the rule set. Decides ties when two attributes claim the same span.</summary>
    public int Index { get; }

    internal Regex? MeasureRegex { get; }
    internal IReadOnlyDictionary<string, MeasureUnit> UnitBySpelling { get; }
    internal IReadOnlyList<(string Folded, string Canonical, string Key)> AliasSpellings { get; }
}

/// <summary>A rule set with every regex and lookup table built, ready to run over a whole file.</summary>
public sealed class CompiledRuleSet
{
    CompiledRuleSet(TitleRuleSet source, IReadOnlyList<CompiledAttribute> attributes)
    {
        Source = source;
        Attributes = attributes;
    }

    public TitleRuleSet Source { get; }
    public IReadOnlyList<CompiledAttribute> Attributes { get; }
    public string DecimalSeparator => Source.DecimalSeparator == "," ? "," : ".";

    public static CompiledRuleSet Compile(TitleRuleSet source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source.TitleColumn))
            throw new InvalidOperationException("The rule set does not say which column holds the title.");

        var attributes = new List<CompiledAttribute>();
        var index = 0;

        foreach (var rule in source.AttributeList)
        {
            if (string.IsNullOrWhiteSpace(rule.Column))
                throw new InvalidOperationException("A rule in this set has no column name.");

            attributes.Add(AttributeMatcher.Compile(rule, index));
            index++;
        }

        return new CompiledRuleSet(source, attributes);
    }
}

/// <summary>
/// Finds every place in a title that expresses a given attribute's kind of value.
///
/// <para><b>Nothing here checks word boundaries.</b> That is deliberate and it is what lets
/// "1TBSSD" — one token carrying a disk capacity and a disk type glued together — come apart into
/// two matches. A boundary rule strict enough to reject the "16" inside "MC16250_3" would also
/// reject the "1TB" inside "1TBSSD", so the two cases cannot be told apart one span at a time.
/// <see cref="TitleCleanBuilder"/> validates boundaries afterwards, once it knows which
/// <em>other</em> spans were accepted: a span may begin or end inside a word only where another
/// accepted span picks up exactly where it leaves off.</para>
///
/// <para><b>There is no fuzzy matching here and none may ever be added.</b> No Levenshtein, no
/// "closest value", no prefix guessing. The same rule as <see cref="CarrierNames"/> and
/// <see cref="SellerGroupMap"/>, for a sharper version of the same reason: an 85%-similar match here
/// does not misroute a message, it silently deletes the wrong characters out of a product title and
/// writes the result back to the marketplace. A value that does not match exactly is reported, never
/// approximated.</para>
/// </summary>
public static class AttributeMatcher
{
    internal static CompiledAttribute Compile(TitleAttributeRule rule, int index)
    {
        Regex? measureRegex = null;
        var unitBySpelling = new Dictionary<string, MeasureUnit>(StringComparer.Ordinal);
        var aliasSpellings = new List<(string Folded, string Canonical, string Key)>();

        if (rule.Kind == TitleAttributeKind.Measure)
        {
            foreach (var unit in rule.UnitList)
            {
                foreach (var spelling in Spellings(unit))
                {
                    var folded = FoldedTitle.Fold(spelling);
                    if (folded.Length > 0)
                        unitBySpelling.TryAdd(folded, unit);
                }
            }

            if (unitBySpelling.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Column '{rule.Column}' is set up as a measured attribute but names no unit. " +
                    "A measured value is only ever matched together with its unit — without one, the " +
                    "bare number in a model name would be removed from the title.");
            }

            measureRegex = BuildMeasureRegex(unitBySpelling.Keys);
        }

        if (rule.Kind == TitleAttributeKind.Alias)
        {
            foreach (var group in rule.AliasGroups)
            {
                if (group is null || group.Count == 0)
                    continue;

                var canonical = group[0].Trim();
                if (canonical.Length == 0)
                    continue;

                var key = FoldedTitle.Fold(canonical);

                foreach (var spelling in group)
                {
                    var folded = FoldedTitle.Fold(spelling);
                    if (folded.Length > 0)
                        aliasSpellings.Add((folded, canonical, key));
                }
            }

            // Longest first, so "Windows 11 Pro" is preferred over a "Windows 11" that would leave
            // the word "Pro" stranded in the cleaned title.
            aliasSpellings.Sort((a, b) => b.Folded.Length.CompareTo(a.Folded.Length));
        }

        return new CompiledAttribute(rule, index, measureRegex, unitBySpelling, aliasSpellings);
    }

    static IEnumerable<string> Spellings(MeasureUnit unit)
    {
        yield return unit.Canonical;

        foreach (var spelling in unit.Spellings ?? [])
            yield return spelling;
    }

    /// <summary>
    /// Number, optional space, unit — with <b>no</b> assertions on either side. The unit is what
    /// makes a number a measurement; a bare "16" is never a candidate, which is the single rule that
    /// keeps the model name "Pro Max 16" and the model code "MC16250_3" out of the removal set.
    /// </summary>
    static Regex BuildMeasureRegex(IEnumerable<string> foldedSpellings)
    {
        var alternation = string.Join(
            "|",
            foldedSpellings
                .OrderByDescending(s => s.Length)
                .ThenBy(s => s, StringComparer.Ordinal)
                .Select(Regex.Escape));

        return new Regex(
            @"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>" + alternation + ")",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Reads one attribute cell through its rule.
    ///
    /// <para>Returns <c>null</c> for an empty cell. A measured cell that parses as a number with no
    /// unit comes back with <see cref="AttributeValue.BareQuantity"/> set and no key — the title is
    /// what supplies the unit. A measured cell that is not a number at all falls back to being
    /// compared as plain text, so it can still match a title literally rather than being dropped.</para>
    /// </summary>
    public static AttributeValue? ReadValue(CompiledAttribute attr, string? cell, string decimalSeparator)
    {
        var text = (cell ?? "").Trim();
        if (text.Length == 0)
            return null;

        switch (attr.Rule.Kind)
        {
            case TitleAttributeKind.Measure:
            {
                var match = attr.MeasureRegex!.Match(FoldedTitle.Fold(text));
                if (match.Success && Measures.TryParseQuantity(match.Groups["n"].Value, out var quantity))
                {
                    var unit = attr.UnitBySpelling[match.Groups["u"].Value];
                    return new AttributeValue(
                        Measures.Format(quantity, unit, decimalSeparator),
                        Measures.Key(quantity, unit));
                }

                if (Measures.TryParseQuantity(text, out var bare))
                    return new AttributeValue(text, "", bare);

                return new AttributeValue(text, FoldedTitle.Fold(text));
            }

            case TitleAttributeKind.Alias:
            {
                var folded = FoldedTitle.Fold(text);
                foreach (var (spelling, canonical, key) in attr.AliasSpellings)
                {
                    if (string.Equals(spelling, folded, StringComparison.Ordinal))
                        return new AttributeValue(canonical, key);
                }

                // A value the catalogue does not carry is kept as itself rather than attached to the
                // nearest entry it resembles.
                return new AttributeValue(text, folded);
            }

            default:
                return new AttributeValue(text, FoldedTitle.Fold(text));
        }
    }

    /// <summary>
    /// Every span of the title that expresses this attribute's kind of value.
    ///
    /// <para><see cref="TitleAttributeKind.Measure"/> and <see cref="TitleAttributeKind.Alias"/> scan
    /// the title independently of the cell — that is what makes a conflict visible, because the scan
    /// reports what the title says rather than only confirming what the cell says.
    /// <see cref="TitleAttributeKind.Text"/> can only search for the cell's own value, so it can
    /// report found or not found and never a conflict.</para>
    /// </summary>
    public static List<TitleMatch> Scan(
        CompiledAttribute attr, FoldedTitle title, AttributeValue? value, string decimalSeparator)
    {
        var matches = new List<TitleMatch>();

        switch (attr.Rule.Kind)
        {
            case TitleAttributeKind.Measure:
                foreach (Match m in attr.MeasureRegex!.Matches(title.Folded))
                {
                    if (!Measures.TryParseQuantity(m.Groups["n"].Value, out var quantity))
                        continue;

                    var unit = attr.UnitBySpelling[m.Groups["u"].Value];
                    var (start, end) = title.ToOriginal(m.Index, m.Index + m.Length);

                    matches.Add(new TitleMatch(
                        start, end,
                        title.Original[start..end],
                        Measures.Format(quantity, unit, decimalSeparator),
                        Measures.Key(quantity, unit),
                        quantity));
                }
                break;

            case TitleAttributeKind.Alias:
                foreach (var (spelling, canonical, key) in attr.AliasSpellings)
                    AddLiteral(matches, title, spelling, canonical, key, attr.Rule.AllowSuffix);
                break;

            default:
                if (value is not null)
                {
                    AddLiteral(
                        matches, title, FoldedTitle.Fold(value.Canonical), value.Canonical, value.Key,
                        attr.Rule.AllowSuffix);
                }
                break;
        }

        return matches;
    }

    static void AddLiteral(
        List<TitleMatch> matches, FoldedTitle title, string folded, string canonical, string key,
        bool allowSuffix = false)
    {
        if (folded.Length == 0)
            return;

        var found = 0;
        var from = 0;

        while (from < title.Folded.Length)
        {
            var at = IndexOfLoose(title.Folded, folded, from, out var length);
            if (at < 0)
                break;

            length = ExtendOverSuffix(title.Folded, at, length, allowSuffix);

            var (start, end) = title.ToOriginal(at, at + length);
            matches.Add(new TitleMatch(start, end, title.Original[start..end], canonical, key));

            found++;
            from = at + 1;
        }

        // Only where the title writes it as one stretch nowhere at all. A value that was found is
        // found; going on to also match its words scattered about would turn one honest match into
        // several noisy ones.
        if (found == 0)
            AddScattered(matches, title, folded, canonical, key, allowSuffix);
    }

    /// <summary>
    /// Turkish inflections a match may run to the end of a word over. A closed list, deliberately:
    /// this is the one place where a span is allowed to cover characters the value does not contain,
    /// and it earns that only by being enumerable and short. Written folded, the form
    /// <see cref="FoldedTitle"/> produces.
    /// </summary>
    static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
    {
        "lar", "ler", "la", "le", "lari", "leri", "larin", "lerin",
        "i", "u", "a", "e", "si", "su", "in", "un", "nin", "nun",
        "da", "de", "ta", "te", "dan", "den", "tan", "ten",
        "ya", "ye", "yi", "yla", "yle",
    };

    /// <summary>
    /// Stretches a match to the end of its word when what follows is an inflection — so "Ocaklar"
    /// goes whole rather than leaving "lar" behind, which is what the boundary rule would otherwise
    /// (rightly) refuse to do.
    /// </summary>
    static int ExtendOverSuffix(string folded, int at, int length, bool allowSuffix)
    {
        if (!allowSuffix || length == 0)
            return length;

        var end = at + length;
        if (end >= folded.Length || !char.IsLetterOrDigit(folded[end]))
            return length;

        var wordEnd = end;
        while (wordEnd < folded.Length && char.IsLetterOrDigit(folded[wordEnd]))
            wordEnd++;

        return Suffixes.Contains(folded[end..wordEnd]) ? wordEnd - at : length;
    }

    /// <summary>
    /// A multi-word value whose words the title separates — "Rustik siyah" against "Rustik 60 cm
    /// Siyah". Each word has to be there, whole and in order; the match then carries them as its
    /// parts and reaches across the gap only for the purpose of claiming it.
    ///
    /// <para><b>Nothing here decides that the gap is ignorable.</b> That is settled afterwards, in
    /// <c>TitleCleanBuilder</c>, which accepts this match only where every gap is covered by other
    /// attributes that are removing what is in it. Two words with unclaimed text between them are
    /// two words, not a value.</para>
    /// </summary>
    static void AddScattered(
        List<TitleMatch> matches, FoldedTitle title, string folded, string canonical, string key,
        bool allowSuffix)
    {
        var needles = folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (needles.Length < 2)
            return;

        var words = Words(title.Folded);
        var parts = new List<(int Start, int End)>(needles.Length);
        var next = 0;

        foreach (var needle in needles)
        {
            var hit = -1;

            for (var w = next; w < words.Count; w++)
            {
                var (start, end) = words[w];
                var text = title.Folded[start..end];

                if (string.Equals(text, needle, StringComparison.Ordinal) ||
                    ExtendOverSuffix(title.Folded, start, needle.Length, allowSuffix) == end - start &&
                    text.StartsWith(needle, StringComparison.Ordinal))
                {
                    hit = w;
                    break;
                }
            }

            if (hit < 0)
                return;

            var (from, to) = title.ToOriginal(words[hit].Start, words[hit].End);
            parts.Add((from, to));
            next = hit + 1;
        }

        matches.Add(new TitleMatch(
            parts[0].Start,
            parts[^1].End,
            title.Original[parts[0].Start..parts[^1].End],
            canonical,
            key,
            Parts: parts));
    }

    /// <summary>Where each word of a folded title begins and ends.</summary>
    static List<(int Start, int End)> Words(string folded)
    {
        var words = new List<(int, int)>();
        var at = 0;

        while (at < folded.Length)
        {
            while (at < folded.Length && char.IsWhiteSpace(folded[at]))
                at++;

            var start = at;
            while (at < folded.Length && !char.IsWhiteSpace(folded[at]))
                at++;

            if (at > start)
                words.Add((start, at));
        }

        return words;
    }

    /// <summary>
    /// Finds <paramref name="needle"/> in <paramref name="haystack"/>, treating <b>any run of
    /// whitespace as equal to any other</b>.
    ///
    /// <para>Titles are typed by hand and carry stray double spaces — a real export writes
    /// "RTX 5070  8GB" with two of them. An exact search makes that invisible difference decide
    /// whether a rule matches, and the operator cannot see what they typed wrong because there is
    /// nothing to see. Everything except the width of the gap still has to match exactly; this is a
    /// tolerance for typography, not a step towards approximate matching.</para>
    /// </summary>
    static int IndexOfLoose(string haystack, string needle, int from, out int length)
    {
        length = 0;

        for (var start = from; start < haystack.Length; start++)
        {
            var i = start;
            var j = 0;
            var ok = true;

            while (j < needle.Length)
            {
                if (char.IsWhiteSpace(needle[j]))
                {
                    while (j < needle.Length && char.IsWhiteSpace(needle[j]))
                        j++;

                    if (i >= haystack.Length || !char.IsWhiteSpace(haystack[i]))
                    {
                        ok = false;
                        break;
                    }

                    while (i < haystack.Length && char.IsWhiteSpace(haystack[i]))
                        i++;
                }
                else
                {
                    if (i >= haystack.Length || haystack[i] != needle[j])
                    {
                        ok = false;
                        break;
                    }

                    i++;
                    j++;
                }
            }

            if (ok)
            {
                length = i - start;
                return start;
            }
        }

        return -1;
    }
}
