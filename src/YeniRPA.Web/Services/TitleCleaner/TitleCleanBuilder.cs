using System.Text.RegularExpressions;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>
/// Turns one product row into a cleaned title plus a verdict per attribute.
///
/// <para><b>Removal is a whitelist.</b> Only a value that an attribute rule names, that the row's own
/// cell carries, and that the title is confirmed to spell the same way is ever cut out. Everything
/// else survives untouched — which is the whole reason "RTXPRO2000" is still in the reference
/// result: no column claims it, so nothing removes it. A subtractive tool that guessed would be
/// destroying catalogue data it cannot get back.</para>
///
/// <para><b>The hard part is deciding what a number belongs to.</b> The reference title carries "16"
/// three times — in the model name ("Pro Max 16"), inside the model code ("MC16250_3") and as the
/// screen size ("16\"") — and only the last may go. Two rules together settle it: a measured value is
/// only ever recognised <em>with its unit</em>, and a span may only begin or end inside a word where
/// another accepted span picks up exactly where it leaves off. The second rule is what lets
/// "1TBSSD" come apart into a disk capacity and a disk type while "MC16250_3" stays whole.</para>
/// </summary>
public static class TitleCleanBuilder
{
    /// <summary>A match, the attribute that found it, and whether it says what that attribute's cell
    /// says. A class rather than a record: the resolution loop identifies candidates by reference,
    /// and two attributes can legitimately produce equal-valued candidates for one span.</summary>
    sealed class Candidate(TitleMatch match, CompiledAttribute attr, bool isValueMatch)
    {
        public TitleMatch Match { get; } = match;
        public CompiledAttribute Attr { get; } = attr;
        public bool IsValueMatch { get; } = isValueMatch;
        public int Start => Match.Start;
        public int End => Match.End;
    }

    /// <summary>
    /// Runs a rule set over a whole uploaded table. The header row is row 1, so the row numbers on
    /// the results line up with what the operator sees in Excel.
    /// </summary>
    public static IReadOnlyList<TitleCleanRow> Clean(CompiledRuleSet rules, List<List<string>> table)
        => Clean(rules, table, out _);

    /// <summary>
    /// Runs a rule set over a whole uploaded table. The header row is row 1, so the row numbers on
    /// the results line up with what the operator sees in Excel.
    /// </summary>
    /// <param name="skippedFieldCodes">Set when the second row held the marketplace's technical field
    /// codes rather than a product — see <see cref="IsFieldCodeRow"/>.</param>
    public static IReadOnlyList<TitleCleanRow> Clean(
        CompiledRuleSet rules, List<List<string>> table, out bool skippedFieldCodes)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(table);

        skippedFieldCodes = false;

        if (table.Count == 0)
            throw new InvalidOperationException("The uploaded file is empty.");

        var header = TabularFile.BuildHeaderIndex(table[0]);
        var titleColumn = rules.Source.TitleColumn.Trim();

        if (!header.TryGetValue(titleColumn, out var titleIndex))
        {
            throw new InvalidOperationException(
                $"Required column '{titleColumn}' was not found in the uploaded file.");
        }

        // Strict on purpose. In the normal flow the rule set was proposed from this very file, so a
        // missing column means the wrong rule set was picked — and that fails loudly here rather
        // than quietly leaving one attribute uncleaned on every row.
        //
        // Every missing column is named at once: picking the wrong rule set usually misses several,
        // and reporting them one per run costs the operator a round trip each.
        var columnByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var attr in rules.Attributes)
        {
            var name = attr.Rule.Column.Trim();
            if (header.TryGetValue(name, out var index))
                columnByName[name] = index;
            else
                missing.Add(name);
        }

        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Select(name => $"'{name}'"));
            throw new InvalidOperationException(
                $"Required column{(missing.Count == 1 ? "" : "s")} {names} " +
                $"{(missing.Count == 1 ? "was" : "were")} not found in the uploaded file. " +
                $"The rule set '{rules.Source.Name}' expects {(missing.Count == 1 ? "it" : "them")}.");
        }

        var rows = new List<TitleCleanRow>(Math.Max(0, table.Count - 1));

        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];
            if (row.All(cell => cell.Trim().Length == 0))
                continue;

            // Left in the output untouched, not dropped: the marketplace's own importer needs it
            // back. It is only kept out of the cleaning and out of the statistics.
            if (r == 1 && IsFieldCodeRow(row, titleIndex))
            {
                skippedFieldCodes = true;
                continue;
            }

            rows.Add(CleanRow(
                rules,
                r + 1,
                TabularFile.GetCell(row, titleIndex),
                name => columnByName.TryGetValue(name.Trim(), out var index)
                    ? TabularFile.GetCell(row, index)
                    : ""));
        }

        return rows;
    }

    /// <summary>Rows sent to the browser per table. Above this the tables say they are truncated
    /// rather than quietly under-reporting how much needs review.</summary>
    public const int PreviewLimit = 2_000;

    /// <summary>A cell holding nothing but a number — no unit, no letters, nothing to identify it by.</summary>
    static readonly Regex BareNumber = new(
        @"^\d+(?:[.,]\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Runs a rule set and summarises the result for the dashboard.
    /// </summary>
    /// <param name="categoryRules">The marketplace's RuleSet, when one has been uploaded. Null leaves
    /// the result exactly as it was before that feature existed.</param>
    /// <param name="fileCategory">Which category the uploaded file declares, for checking the RuleSet
    /// against.</param>
    public static TitleCleanData BuildData(
        CompiledRuleSet rules,
        List<List<string>> table,
        IReadOnlyList<string>? notes = null,
        IReadOnlyList<CategoryTypeRule>? categoryRules = null,
        string? fileCategory = null)
    {
        var rows = Clean(rules, table, out var skippedFieldCodes);

        var allNotes = new List<string>(notes ?? []);
        if (skippedFieldCodes)
        {
            allNotes.Add(
                "Dosyanın 2. satırı ürün değil, pazaryerinin teknik alan kodlarını taşıyor " +
                "(TITLE__TR_TR, BRAND, PROD_FEAT_…). Temizlemeye alınmadı; çıktıda olduğu gibi duruyor.");
        }

        var summaries = rules.Attributes.Select(attr =>
        {
            var column = attr.Rule.Column;
            var results = rows
                .Select(row => row.Attributes.FirstOrDefault(a =>
                    string.Equals(a.Column, column, StringComparison.Ordinal)))
                .Where(a => a is not null)
                .Select(a => a!.Status)
                .ToList();

            return new TitleAttributeSummary(
                column,
                attr.Rule.Kind,
                attr.Rule.Remove,
                results.Count(s => s == TitleAttributeStatus.Ok),
                results.Count(s => s == TitleAttributeStatus.Corrected),
                results.Count(s => s is TitleAttributeStatus.Conflict or TitleAttributeStatus.Ambiguous),
                results.Count(s => s == TitleAttributeStatus.NotInTitle),
                results.Count(s => s == TitleAttributeStatus.Empty));
        }).ToList();

        return new TitleCleanData(
            rules.Source,
            rows.Count,
            rows.Count(r => r.Changed),
            rows.Count(r => !r.Changed),
            rows.Count(r => r.HasConflict),
            summaries.Sum(s => s.Corrected),
            rows.Sum(r => r.Attributes.Count(a => a.Status == TitleAttributeStatus.Filled)),
            summaries,
            rows.Take(PreviewLimit).ToList(),
            rows.Where(r => r.HasConflict).Take(PreviewLimit).ToList(),
            PreviewLimit,
            allNotes,
            // Computed over every row, not just the ones the preview tables show: a scenario's row
            // count is the reason to act on it, and a truncated count would understate it.
            [
                .. TitleFixSuggester.Suggest(rules, rows),
                .. TitleFixSuggester.SuggestCategoryTypes(rules, rows, categoryRules, fileCategory),
            ]);
    }

    /// <summary>
    /// Whether a row holds the marketplace's technical field codes rather than a product.
    ///
    /// <para>A Mirakl import template carries three header rows in effect: the human column name, the
    /// technical field code (<c>TITLE__TR_TR</c>, <c>BRAND</c>, <c>PROD_FEAT_16858</c>) and then the
    /// products. Read as data, that middle row poisons everything downstream — it seeds every alias
    /// catalogue with a field code, counts a column that is actually empty as having one value, and
    /// produces a junk output row.</para>
    ///
    /// <para>The test is deliberately narrow, because a false positive silently drops a real product.
    /// It only ever runs on the row directly under the header, and its first requirement is that the
    /// <b>title cell contains no whitespace at all</b> — a product title always does.</para>
    /// </summary>
    public static bool IsFieldCodeRow(List<string> row, int titleIndex)
    {
        var title = TabularFile.GetCell(row, titleIndex).Trim();
        if (title.Length == 0 || title.Any(char.IsWhiteSpace))
            return false;

        var values = row.Select(cell => cell.Trim()).Where(cell => cell.Length > 0).ToList();
        if (values.Count < 5)
            return false;

        var single = values.Count(v => !v.Any(char.IsWhiteSpace));
        return single >= values.Count * 0.6;
    }

    /// <summary>Cleans one row. <paramref name="cell"/> reads an attribute column by header name.</summary>
    public static TitleCleanRow CleanRow(
        CompiledRuleSet rules, int rowNumber, string? rawTitle, Func<string, string?> cell)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(cell);

        var title = FoldedTitle.Of(rawTitle);
        var separator = rules.DecimalSeparator;

        var values = new AttributeValue?[rules.Attributes.Count];
        var pool = new List<Candidate>();

        for (var i = 0; i < rules.Attributes.Count; i++)
        {
            var attr = rules.Attributes[i];
            var value = AttributeMatcher.ReadValue(attr, cell(attr.Rule.Column), separator);
            values[i] = value;

            foreach (var match in AttributeMatcher.Scan(attr, title, value, separator))
            {
                if (match.End > match.Start)
                    pool.Add(new Candidate(match, attr, IsValueMatch(value, match)));
            }
        }

        var accepted = Resolve(pool, title);

        // Spans that some attribute has confirmed as its own value. Nothing else may cite them as
        // evidence about a different attribute.
        var claimed = accepted.Where(c => c.IsValueMatch).ToList();

        var results = new List<TitleAttributeResult>(rules.Attributes.Count);
        var errors = new List<string>();
        var removals = new List<(int Start, int End)>();

        for (var i = 0; i < rules.Attributes.Count; i++)
        {
            var attr = rules.Attributes[i];
            var original = (cell(attr.Rule.Column) ?? "").Trim();

            results.Add(Judge(
                attr,
                values[i],
                original,
                accepted.Where(c => ReferenceEquals(c.Attr, attr)).ToList(),
                Evidence(attr, pool, accepted, claimed, title),
                removals,
                errors));
        }

        return new TitleCleanRow(
            rowNumber,
            title.Original,
            Apply(title.Original, removals),
            results,
            errors);
    }

    // ---------------------------------------------------------------------
    // Verdicts
    // ---------------------------------------------------------------------

    /// <summary>
    /// What this attribute's own scan found that is worth reporting: boundary-valid spans that no
    /// <em>other</em> attribute has confirmed as its value.
    ///
    /// <para>Deliberately taken from the whole candidate pool rather than from the accepted set.
    /// Two attributes whose unit families overlap — a RAM rule and a disk rule both knowing GB —
    /// produce competing candidates for the same "16GB", and only one of them can win a span. If
    /// evidence came from the accepted set, whichever attribute lost that arbitration would report
    /// "not in the title" about a title that plainly mentions it, and a real disagreement would
    /// disappear.</para>
    /// </summary>
    static List<Candidate> Evidence(
        CompiledAttribute attr,
        List<Candidate> pool,
        List<Candidate> accepted,
        List<Candidate> claimed,
        FoldedTitle title)
    {
        return pool
            .Where(c => ReferenceEquals(c.Attr, attr))
            .Where(c => BoundaryOk(c, accepted, title))
            // A scattered match nobody owns the gap of is not a sighting of the value at all, so it
            // is no more evidence than it was a match. Without this the two words of a colour found
            // either side of unclaimed text are reported as the title "saying" that colour — against
            // a cell that says the same thing, which reads as a conflict between a value and itself.
            .Where(c => GapsClaimed(c, accepted, title))
            .Where(c => !claimed.Any(v =>
                !ReferenceEquals(v.Attr, attr) && v.Start < c.End && c.Start < v.End))
            .ToList();
    }

    static TitleAttributeResult Judge(
        CompiledAttribute attr,
        AttributeValue? value,
        string original,
        List<Candidate> mine,
        List<Candidate> evidence,
        List<(int Start, int End)> removals,
        List<string> errors)
    {
        var rule = attr.Rule;

        if (value is null)
            return JudgeEmpty(rule, original, mine, removals);

        var hits = mine.Where(c => c.IsValueMatch).ToList();

        // A bare number has no unit and therefore no identity. The Measure rules refuse to recognise
        // one for exactly this reason — "16" is a screen size, a model name and a fragment of a model
        // code at once — and a Text or Alias attribute asking to delete an unqualified number from a
        // title is asking for the same damage. Refused per row rather than per column, so one
        // numeric value among a hundred processor models costs that row and not the other ninety-nine.
        //
        // The test is on the text that would be CUT, not on what the cell happens to hold. Deleting a
        // bare number from a title is the dangerous act; a cell reading "465" whose catalogue maps it
        // onto the title's "Ultra 5 465" is asking to delete a phrase, which is safe and is exactly
        // how the operator resolves this from the suggested fixes.
        if (rule.Kind != TitleAttributeKind.Measure && hits.Count > 0 &&
            BareNumber.IsMatch(hits[0].Match.Text.Trim()))
        {
            var message =
                $"{rule.Column}: \"{original}\" birimsiz bir sayı — başlıkta geçiyor ama model adının " +
                "parçası da olabileceği için çıkarılmadı. Başlıktaki tam ifadeyi bu değerin karşılığı " +
                "olarak ekleyin, ya da kolon bir ölçüyse Tip'ini 'Ölçü' yapıp birimini yazın.";
            errors.Add(message);

            return new TitleAttributeResult(
                rule.Column, TitleAttributeStatus.Ambiguous, original, original, original, message,
                TitleAttributeReason.BareNumber);
        }

        if (hits.Count == 0)
        {
            var said = evidence.Select(c => c.Match.Canonical).Distinct(StringComparer.Ordinal).ToList();

            // Nothing added to `errors`: a cell whose value the title never mentions is ordinary, not
            // a problem, and this row keeps its place outside the review list. The reason is carried
            // only so the fix suggester can check whether the title names this value under a spelling
            // the rule does not know — see TitleAttributeReason.SpellingUnknown.
            if (said.Count == 0)
            {
                return new TitleAttributeResult(
                    rule.Column, TitleAttributeStatus.NotInTitle, original, original,
                    Reason: TitleAttributeReason.SpellingUnknown);
            }

            var joined = string.Join(", ", said);
            var message = $"{rule.Column}: başlıkta \"{joined}\", özellikte \"{original}\"";
            errors.Add(message);

            // Nothing is removed and nothing is rewritten. Which side is right is not something this
            // tool can know, and acting on the wrong one corrupts the catalogue silently.
            return new TitleAttributeResult(
                rule.Column, TitleAttributeStatus.Conflict, original, original, joined, message,
                TitleAttributeReason.Disagreement);
        }

        var distinctKeys = hits.Select(c => c.Match.Key).Distinct(StringComparer.Ordinal).ToList();

        // A cell holding a bare number matches on the number alone, so a title carrying that number
        // against two different units leaves the unit unsettled. Guessing one would write a made-up
        // value into the catalogue.
        if (value.BareQuantity.HasValue && distinctKeys.Count > 1)
        {
            var candidates = string.Join(", ", hits.Select(c => c.Match.Canonical).Distinct(StringComparer.Ordinal));
            var message =
                $"{rule.Column}: özellikte birim yok (\"{original}\"), başlıkta birden fazla karşılık var ({candidates})";
            errors.Add(message);

            return new TitleAttributeResult(
                rule.Column, TitleAttributeStatus.Ambiguous, original, original, candidates, message,
                TitleAttributeReason.UnitUnsettled);
        }

        // The same value in more than one place, and no way to tell which one is this attribute.
        //
        // Real titles repeat a measurement constantly, and a repeat is almost never a typo — it is
        // two different things that happen to be the same size. "RTX 5070 8GB 8GB 512GB SSD" is a
        // graphics card with 8 GB of its own memory next to 8 GB of system RAM, and a RAM rule that
        // removed every match would delete the card's memory out of the title. The row above it,
        // where RAM is 24 GB, would come out perfectly — so the damage appears on some rows and not
        // others, which is the hardest kind to notice.
        //
        // The fix for the operator is to add a rule for the other column (this file has a graphics
        // memory column), after which each occurrence is claimed by its own attribute and both are
        // removed correctly. Until then this is reported, not guessed at.
        if (hits.Count > 1)
        {
            var found = hits[0].Match.Canonical;

            // Names the fix rather than the problem. The other occurrence belongs to something else
            // — a graphics card's own memory beside the system RAM — and the way to say so is a rule
            // that claims the longer phrase around it and is not allowed to remove it.
            var message =
                $"{rule.Column}: \"{found}\" başlıkta {hits.Count} kez geçiyor, hangisinin bu özellik " +
                $"olduğu belirsiz. Diğerini sahiplenen bir kural ekleyin: o kolonu Değer Listesi yapıp " +
                $"başlıktaki uzun yazımı ekleyin (ör. \"RTX 5070 {found}\") ve o satırda Çıkar ile " +
                "Düzelt'i kapalı bırakın — böylece o metin korunur, buradaki değer temizlenir";
            errors.Add(message);

            return new TitleAttributeResult(
                rule.Column, TitleAttributeStatus.Ambiguous, original, original, found, message,
                TitleAttributeReason.ValueRepeated);
        }

        var canonical = hits[0].Match.Canonical;
        var differs = !string.Equals(original, canonical, StringComparison.Ordinal);
        var status = differs && rule.Correct ? TitleAttributeStatus.Corrected : TitleAttributeStatus.Ok;

        // The pieces, not the reach: a scattered match spans text that belongs to another attribute,
        // and cutting the whole stretch would take that with it.
        if (rule.Remove)
            removals.AddRange(hits.SelectMany(c => c.Match.Spans));

        return new TitleAttributeResult(
            rule.Column,
            status,
            original,
            status == TitleAttributeStatus.Corrected ? canonical : original,
            canonical);
    }

    static TitleAttributeResult JudgeEmpty(
        TitleAttributeRule rule,
        string original,
        List<Candidate> mine,
        List<(int Start, int End)> removals)
    {
        if (!rule.FillFromTitle)
            return new TitleAttributeResult(rule.Column, TitleAttributeStatus.Empty, original, original);

        var keys = mine.Select(c => c.Match.Key).Distinct(StringComparer.Ordinal).ToList();

        // Filled only where the title carries exactly one candidate, and this is the one place the
        // tool writes a value nobody typed.
        //
        // Both halves matter. Two different values leave the answer unknown; two copies of the *same*
        // value leave it unknown which of them is this attribute — "8GB 8GB" is a graphics card's own
        // memory beside the system RAM, and filling from it would take both. A filled cell is refused
        // in exactly that situation, so an empty one has to be too, or the safer-looking case would
        // be the more destructive one.
        if (keys.Count != 1 || mine.Count != 1)
            return new TitleAttributeResult(rule.Column, TitleAttributeStatus.Empty, original, original);

        var found = mine.Where(c => string.Equals(c.Match.Key, keys[0], StringComparison.Ordinal)).ToList();

        if (rule.Remove)
            removals.AddRange(found.Select(c => (c.Start, c.End)));

        return new TitleAttributeResult(
            rule.Column,
            TitleAttributeStatus.Filled,
            original,
            found[0].Match.Canonical,
            found[0].Match.Canonical);
    }

    static bool IsValueMatch(AttributeValue? value, TitleMatch match)
    {
        if (value is null)
            return false;

        if (value.BareQuantity is { } bare)
            return match.Quantity.HasValue && Math.Abs(match.Quantity.Value - bare) < 1e-9;

        return value.Key.Length > 0 && string.Equals(value.Key, match.Key, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Span resolution
    // ---------------------------------------------------------------------

    /// <summary>
    /// Picks the non-overlapping set of spans the title actually carries.
    ///
    /// <para>Boundary validity depends on which other spans were accepted, and which spans are
    /// accepted depends on what survives validation — so this runs to a fixed point rather than in
    /// one pass. Dropping an invalid span frees whatever it was overlapping, which is why the whole
    /// pool is re-resolved instead of the invalid entries simply being struck off.</para>
    /// </summary>
    static List<Candidate> Resolve(List<Candidate> pool, FoldedTitle title)
    {
        var live = pool;

        while (true)
        {
            var accepted = Greedy(live);
            var invalid = accepted
                .Where(c => !BoundaryOk(c, accepted, title) || !GapsClaimed(c, accepted, title))
                .ToList();

            if (invalid.Count == 0)
                return accepted;

            live = live.Where(c => !invalid.Contains(c)).ToList();
        }
    }

    /// <summary>
    /// For a value the title writes in pieces, whether the text between them belongs to somebody.
    ///
    /// <para>This is the whole of the safety case for scattered matching. "Rustik siyah" may answer
    /// "Rustik 60 cm Siyah" because a width rule is removing the "60 cm" in between — the two words
    /// really are one value with another value inserted into it. The same two words either side of
    /// text no rule claims are just two words, and reading them as a colour would delete a word the
    /// operator never accounted for.</para>
    ///
    /// <para>Only <em>removing</em> matches count. A rule that recognises the gap but is not allowed
    /// to cut it leaves that text in the title, and a value cannot be said to span text that stays.</para>
    /// </summary>
    static bool GapsClaimed(Candidate candidate, List<Candidate> accepted, FoldedTitle title)
    {
        var parts = candidate.Match.Parts;
        if (parts is null || parts.Count < 2)
            return true;

        for (var i = 1; i < parts.Count; i++)
        {
            for (var at = parts[i - 1].End; at < parts[i].Start; at++)
            {
                if (!char.IsLetterOrDigit(title.Original[at]))
                    continue;

                var covered = accepted.Any(o =>
                    !ReferenceEquals(o, candidate) &&
                    o.IsValueMatch &&
                    o.Attr.Rule.Remove &&
                    o.Match.Spans.Any(s => s.Start <= at && at < s.End));

                if (!covered)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Longest span wins, and a span that says what its cell says outranks one that does not — so a
    /// disk capacity's own "512GB" beats the same characters seen by a RAM rule whose family also
    /// happens to include GB.
    ///
    /// <para>Handed out in two passes, and the first pass gives <b>each attribute at most one
    /// span</b>. Titles repeat a measurement constantly — "RTX 5070 8GB 8GB" is a card's own memory
    /// beside the system RAM — and both rules match both occurrences identically. A single greedy
    /// pass would let whichever rule sits higher take both, leaving the other with nothing and
    /// reporting a value the title plainly carries as missing. One each first, the rest afterwards.</para>
    /// </summary>
    static List<Candidate> Greedy(List<Candidate> pool)
    {
        var ordered = pool
            .OrderByDescending(c => c.IsValueMatch)
            .ThenByDescending(c => c.Match.Length)
            .ThenBy(c => c.Attr.Index)
            .ThenBy(c => c.Start)
            .ToList();

        var accepted = new List<Candidate>();
        var served = new HashSet<int>();

        // Only spans an attribute has actually confirmed are shared out here. A rule that merely
        // *could* have matched — a cache column seeing the graphics card's "8GB" because they share
        // the GB family — must not get a reserved seat ahead of the rule the value belongs to.
        foreach (var candidate in ordered)
        {
            if (!candidate.IsValueMatch || served.Contains(candidate.Attr.Index) || Overlaps(accepted, candidate))
                continue;

            accepted.Add(candidate);
            served.Add(candidate.Attr.Index);
        }

        // Whatever is still unclaimed. An attribute picking up a second span here is what surfaces a
        // repeated value as ambiguous rather than removing both copies of it.
        foreach (var candidate in ordered)
        {
            if (accepted.Contains(candidate) || Overlaps(accepted, candidate))
                continue;

            accepted.Add(candidate);
        }

        return accepted;
    }

    /// <summary>
    /// Whether two matches want the same characters — compared piece by piece.
    ///
    /// <para>The outer reach of a scattered match deliberately covers text it does not claim: the
    /// "60 cm" sitting inside "Rustik 60 cm Siyah" belongs to the width rule. Comparing outer reaches
    /// would make those two candidates rivals, and the longer one would evict the other — leaving the
    /// width in the title with nobody left to remove it.</para>
    /// </summary>
    static bool Overlaps(List<Candidate> accepted, Candidate candidate) =>
        accepted.Any(a => a.Match.Spans.Any(x =>
            candidate.Match.Spans.Any(y => x.Start < y.End && y.Start < x.End)));

    /// <summary>
    /// A span may cut into a word only where another accepted span continues from it. "1TBSSD" is
    /// two spans meeting in the middle of a token and is valid; the "16250" inside "MC16250_3" has
    /// a letter on its left that no span accounts for and is not.
    /// </summary>
    static bool BoundaryOk(Candidate candidate, List<Candidate> accepted, FoldedTitle title)
    {
        var leftOk = !title.AlphanumericBefore(candidate.Start)
                     || accepted.Any(o => !ReferenceEquals(o, candidate) && o.End == candidate.Start);

        var rightOk = !title.AlphanumericAt(candidate.End)
                      || accepted.Any(o => !ReferenceEquals(o, candidate) && o.Start == candidate.End);

        return leftOk && rightOk;
    }

    // ---------------------------------------------------------------------
    // Rewriting the title
    // ---------------------------------------------------------------------

    /// <summary>
    /// Cuts the removed spans out, right to left so the earlier offsets stay valid, then tidies what
    /// the cuts left behind: runs of whitespace collapse, and a token that is now nothing but
    /// punctuation — a dash or slash that used to separate two removed values — goes with them.
    /// </summary>
    static string Apply(string original, List<(int Start, int End)> removals)
    {
        if (removals.Count == 0)
            return original.Trim();

        var text = original;

        foreach (var (start, end) in removals.Distinct().OrderByDescending(s => s.Start))
        {
            if (start < 0 || end > text.Length || end <= start)
                continue;

            // A space, not nothing: two tokens either side of a removed span must not fuse into one.
            text = string.Concat(text.AsSpan(0, start), " ", text.AsSpan(end));
        }

        return string.Join(
            " ",
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Any(char.IsLetterOrDigit)));
    }
}
