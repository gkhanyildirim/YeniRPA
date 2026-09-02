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
/// <param name="BaseQuantity">The quantity in the family's base unit, where the units convert.
/// Null for a family that does not — the inch mark stands alone — and so is never compared there.</param>
/// <param name="Decimals">How precisely the <em>title</em> wrote the number. A title may be less
/// precise than the cell ("75 cm" for 745 mm) and still be talking about the same product; it may
/// not be differently precise. This is what says how far the comparison may round.</param>
/// <param name="Bare">
/// Set where the title wrote the number with <b>no unit</b> — "512SSD" for 512 GB. Such a match is
/// not valid on its own and <c>TitleCleanBuilder</c> refuses it unless another accepted span is glued
/// to it; see <c>BareSupported</c> there. Carried on the match rather than decided there because only
/// the scan knows whether a unit was read.
/// </param>
public sealed record TitleMatch(
    int Start, int End, string Text, string Canonical, string Key, double? Quantity = null,
    IReadOnlyList<(int Start, int End)>? Parts = null,
    double? BaseQuantity = null, int Decimals = 0, bool Bare = false)
{
    public int Length => End - Start;

    /// <summary>The stretches this match is made of: its parts, or itself.</summary>
    public IReadOnlyList<(int Start, int End)> Spans => Parts ?? [(Start, End)];
}

/// <summary>What one attribute cell holds, read through its rule.</summary>
/// <param name="BareQuantity">Set only when a <see cref="TitleAttributeKind.Measure"/> cell carries a
/// number with no unit ("16"). The unit is then whatever the title supplies, which is the case the
/// whole "fix the attribute" half of this module exists for.</param>
/// <param name="BaseQuantity">The cell's quantity in the family's base unit, for comparing against a
/// title that rounded it. Null where the units do not convert.</param>
/// <param name="Unit">The unit the cell was written in, where it named one. Kept so a title that
/// omitted the unit entirely — "512SSD" against a cell of "512 GB" — can still be canonicalised and
/// keyed as the cell's own unit rather than as a number with no identity.</param>
/// <param name="Parts">
/// Every measurement the cell holds, where it holds more than one — a disk column reading
/// "1 TB + 1 TB" for a machine with two of them. Null for the ordinary single-valued cell, and the
/// first part is always this value itself.
///
/// <para>It is what says how many times the title may carry the value. A repeat is normally
/// ambiguous — "RTX 5070 8GB 8GB" is a graphics card's own memory beside the system RAM — and that
/// guard is not loosened here but sharpened: the number of occurrences a row is entitled to is read
/// off the cell rather than assumed to be one.</para>
/// </param>
public sealed record AttributeValue(
    string Canonical, string Key, double? BareQuantity = null, double? BaseQuantity = null,
    MeasureUnit? Unit = null,
    IReadOnlyList<AttributeValue>? Parts = null)
{
    /// <summary>The measurements this cell holds: its parts, or itself.</summary>
    public IReadOnlyList<AttributeValue> PartList => Parts ?? [this];

    /// <summary>
    /// How many times in the title this cell accounts for <paramref name="key"/>.
    ///
    /// <para><b>One, unless the cell genuinely lists several.</b> Counting keys on an ordinary cell
    /// would be wrong twice over: a bare number has no key at all, and a rounded match carries the
    /// title's key rather than the cell's — both would come out as nought and turn a perfectly good
    /// single match into a repeat.</para>
    /// </summary>
    public int Allowed(string key)
    {
        if (Parts is null)
            return 1;

        var counted = Parts.Count(p => string.Equals(p.Key, key, StringComparison.Ordinal));
        return counted > 0 ? counted : 1;
    }
}

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

    /// <summary>The quantity in the family's base unit, or <c>null</c> where the units do not
    /// convert and there is no common ground to compare on.</summary>
    public static double? Base(double quantity, MeasureUnit unit) =>
        unit.Factor > 0 ? quantity * unit.Factor : null;

    /// <summary>How many decimal places a number was written with. "75" is none, "15,6" is one.</summary>
    public static int Decimals(string text)
    {
        var at = (text ?? "").LastIndexOfAny(['.', ',']);
        return at < 0 ? 0 : text!.Length - at - 1;
    }

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

/// <summary>One entry of a reference list, split into the words a cell value is matched against.</summary>
internal sealed record ReferenceEntry(string Canonical, IReadOnlyList<string> Words);

/// <summary>One attribute rule with its scanning machinery built once for the whole file.</summary>
public sealed class CompiledAttribute
{
    /// <summary>
    /// Which reference entries each distinct cell value turned out to be consistent with.
    ///
    /// <para>A catalogue runs to thousands of entries and a file runs to thousands of rows, but the
    /// number of <em>distinct values</em> in one column of one file is small — a laptop export names
    /// ten or so processors. Narrowing once per value rather than once per row is what keeps a 5000
    /// entry list off the critical path. Single-threaded like the rest of a run: a compiled rule set
    /// belongs to the request that built it.</para>
    /// </summary>
    readonly Dictionary<string, IReadOnlyList<ReferenceEntry>> _consistent = new(StringComparer.Ordinal);

    internal CompiledAttribute(
        TitleAttributeRule rule,
        int index,
        Regex? measureRegex,
        IReadOnlyDictionary<string, MeasureUnit> unitBySpelling,
        IReadOnlyList<(string Folded, string Canonical, string Key)> aliasSpellings,
        IReadOnlyList<ReferenceEntry> referenceEntries)
    {
        Rule = rule;
        Index = index;
        MeasureRegex = measureRegex;
        UnitBySpelling = unitBySpelling;
        AliasSpellings = aliasSpellings;
        ReferenceEntries = referenceEntries;
    }

    public TitleAttributeRule Rule { get; }

    /// <summary>Position in the rule set. Decides ties when two attributes claim the same span.</summary>
    public int Index { get; }

    internal Regex? MeasureRegex { get; }
    internal IReadOnlyDictionary<string, MeasureUnit> UnitBySpelling { get; }
    internal IReadOnlyList<(string Folded, string Canonical, string Key)> AliasSpellings { get; }
    internal IReadOnlyList<ReferenceEntry> ReferenceEntries { get; }

    internal IReadOnlyList<ReferenceEntry> ConsistentWith(
        string foldedValue, Func<IReadOnlyList<ReferenceEntry>> narrow)
    {
        if (!_consistent.TryGetValue(foldedValue, out var entries))
        {
            entries = narrow();
            _consistent[foldedValue] = entries;
        }

        return entries;
    }
}

/// <summary>A rule set with every regex and lookup table built, ready to run over a whole file.</summary>
public sealed class CompiledRuleSet
{
    CompiledRuleSet(
        TitleRuleSet source,
        IReadOnlyList<CompiledAttribute> attributes,
        IReadOnlyList<TitleReferenceList> referenceLists)
    {
        Source = source;
        Attributes = attributes;
        ReferenceLists = referenceLists;
    }

    public TitleRuleSet Source { get; }
    public IReadOnlyList<CompiledAttribute> Attributes { get; }

    /// <summary>
    /// The lists this set was compiled against, carried so that anything re-compiling a variant of it
    /// can hand them straight back.
    ///
    /// <para>Without this the suggester went silent the moment any rule named a list: it re-compiles
    /// an edited copy of the set to preview each card, that compile had no lists to give, a rule
    /// naming one is refused, and the refusal is caught and read as "this fix does not work". Every
    /// card on the file disappeared — not just the ones touching that column.</para>
    /// </summary>
    public IReadOnlyList<TitleReferenceList> ReferenceLists { get; }
    public string DecimalSeparator => Source.DecimalSeparator == "," ? "," : ".";

    /// <param name="referenceLists">The reference lists available to this run, by name. Omitted
    /// leaves every rule without one — the behaviour before the feature existed, and what a caller
    /// that has no store to read them from should get.</param>
    public static CompiledRuleSet Compile(
        TitleRuleSet source, IReadOnlyList<TitleReferenceList>? referenceLists = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source.TitleColumn))
            throw new InvalidOperationException("The rule set does not say which column holds the title.");

        var byName = new Dictionary<string, TitleReferenceList>(StringComparer.Ordinal);
        foreach (var list in referenceLists ?? [])
        {
            if (!string.IsNullOrWhiteSpace(list.Name))
                byName.TryAdd(FoldedTitle.Fold(list.Name), list);
        }

        var attributes = new List<CompiledAttribute>();
        var index = 0;

        foreach (var rule in source.AttributeList)
        {
            if (string.IsNullOrWhiteSpace(rule.Column))
                throw new InvalidOperationException("A rule in this set has no column name.");

            TitleReferenceList? list = null;

            if (!string.IsNullOrWhiteSpace(rule.ReferenceList))
            {
                // A named list that is not loaded is refused rather than ignored: the rule was written
                // because the column cannot be cleaned without it, and running on quietly would leave
                // every title in the file carrying the value the operator asked to have removed.
                if (!byName.TryGetValue(FoldedTitle.Fold(rule.ReferenceList), out list))
                {
                    throw new InvalidOperationException(
                        $"Column '{rule.Column}' refers to the reference list '{rule.ReferenceList}', " +
                        "which is not loaded. Upload it, or clear the column's reference list.");
                }
            }

            attributes.Add(AttributeMatcher.Compile(rule, index, list));
            index++;
        }

        return new CompiledRuleSet(source, attributes, referenceLists ?? []);
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
    internal static CompiledAttribute Compile(
        TitleAttributeRule rule, int index, TitleReferenceList? referenceList = null)
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

        var entries = new List<ReferenceEntry>();

        // A measured attribute is matched by number and unit; a catalogue of spellings has nowhere to
        // attach to one, so a list on such a rule would silently do nothing.
        if (referenceList is not null && rule.Kind != TitleAttributeKind.Measure)
        {
            foreach (var value in referenceList.ValueList)
            {
                var words = FoldedTitle.Fold(value ?? "")
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                if (words.Length > 0)
                    entries.Add(new ReferenceEntry(value!.Trim(), words));
            }
        }

        return new CompiledAttribute(
            rule, index, measureRegex, unitBySpelling, aliasSpellings, entries);
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
                // Every measurement in the cell, not just the first. A machine with two disks has
                // "1 TB + 1 TB" in one column, and reading only the front of it made the second one
                // in the title look like a repeat nobody had asked for.
                var parts = new List<AttributeValue>();

                foreach (Match m in attr.MeasureRegex!.Matches(FoldedTitle.Fold(text)))
                {
                    if (!Measures.TryParseQuantity(m.Groups["n"].Value, out var quantity))
                        continue;

                    var unit = attr.UnitBySpelling[m.Groups["u"].Value];

                    parts.Add(new AttributeValue(
                        Measures.Format(quantity, unit, decimalSeparator),
                        Measures.Key(quantity, unit),
                        BaseQuantity: Measures.Base(quantity, unit),
                        Unit: unit));
                }

                if (parts.Count == 1)
                    return parts[0];

                // The head keeps its own canonical spelling so nothing downstream has to know about
                // the rest; what the extra parts add is how many matches the row is entitled to.
                if (parts.Count > 1)
                    return parts[0] with { Parts = parts };

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
            {
                var written = new List<(int Start, int End)>();

                foreach (Match m in attr.MeasureRegex!.Matches(title.Folded))
                {
                    if (!Measures.TryParseQuantity(m.Groups["n"].Value, out var quantity))
                        continue;

                    var unit = attr.UnitBySpelling[m.Groups["u"].Value];
                    var (start, end) = title.ToOriginal(m.Index, m.Index + m.Length);
                    written.Add((m.Index, m.Index + m.Length));

                    matches.Add(new TitleMatch(
                        start, end,
                        title.Original[start..end],
                        Measures.Format(quantity, unit, decimalSeparator),
                        Measures.Key(quantity, unit),
                        quantity,
                        BaseQuantity: Measures.Base(quantity, unit),
                        Decimals: Measures.Decimals(m.Groups["n"].Value)));
                }

                AddUnwritten(matches, title, value, written, decimalSeparator);
                break;
            }

            case TitleAttributeKind.Alias:
                AddReference(matches, attr, title, value);

                AddSpellings(
                    matches, title, attr.AliasSpellings,
                    attr.Rule.AllowSuffix, attr.Rule.AllowPartial);
                break;

            default:
                if (value is not null)
                {
                    AddReference(matches, attr, title, value);

                    AddSpellings(
                        matches, title,
                        [(FoldedTitle.Fold(value.Canonical), value.Canonical, value.Key)],
                        attr.Rule.AllowSuffix, attr.Rule.AllowPartial);
                }
                break;
        }

        return matches;
    }

    /// <summary>
    /// The cell's value as some reference entry spells it in full — "Ultra5 125H" for a cell reading
    /// "Intel Core Ultra 5", because the catalogue carries "Intel Core Ultra 5 125H".
    ///
    /// <para><b>Removal is still a whitelist and this does not widen it.</b> An entry is only looked
    /// at when the row's own cell value sits inside it as a run of whole words, so the row is always
    /// the one asserting what the product is; the list only says how that value is written out in
    /// full. An entry the cell does not agree with is never searched for, which is why a catalogue of
    /// five thousand processors cannot introduce a processor into a title.</para>
    ///
    /// <para>What gets searched is the entry <em>from the cell's value onwards</em>. Titles drop the
    /// manufacturer — "Intel Core Ultra 5 125H" is written "Ultra5 125H" — but they do not drop the
    /// model code, and dropping the front is what the cell's own position in the entry already tells
    /// us is safe.</para>
    ///
    /// <para>The match carries the <b>cell's</b> canonical spelling, not the entry's. The list says
    /// what the title says; what the cell ought to say is not its business, and rewriting a catalogue
    /// column out of a reference file is a much larger claim than removing text from a title.</para>
    /// </summary>
    static void AddReference(
        List<TitleMatch> matches, CompiledAttribute attr, FoldedTitle title, AttributeValue? value)
    {
        if (attr.ReferenceEntries.Count == 0 || value is null)
            return;

        var folded = FoldedTitle.Fold(value.Canonical);
        var needle = folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (needle.Length == 0)
            return;

        var consistent = attr.ConsistentWith(folded, () => Narrow(attr.ReferenceEntries, needle));
        if (consistent.Count == 0)
            return;

        // The title reduced to its letters and digits. A set of whole words was too strict: the same
        // model code is written "Ryzen7-7735HS" in one title and "i5 14450HX" in the next, so the
        // word "7735hs" the entry adds is a token in one and buried in another. This is only the
        // cheap sieve — AddSpellings still has to find the entry properly, and BoundaryOk still
        // refuses a span that cuts into a word.
        var letters = Bare(title.Folded);
        var said = Bare(folded);

        // Longest first: the most of the entry the title turns out to carry is what it means, and a
        // shorter entry that is a prefix of a longer one must not take the span first.
        foreach (var entry in consistent.OrderByDescending(e => e.Words.Count))
        {
            var at = StartOf(entry.Words, needle);
            if (at < 0)
                continue;

            var tail = entry.Words.Skip(at).ToList();

            // An entry that says no more than the cell does is not worth a search — the ordinary
            // path already looks for the cell's own value, and this one exists to find more than it.
            if (Bare(string.Concat(tail)).Length <= said.Length)
                continue;

            // Every word the entry adds has to be a word the title actually writes — all of them, not
            // just the last. Checking only the last let "AMD Ryzen 5 PRO 220" through against a title
            // reading "Ryzen5 220", and since "PRO" is nowhere in that title the partial search fell
            // back to the one word both sides did share: the bare "220". A reference entry that
            // matches nothing but an unqualified number is the exact deletion this module refuses
            // everywhere else, and it cost the whole file its processor removal.
            if (!tail.Skip(needle.Length).All(w => letters.Contains(Bare(w), StringComparison.Ordinal)))
                continue;

            // And the end of it, which for a catalogue written "Intel Core i5-13420H" is inside the
            // last word rather than a word of its own.
            if (!letters.Contains(Bare(tail[^1]), StringComparison.Ordinal))
                continue;

            var before = matches.Count;

            // Partial, always, and not as a loosening. An entry is by construction *longer* than the
            // cell — that is the entire reason to consult one — and a title writes "Ultra5 125H" where
            // the catalogue writes "Intel Core Ultra 5 125H". Carrying only part of the entry is the
            // normal case here, where for a cell value it is the exception.
            //
            // What keeps it honest is the rule AddPartial already enforces: every word the run leaves
            // out has to be absent from the title. So the run can drop "Intel Core", which the title
            // genuinely does not say, and cannot drop "125H", which it does — the model code is in the
            // span or there is no match.
            AddSpellings(
                matches, title,
                [(string.Join(' ', entry.Words.Skip(at)), value.Canonical, value.Key)],
                attr.Rule.AllowSuffix,
                allowPartial: true);

            if (matches.Count == before)
                continue;

            // The entry has to have earned its place: what it matched must actually carry the words
            // it adds to the cell, or it is the ordinary search wearing a catalogue's name.
            //
            // Without this the first entry to match at all won and the loop stopped there. Against a
            // title reading "Ultra5 225U", the catalogue's "Intel Core Ultra 5 225" — a real
            // processor, listed before "…225U" — matched nothing but the "Ultra5" the cell alone
            // would have found, and blocked the entry that would have taken the model code with it.
            var end = Bare(FoldedTitle.Fold(matches[^1].Text));
            if (end.Contains(Bare(tail[^1]), StringComparison.Ordinal))
                return;

            matches.RemoveRange(before, matches.Count - before);
        }
    }

    /// <summary>The entries whose words carry <paramref name="needle"/> as a run — the narrowing that
    /// is done once per distinct cell value rather than once per row.</summary>
    static IReadOnlyList<ReferenceEntry> Narrow(
        IReadOnlyList<ReferenceEntry> entries, string[] needle) =>
        entries.Where(e => StartOf(e.Words, needle) >= 0).ToList();

    /// <summary>
    /// Where <paramref name="needle"/> begins inside <paramref name="words"/> as a run of whole
    /// words, or -1. Contiguous rather than scattered: "Ryzen 5" is in "AMD Ryzen 5 220" and is not
    /// in "AMD Ryzen 7 5800X".
    ///
    /// <para>The <b>last</b> word may end part-way into the entry's, and only there. Intel's own
    /// catalogue writes "Intel Core i5-13420H" — the family and the model code joined by a hyphen —
    /// against a cell that reads "Intel Core i5", so requiring whole words throughout would rule out
    /// every Intel entry written that way. The cut still has to land on a boundary: on a separator,
    /// or where letter meets digit. "Ryzen 3" therefore does not match "AMD Ryzen 30", which is the
    /// case this restriction is for.</para>
    /// </summary>
    static int StartOf(IReadOnlyList<string> words, string[] needle)
    {
        for (var i = 0; i + needle.Length <= words.Count; i++)
        {
            var ok = true;

            for (var j = 0; j < needle.Length && ok; j++)
            {
                var word = words[i + j];

                ok = j == needle.Length - 1
                    ? string.Equals(word, needle[j], StringComparison.Ordinal) || EndsCleanly(word, needle[j])
                    : string.Equals(word, needle[j], StringComparison.Ordinal);
            }

            if (ok)
                return i;
        }

        return -1;
    }

    /// <summary>Whether <paramref name="prefix"/> starts <paramref name="word"/> and stops at a
    /// boundary — a separator, or a change between letter and digit.</summary>
    static bool EndsCleanly(string word, string prefix) =>
        prefix.Length > 0 &&
        word.Length > prefix.Length &&
        word.StartsWith(prefix, StringComparison.Ordinal) &&
        (!char.IsLetterOrDigit(word[prefix.Length]) || ClassChanges(word, prefix.Length));

    /// <summary>Any number in a title, with or without anything after it.</summary>
    static readonly Regex AnyNumber = new(
        @"\d+(?:[.,]\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The cell's own quantity, found in the title with <b>no unit written</b> — "512SSD" for a cell
    /// reading "512 GB", where the disk type is glued straight onto the capacity and the unit never
    /// appears at all.
    ///
    /// <para><b>This does not loosen the rule that a measured value needs its unit.</b> The number is
    /// only looked for at all when the cell already supplies the unit, and only where the cell's own
    /// quantity is the one written — so no new value can enter through here, only a value the row
    /// already asserts. What is still missing is the evidence that this particular number <em>is</em>
    /// that value rather than part of a model name, and that is settled where it has to be, against
    /// the other accepted spans: <c>TitleCleanBuilder.BareSupported</c> throws the match away unless
    /// another confirmed span is glued to it with no separator between them. "512SSD" survives because
    /// the disk type continues from it; the "16" in "Pro Max 16" has nothing either side of it and does
    /// not.</para>
    ///
    /// <para>Numbers standing free of any word are skipped before that even comes up — cheap, and it
    /// keeps the pool to the handful of glued tokens this is for.</para>
    /// </summary>
    static void AddUnwritten(
        List<TitleMatch> matches,
        FoldedTitle title,
        AttributeValue? value,
        List<(int Start, int End)> written,
        string decimalSeparator)
    {
        // No unit in the cell either means there is nothing to canonicalise to. That case is the
        // cell's problem, not the title's, and it is already reported as such.
        if (value is null || value.Unit is null || value.BareQuantity.HasValue)
            return;

        // Every measurement the cell holds, so "128SSD+1TBSSD" gets its bare 128 from a cell reading
        // "1 TB + 128 GB" rather than only from the part that happens to be written first.
        // Keyed, so "1 TB + 1 TB" — which is two parts saying one thing — does not try to register
        // the same key twice. How many occurrences the title may carry is settled later, against the
        // cell's own count; here we only need to know which values to look for.
        var wanted = value.PartList
            .Where(p => p.Unit is not null)
            .DistinctBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Unit!, StringComparer.Ordinal);

        foreach (Match m in AnyNumber.Matches(title.Folded))
        {
            // Already read as a proper measurement, unit and all.
            if (written.Any(w => m.Index < w.End && w.Start < m.Index + m.Length))
                continue;

            if (!Measures.TryParseQuantity(m.Value, out var quantity))
                continue;

            var hit = wanted.FirstOrDefault(p =>
                string.Equals(Measures.Key(quantity, p.Value), p.Key, StringComparison.Ordinal));

            if (hit.Value is not { } unit)
                continue;

            var key = hit.Key;

            // Glued to a word on one side or the other. A number standing on its own is a model
            // number as often as anything else, and there would be nothing to support it with.
            var before = m.Index > 0 && char.IsLetterOrDigit(title.Folded[m.Index - 1]);
            var after = m.Index + m.Length < title.Folded.Length &&
                        char.IsLetterOrDigit(title.Folded[m.Index + m.Length]);

            if (!before && !after)
                continue;

            var (start, end) = title.ToOriginal(m.Index, m.Index + m.Length);

            matches.Add(new TitleMatch(
                start, end,
                title.Original[start..end],
                Measures.Format(quantity, unit, decimalSeparator),
                key,
                quantity,
                BaseQuantity: Measures.Base(quantity, unit),
                Decimals: Measures.Decimals(m.Value),
                Bare: true));
        }
    }

    /// <summary>
    /// Looks for a value's spellings in the title, falling back a step at a time.
    ///
    /// <list type="number">
    ///   <item>As one stretch, the way a title normally writes a value.</item>
    ///   <item>Scattered — the words in order, with something else inserted between them. Tried
    ///   <b>per spelling</b>, for the spellings the title did not write out. A catalogue holding both
    ///   "Rustik siyah" and "Siyah" has to be able to find the first one spread across a title that
    ///   also plainly contains the second; gating this on the whole attribute would let the shorter
    ///   value silence the longer one.</item>
    ///   <item>Partial — only some of the value's words, and only where the column allows it. This one
    ///   <b>is</b> gated on the whole attribute: once anything has been found, taking another spelling
    ///   apart to look for a second opinion is noise.</item>
    /// </list>
    /// </summary>
    static void AddSpellings(
        List<TitleMatch> matches,
        FoldedTitle title,
        IReadOnlyList<(string Folded, string Canonical, string Key)> spellings,
        bool allowSuffix,
        bool allowPartial)
    {
        var unwritten = new List<(string Folded, string Canonical, string Key)>();

        foreach (var spelling in spellings)
        {
            var before = matches.Count;
            AddLiteral(matches, title, spelling.Folded, spelling.Canonical, spelling.Key, allowSuffix);

            if (matches.Count == before)
                unwritten.Add(spelling);
        }

        foreach (var (folded, canonical, key) in unwritten)
            AddScattered(matches, title, folded, canonical, key, allowSuffix);

        if (matches.Count == 0 && allowPartial)
        {
            foreach (var (folded, canonical, key) in spellings)
                AddPartial(matches, title, folded, canonical, key);
        }

        // Offered alongside whatever else was found rather than only when nothing was, because a
        // catalogue routinely holds both "Dizüstü Bilgisayar" and "Dizüstü". A title cut to
        // "… Dizüstü Bi" matches the short spelling outright, so gating truncation on "nothing
        // matched" meant the short one always won and the "Bi" stayed behind on every such row. The
        // truncated span is the longer and more specific reading of the same text; letting both into
        // the pool lets the ordinary arbitration prefer it.
        foreach (var (folded, canonical, key) in spellings)
            AddTruncated(matches, title, folded, canonical, key);
    }

    /// <summary>
    /// A value the title ran out of room for — "Dizüstü Bilgisayar" written "Dizüstü Bi".
    ///
    /// <para>A marketplace caps its title field, and a seller who writes up to the cap gets the last
    /// word cut off mid-letter. Five of the forty-eight rows in one real export end that way. Nothing
    /// else in this class can find those: every other step compares whole words, and half a word is
    /// not one.</para>
    ///
    /// <para><b>Three conditions, and each of them is holding something back.</b></para>
    ///
    /// <list type="number">
    ///   <item>The match runs to the <b>exact end</b> of the title. The end of the field is the only
    ///   place anything gets cut; a short spelling in the middle of a title is a different word.</item>
    ///   <item><b>More of the value survived than was lost.</b> The first attempt used no such
    ///   measure and a product-description column promptly matched an entire title with its opening
    ///   sentence, claimed every character of it, and evicted every other rule on the row — a whole
    ///   white-goods file came out uncleaned. A description is hundreds of characters against a
    ///   title's dozen, so it can never clear half.</item>
    ///   <item>Every <b>whole word</b> the cut left behind is absent from the title. This is the same
    ///   honesty <see cref="AddPartial"/> insists on, and it is what stops a cell reading
    ///   "Windows 11 Pro" answering a title that ends "Windows 11" while going on to say "Pro"
    ///   elsewhere.</item>
    /// </list>
    ///
    /// <para>The cut may fall between two words as easily as inside one — a field that runs out at
    /// exactly 100 characters does not care where the spaces are, and "… Taşınabilir İş" is as
    /// truncated as "… Taşınabilir İ".</para>
    /// </summary>
    static void AddTruncated(
        List<TitleMatch> matches, FoldedTitle title, string folded, string canonical, string key)
    {
        var words = folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
            return;

        var titleWords = Words(title.Folded)
            .Select(w => Bare(title.Folded[w.Start..w.End]))
            .ToHashSet(StringComparer.Ordinal);

        // Longest first: the most of the value the title had room for is what it was writing. Stops
        // at half, which is where a value stops being recognisable as itself.
        for (var take = folded.Length - 1; take * 2 > folded.Length; take--)
        {
            // A cut that leaves a trailing space is the same cut one character earlier.
            if (char.IsWhiteSpace(folded[take - 1]))
                continue;

            // Whatever whole words the cut discarded must be nowhere in the title.
            var kept = folded[..take].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words.Skip(kept).Any(w => titleWords.Contains(Bare(w), StringComparer.Ordinal)))
                continue;

            var needle = folded[..take];
            var from = 0;

            while (from < title.Folded.Length)
            {
                var at = IndexOfLoose(title.Folded, needle, from, out var length);
                if (at < 0)
                    break;

                // Has to be what the title ends with. An earlier occurrence is a word in its own
                // right, not a cut-off one, so the search carries on past it rather than giving up.
                if (at + length == title.Folded.Length)
                {
                    var (start, end) = title.ToOriginal(at, at + length);
                    matches.Add(new TitleMatch(start, end, title.Original[start..end], canonical, key));
                    return;
                }

                from = at + 1;
            }
        }
    }

    static void AddLiteral(
        List<TitleMatch> matches, FoldedTitle title, string folded, string canonical, string key,
        bool allowSuffix = false)
    {
        if (folded.Length == 0)
            return;

        var from = 0;

        while (from < title.Folded.Length)
        {
            var at = IndexOfLoose(title.Folded, folded, from, out var length);
            if (at < 0)
                break;

            length = ExtendOverSuffix(title.Folded, at, length, allowSuffix);

            var (start, end) = title.ToOriginal(at, at + length);
            matches.Add(new TitleMatch(start, end, title.Original[start..end], canonical, key));

            from = at + 1;
        }
    }

    /// <summary>Words with at least this much to them. Below it a word carries no identity of its
    /// own — "EI", "VE", "CT" — and a match built on one is a coincidence.</summary>
    const int MeaningfulWord = 3;

    /// <summary>
    /// Part of a value, where the rest of it is nowhere in the title — a cell reading
    /// "CETINTAS EVII" against a title that says only "Çetintaş", or "Temperli Cam" against one that
    /// says only "Cam".
    ///
    /// <para><b>The condition is what keeps this honest.</b> Every word the run leaves out has to be
    /// absent from the title entirely. That is what stops a cell reading "Ankastre Ocak" cutting the
    /// word "Ankastre" out of a title that goes on to say "Ocak" — there the value is present and it
    /// is the contiguous or scattered search's job, not this one's.</para>
    ///
    /// <para>Still exact: every word is compared character for character once folded. What is partial
    /// is how much of the value the title was asked to carry, not how closely it had to spell it.</para>
    /// </summary>
    static void AddPartial(
        List<TitleMatch> matches, FoldedTitle title, string folded, string canonical, string key)
    {
        var needles = folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (needles.Length < 2)
            return;

        var words = Words(title.Folded);
        var raw = words.Select(w => title.Folded[w.Start..w.End]).ToList();
        var texts = raw.Select(Bare).ToList();

        // Longest first: the most of the value the title turns out to carry is what it means.
        for (var take = needles.Length - 1; take >= 1; take--)
        {
            for (var skip = 0; skip + take <= needles.Length; skip++)
            {
                var slice = needles.Skip(skip).Take(take).ToList();
                var run = slice.Select(Bare).ToList();

                if (run.All(w => w.Length < MeaningfulWord))
                    continue;

                // Anything the run leaves out must be nowhere in the title.
                var rest = needles.Where((_, i) => i < skip || i >= skip + take).Select(Bare);
                if (rest.Any(w => w.Length > 0 && texts.Contains(w, StringComparer.Ordinal)))
                    continue;

                // Punctuation-stripped first, then as written. Stripping is what lets a cell's
                // "(vitroseramik)" be compared as the word it is; keeping it is what lets a
                // catalogue's "i5-14450HX" meet a title's "i5 14450HX", because the hyphen is the
                // separator the two sides disagree about and Bare would have thrown it away.
                var (first, last) = IndexOfRun(texts, run);
                if (first < 0)
                    (first, last) = IndexOfRun(raw, slice);

                if (first < 0)
                    continue;

                var (start, _) = title.ToOriginal(words[first].Start, words[first].End);
                var (_, end) = title.ToOriginal(words[last].Start, words[last].End);

                matches.Add(new TitleMatch(start, end, title.Original[start..end], canonical, key));
                return;
            }
        }
    }

    /// <summary>
    /// Which of the title's words a run of the value's words sits across — first and last, or
    /// (-1, -1).
    ///
    /// <para>Not a word-for-word comparison, because the two sides need not agree on where the spaces
    /// go: a cell reading "Intel Core Ultra 5" has to find the title's "Ultra5", which is two of the
    /// value's words inside one of the title's. So a run is compared against a <em>consecutive stretch
    /// of title words</em> under the same rule <see cref="IndexOfLoose"/> uses — a gap may be missing
    /// only where letter meets digit.</para>
    ///
    /// <para>The match is still reported as whole title words, which is what keeps this from cutting
    /// into one: the span handed back always starts and ends on a word boundary.</para>
    /// </summary>
    static (int First, int Last) IndexOfRun(List<string> words, List<string> run)
    {
        var needle = string.Join(' ', run);

        // Letters and digits only. Counting separators would make "ultra9-275hx" look longer than
        // "ultra 9 275hx" and break out of the search before comparing them — which is exactly the
        // pair this has to be able to compare, since which separator gets typed is the thing the two
        // sides disagree about.
        var letters = needle.Count(char.IsLetterOrDigit);

        for (var i = 0; i < words.Count; i++)
        {
            var text = "";

            for (var k = 0; i + k < words.Count; k++)
            {
                text = k == 0 ? words[i] : text + " " + words[i + k];

                // Only ever grows, so once it carries more characters than the value does there is
                // nothing further to try from this starting word.
                if (text.Count(char.IsLetterOrDigit) > letters)
                    break;

                if (GlueEquals(text, needle))
                    return (i, i + k);
            }
        }

        return (-1, -1);
    }

    /// <summary>Whether two strings are the same under <see cref="IndexOfLoose"/>'s tolerances —
    /// the whole of one, from its start, answering the whole of the other.</summary>
    static bool GlueEquals(string text, string needle) =>
        IndexOfLoose(text, needle, 0, out var length) == 0 && length == text.Length;

    /// <summary>A word with its punctuation taken off, so a cell's "(vitroseramik)" is compared as
    /// the word it is rather than counted absent because of its brackets.</summary>
    static string Bare(string word) =>
        new(word.Where(char.IsLetterOrDigit).ToArray());

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
    /// Whether one folded word is another with a Turkish inflection on the end — "ocaklar" against
    /// "ocak". The leftover report asks this to tell "the column would have found it, if it were
    /// allowed to follow the ending" apart from "the column has no entry for this at all".
    /// </summary>
    public static bool IsInflected(string word, string root) =>
        word.Length > root.Length &&
        word.StartsWith(root, StringComparison.Ordinal) &&
        Suffixes.Contains(word[root.Length..]);

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
    /// whitespace as equal to any other</b>, and <b>as equal to no gap at all where the characters
    /// either side change between letter and digit</b>.
    ///
    /// <para>Titles are typed by hand and carry stray double spaces — a real export writes
    /// "RTX 5070  8GB" with two of them. An exact search makes that invisible difference decide
    /// whether a rule matches, and the operator cannot see what they typed wrong because there is
    /// nothing to see.</para>
    ///
    /// <para>The second tolerance is the same problem one step further on. A marketplace attribute
    /// cell writes "Ryzen™ 5" and "Core™ 5"; the title writes "Ryzen5 220" and "Core5 120U". Nothing
    /// separates those but a space that one side chose not to type, and without this the processor
    /// column matches nothing at all on a whole file. It is allowed <b>only at a letter/digit change</b>,
    /// which is a word boundary in its own right — "Pro Max" therefore still does not match "ProMax",
    /// because two letters running together are two words the writer joined, not one word they split.</para>
    ///
    /// <para>Everything except the width of the gap still has to match exactly, and
    /// <c>TitleCleanBuilder.BoundaryOk</c> still refuses a span that cuts into a word. This is a
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
                if (IsGap(needle[j]))
                {
                    while (j < needle.Length && IsGap(needle[j]))
                        j++;

                    if (i < haystack.Length && IsGap(haystack[i]))
                    {
                        while (i < haystack.Length && IsGap(haystack[i]))
                            i++;
                    }
                    else if (!ClassChanges(haystack, i))
                    {
                        ok = false;
                        break;
                    }
                }
                else if (i < haystack.Length && IsGap(haystack[i]) && ClassChanges(needle, j))
                {
                    // The mirror case: the cell writes "Ryzen5" and the title writes "Ryzen 5".
                    while (i < haystack.Length && IsGap(haystack[i]))
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

    /// <summary>
    /// A character that only separates two others: whitespace, or the punctuation a model code is
    /// joined with.
    ///
    /// <para>Which one gets typed between a processor family and its model number is arbitrary, and
    /// the same product is written all three ways in one file — the catalogue says
    /// "AMD Ryzen 7 7735HS" and "Intel Core i5-14450HX", the titles say "Ryzen7-7735HS",
    /// "i5 14450HX" and "Ryzen5 220". Treating a hyphen as a space here is the same tolerance for
    /// typography the whitespace rule already is, and it stops short of the characters that carry
    /// meaning: a letter or a digit is never a gap.</para>
    /// </summary>
    static bool IsGap(char ch) =>
        char.IsWhiteSpace(ch) || ch is '-' or '_' or '/' or '.';

    /// <summary>
    /// Whether <paramref name="at"/> sits on a letter/digit change — the character before it and the
    /// character at it are both alphanumeric and one is a digit while the other is not.
    ///
    /// <para>This is the only place a written gap may go missing. A change of class is a word boundary
    /// that needs no space to be one, which is why "Ryzen5" reads as two tokens to any human and
    /// "ProMax" does not.</para>
    /// </summary>
    static bool ClassChanges(string text, int at)
    {
        if (at <= 0 || at >= text.Length)
            return false;

        var before = text[at - 1];
        var here = text[at];

        return char.IsLetterOrDigit(before) && char.IsLetterOrDigit(here) &&
               char.IsDigit(before) != char.IsDigit(here);
    }
}
