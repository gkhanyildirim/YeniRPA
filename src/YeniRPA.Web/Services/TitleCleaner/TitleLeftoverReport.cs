using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>
/// What a cleaned file still has in its titles, and what accounts for each of it.
///
/// <para>The module could always say what it <em>did</em>. It could not say what it did not do, and
/// that is the only question anybody asks of a cleaned file: why is that word still there. Answering
/// it by eye means reading a rule table against a title and guessing, under which a column with its
/// removal switched off looks exactly like a column that failed to match — two opposite problems
/// wearing the same face.</para>
///
/// <para>So every leftover word is looked up against the row's <b>own cells</b>. Where a column
/// carries the word, its rule is asked why it did not act, and the answer is a setting the operator
/// can turn. Where no column carries it, that is said too — a model code is a leftover and is meant
/// to be.</para>
/// </summary>
public static class TitleLeftoverReport
{
    /// <summary>Words too short to be worth reporting. Single letters and two-letter fragments in a
    /// title are punctuation by another name.</summary>
    const int ShortestWord = 3;

    /// <summary>
    /// The most words a cell may hold before it stops being an attribute and starts being prose.
    ///
    /// <para>Without this the report is worthless, and dangerously so. A product-description column
    /// contains nearly every word of the title, so <em>every</em> leftover gets attributed to it —
    /// and since a description is a column that does not remove, the card offered would be "turn
    /// removal on for the description", which would cut the title to pieces. A real attribute value
    /// is a short phrase: "Temperli Cam", "Elektrikli (vitroseramik)", "Ankastre ocak".</para>
    /// </summary>
    const int LongestValue = 6;

    public static IReadOnlyList<TitleLeftover> Build(
        CompiledRuleSet rules, IReadOnlyList<TitleCleanRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rows);

        var found = new Dictionary<string, Entry>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            foreach (var word in Split(row.CleanTitle))
            {
                var folded = Bare(FoldedTitle.Fold(word));
                if (folded.Length < ShortestWord)
                    continue;

                if (!found.TryGetValue(folded, out var entry))
                {
                    var (column, cause) = Explain(rules, row, folded);
                    entry = new Entry(word, column, cause, row.OriginalTitle);
                    found[folded] = entry;
                }

                entry.Rows++;
            }
        }

        return found.Values
            // What the operator can act on first, and the ordinary leftovers last. Within each, the
            // row count is the reason to look.
            .OrderBy(e => e.Cause == TitleLeftoverCause.Unclaimed)
            .ThenByDescending(e => e.Rows)
            .ThenBy(e => e.Word, StringComparer.Ordinal)
            .Select(e => new TitleLeftover(
                e.Word, e.Rows, e.Column, e.Cause, Reason(e.Cause, e.Column), e.Sample))
            .ToList();
    }

    sealed class Entry(string word, string? column, TitleLeftoverCause cause, string sample)
    {
        public string Word { get; } = word;
        public string? Column { get; } = column;
        public TitleLeftoverCause Cause { get; } = cause;
        public string Sample { get; } = sample;
        public int Rows { get; set; }
    }

    /// <summary>
    /// Which column accounts for this word, and what stopped it acting.
    ///
    /// <para>Only the row's own cells are consulted. A word is not "claimed" because some other
    /// product's cell somewhere in the file happens to carry it.</para>
    ///
    /// <para>Where several columns carry the word, the one with the <b>shortest</b> value wins. A
    /// cell reading "Cam" is making a far more specific claim on the word "Cam" than one reading
    /// "Cam seramik ve emaye karışımı", and the specific claim is the one worth reporting.</para>
    /// </summary>
    static (string? Column, TitleLeftoverCause Cause) Explain(
        CompiledRuleSet rules, TitleCleanRow row, string word)
    {
        var candidates = row.Attributes
            .Select(a => (Attribute: a, Words: Words(a.OriginalValue)))
            .Where(c => c.Words.Count > 0 && c.Words.Count <= LongestValue)
            .OrderBy(c => c.Words.Count);

        foreach (var (attribute, cellWords) in candidates)
        {
            var rule = rules.Attributes.FirstOrDefault(a =>
                string.Equals(a.Rule.Column, attribute.Column, StringComparison.Ordinal))?.Rule;

            if (rule is null)
                continue;

            var carries = cellWords.Contains(word, StringComparer.Ordinal);

            // An inflection is carried too — "ocaklar" in the title against "ocak" in the cell — and
            // it has to be looked for before deciding no column owns the word.
            var inflected = !carries && cellWords.Any(w => AttributeMatcher.IsInflected(word, w));

            if (!carries && !inflected)
                continue;

            // Whether the rule found its value at all. The order matters: a column that matched and
            // kept what it found is a removal setting, while a column that never matched is not —
            // reporting "Çıkar kapalı" for the second would send the operator to switch on something
            // that changes nothing, because there was no match to remove.
            if (attribute.Status is TitleAttributeStatus.Ok or TitleAttributeStatus.Corrected
                or TitleAttributeStatus.Filled)
            {
                return (rule.Column, rule.Remove
                    ? TitleLeftoverCause.Unmatched
                    : TitleLeftoverCause.RemoveOff);
            }

            if (inflected && !rule.AllowSuffix)
                return (rule.Column, TitleLeftoverCause.NeedsSuffix);

            // Part of a longer value, with the rest of it nowhere in this title — the case the
            // partial setting exists for. Checked against the original title, not the cleaned one:
            // the missing words may have been cut out by somebody else.
            if (cellWords.Count > 1 && !rule.AllowPartial)
            {
                var others = cellWords.Where(w => !string.Equals(w, word, StringComparison.Ordinal));
                var titleWords = Words(row.OriginalTitle);

                if (!others.Any(w => titleWords.Contains(w, StringComparer.Ordinal)))
                    return (rule.Column, TitleLeftoverCause.NeedsPartial);
            }

            if (!rule.Remove)
                return (rule.Column, TitleLeftoverCause.RemoveOff);

            return (rule.Column, TitleLeftoverCause.Unmatched);
        }

        return (null, TitleLeftoverCause.Unclaimed);
    }

    static string Reason(TitleLeftoverCause cause, string? column) => cause switch
    {
        TitleLeftoverCause.RemoveOff =>
            $"{column} hücresinde de yazıyor ama o kolonda \"Çıkar\" kapalı",
        TitleLeftoverCause.NeedsSuffix =>
            $"{column} değerinin çekim ekli hâli — o kolonda \"Ek\" kapalı",
        TitleLeftoverCause.NeedsPartial =>
            $"{column} değerinin bir parçası — o kolonda \"Kısmi\" kapalı",
        TitleLeftoverCause.Unmatched =>
            $"{column} hücresinde geçiyor ama başlıktaki yazım kataloğa girilmemiş",
        _ => "Hiçbir kolon bu kelimeyi talep etmiyor",
    };

    static List<string> Split(string text) =>
        (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    static List<string> Words(string? text) =>
        Split(text ?? "")
            .Select(w => Bare(FoldedTitle.Fold(w)))
            .Where(w => w.Length > 0)
            .ToList();

    /// <summary>A word with its punctuation taken off, so "(vitroseramik)" is compared as the word
    /// it is and "EI-Ç" does not look like something else.</summary>
    static string Bare(string word) => new(word.Where(char.IsLetterOrDigit).ToArray());
}
