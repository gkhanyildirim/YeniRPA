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

        /// <summary>Whether another confirmed value sits hard against this one with no separator —
        /// the "SSD" in "1TBSSD". Filled in once the accepted set is known.</summary>
        public bool Anchored { get; set; }
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

        var leftovers = TitleLeftoverReport.Build(rules, rows);
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
                .. TitleFixSuggester.SuggestSettings(rules, rows, leftovers),
            ],
            leftovers);
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

        // Which spans are written hard against another confirmed value. "1TBSSD+1TBSSD" is two disks
        // and says "SSD" twice, but the disk-type column can only say it once — so the evidence that
        // the second one is real has to come from the capacity it is glued to, not from the cell.
        foreach (var candidate in accepted)
            candidate.Anchored = Anchored(candidate, accepted);

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
                title,
                removals,
                errors));
        }

        if (rules.Source.CollapseRepeats)
            AddRepeats(title, removals);

        // A title that answers exactly one of a dozen filled cells is not a product name, and
        // cutting that one value out of it writes a mangled sentence back to the marketplace.
        var suspect = NotATitle(results);
        if (suspect)
        {
            removals.Clear();
            errors.Add(
                "Bu satırın başlığı bir ürün adına benzemiyor — dolu özelliklerden yalnızca biri " +
                "başlıkta bulundu. Başlığa dokunulmadı; satırı elden geçirin.");
        }

        return new TitleCleanRow(
            rowNumber,
            title.Original,
            Apply(title.Original, removals),
            results,
            errors,
            suspect);
    }

    /// <summary>How many filled cells a row needs before the count below means anything. Under it a
    /// rule set is simply too small to draw a conclusion from — two columns of which one matched is
    /// an ordinary row, not a broken title.</summary>
    const int EnoughCells = 4;

    /// <summary>
    /// Whether this row's title reads as something other than a product name.
    ///
    /// <para>The test is deliberately arithmetic and knows nothing about brands or categories: of the
    /// cells this row actually filled, the title carries <b>one</b>. A real export puts marketing copy
    /// in the title column — "2 YIL LENOVO TÜRKİYE GARANTİLİ - HIZLI KARGO" — and the single thing
    /// such a line has in common with the row is the brand, so the cleaner would faithfully cut the
    /// brand out and write the rest back. A genuine title matches half its columns or better.</para>
    /// </summary>
    static bool NotATitle(List<TitleAttributeResult> results)
    {
        var filled = results.Count(r => r.Status != TitleAttributeStatus.Empty);
        if (filled < EnoughCells)
            return false;

        return results.Count(r => r.Status is TitleAttributeStatus.Ok
            or TitleAttributeStatus.Corrected or TitleAttributeStatus.Filled) == 1;
    }

    /// <summary>
    /// The second of two identical words written back to back — "Lenovo Ideapad Ideapad Slim3".
    ///
    /// <para>The only place this module removes text no column claimed, which is why it is opt-in per
    /// rule set. Two things keep it from being the blunt instrument that sounds like.</para>
    ///
    /// <para><b>A repeat carrying a digit is never touched.</b> "RTX 5070 8GB 8GB" is a graphics
    /// card's own memory beside the system RAM, on the rows where the two happen to be the same size —
    /// the case this module already has a verdict and a paragraph of its own for. Collapsing it would
    /// delete the card's memory out of the title, and only on some rows, which is the hardest kind of
    /// damage to notice.</para>
    ///
    /// <para><b>Read off the original title, not the cleaned one.</b> Cleaning can push two words
    /// together that the seller never wrote together — cutting the middle out of "Ocak Siyah Ocak"
    /// leaves "Ocak Ocak", which is not a repetition anybody typed. Only what was already adjacent
    /// counts.</para>
    /// </summary>
    static void AddRepeats(FoldedTitle title, List<(int Start, int End)> removals)
    {
        var words = new List<(int Start, int End)>();
        var at = 0;

        while (at < title.Original.Length)
        {
            while (at < title.Original.Length && char.IsWhiteSpace(title.Original[at]))
                at++;

            var start = at;
            while (at < title.Original.Length && !char.IsWhiteSpace(title.Original[at]))
                at++;

            if (at > start)
                words.Add((start, at));
        }

        for (var i = 1; i < words.Count; i++)
        {
            var previous = title.Original[words[i - 1].Start..words[i - 1].End];
            var current = title.Original[words[i].Start..words[i].End];

            if (current.Any(char.IsDigit) ||
                !current.Any(char.IsLetter) ||
                !string.Equals(
                    FoldedTitle.Fold(previous), FoldedTitle.Fold(current), StringComparison.Ordinal))
            {
                continue;
            }

            // Some attribute is already cutting these characters. A second span over the same text
            // would make Apply cut twice from shifting offsets and take a neighbour with it.
            if (removals.Any(r => r.Start < words[i].End && words[i].Start < r.End))
                continue;

            removals.Add(words[i]);
        }
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
            // A bare number nothing supports is not a sighting of anything — the title wrote a
            // number, and only the unit would have made it this value.
            .Where(c => BareSupported(c, accepted))
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
        FoldedTitle title,
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
            // The title's own words. A conflict is reported so a person can read the two sides
            // against each other, and quoting the rule's canonical spelling for the title's half
            // shows them a phrase the title may not contain — it also feeds the "merge these two
            // spellings" fix, which has to fold in what the title actually wrote.
            var said = evidence.Select(c => c.Match.Text).Distinct(StringComparer.Ordinal).ToList();

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
        // How many occurrences the cell itself accounts for. One, normally — but a disk column
        // reading "1 TB + 1 TB" is a machine with two of them, and the second "1TBSSD" in its title
        // is not a repeat nobody asked for. The guard is not loosened by this, it is sharpened: the
        // number is read off the row rather than assumed.
        //
        // What counts as an occurrence is settled first. Two spellings of one alias group written
        // side by side name the product once, at length: "Gaming Laptop" and "Gaming Notebook" are
        // one product type, not two, and counting their words separately reported a repeat the title
        // never had — under the group's canonical name ("Notebook"), which in the first of those
        // titles does not appear at all. Measures keep the old count: "8GB 8GB" really is two
        // numbers, and so is any spelling written twice in a row.
        var counted = rule.Kind == TitleAttributeKind.Alias ? OnePhrase(title, hits) : hits;

        var repeated = counted
            .GroupBy(c => c.Match.Key, StringComparer.Ordinal)
            // What the cell says, or what the title's own compounds prove — whichever is larger. A
            // disk-type column says "SSD" once however many disks the machine has, and
            // "1TBSSD+1TBSSD" carries two confirmed capacities each with an SSD written onto it.
            // "RTX 5070 8GB 8GB" is untouched by this: neither of those is glued to anything, so the
            // count stays at what the cell says and the repeat is still reported.
            .Any(g => g.Count() > Math.Max(value.Allowed(g.Key), g.Count(c => c.Anchored)));

        if (repeated)
        {
            // What the title wrote, not what the rule calls it. The group's canonical spelling is
            // often absent from the title — an operator told that "Notebook" occurs twice in a title
            // holding neither of those words has been sent looking for something that is not there.
            var said = counted
                .Select(c => c.Match.Text)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var joined = string.Join(", ", said);
            var found = string.Join("\", \"", said);

            // Names the fix rather than the problem. The other occurrence belongs to something else
            // — a graphics card's own memory beside the system RAM — and the way to say so is a rule
            // that claims the longer phrase around it and is not allowed to remove it.
            var message =
                $"{rule.Column}: \"{found}\" başlıkta {counted.Count} kez geçiyor, hangisinin bu özellik " +
                $"olduğu belirsiz. Diğerini sahiplenen bir kural ekleyin: o kolonu Değer Listesi yapıp " +
                $"başlıktaki uzun yazımı ekleyin (ör. \"RTX 5070 {said[0]}\") ve o satırda Çıkar ile " +
                "Düzelt'i kapalı bırakın — böylece o metin korunur, buradaki değer temizlenir";
            errors.Add(message);

            return new TitleAttributeResult(
                rule.Column, TitleAttributeStatus.Ambiguous, original, original, joined, message,
                TitleAttributeReason.ValueRepeated);
        }

        var canonical = hits[0].Match.Canonical;
        var differs = !string.Equals(original, canonical, StringComparison.Ordinal);

        // A rounded match agrees about the product and disagrees about the precision, and the cell is
        // the precise one. Rewriting 745 mm as the title's "75 cm" would delete a figure nobody asked
        // to lose — so the title is cleaned and the cell is left exactly as it was.
        var rounded = value.PartList.Any(part => Rounded(part, hits[0].Match));

        // A cell holding several measurements is never rewritten. The canonical form of one match is
        // one value, and writing it back over "1 TB + 1 TB" would throw the second disk away — the
        // title is cleaned and the cell keeps everything it said.
        var status = differs && rule.Correct && !rounded && value.Parts is null
            ? TitleAttributeStatus.Corrected
            : TitleAttributeStatus.Ok;

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

        // Any of them: a disk column reading "2 TB + 1 TB" asserts both, and a title writing either
        // is writing something the row says it has.
        return value.PartList.Any(part =>
            part.Key.Length > 0 && string.Equals(part.Key, match.Key, StringComparison.Ordinal) ||
            Rounded(part, match));
    }

    /// <summary>
    /// Whether the two say the same thing once the cell is read to the precision the title wrote.
    ///
    /// <para>A title rounds: 745 mm of width is written "75 cm". Refusing that is not caution, it is
    /// a false conflict — and an expensive one, because the span then belongs to nobody and every
    /// other rule sharing the unit family reports its own disagreement about the same "75 cm".</para>
    ///
    /// <para><b>The title may be less precise, never differently precise.</b> Rounding happens only
    /// to the number of decimals the title itself used, so "15,7" against a cell of 15,6 stays a
    /// conflict — the title wrote a decimal, and it is a different one.</para>
    /// </summary>
    /// <para>True only where the rounding was <b>needed</b>. Two quantities that are already equal in
    /// the base unit — a cell of 1024 GB against a title's "1TB" — match on their key and are an
    /// ordinary agreement about spelling, which <c>Düzelt</c> is entitled to act on.</para>
    static bool Rounded(AttributeValue value, TitleMatch match) =>
        value.BaseQuantity is { } cell && match.BaseQuantity is { } said &&
        Math.Abs(cell - said) > 1e-9 &&
        Math.Round(cell, match.Decimals, MidpointRounding.AwayFromZero) ==
        Math.Round(said, match.Decimals, MidpointRounding.AwayFromZero);

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
                .Where(c => !BoundaryOk(c, accepted, title) ||
                            !GapsClaimed(c, accepted, title) ||
                            !BareSupported(c, accepted))
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
            // A value the title spelled out with its unit outranks the same value read off a bare
            // number, wherever the two sit — the written one is evidence in its own right.
            .ThenBy(c => c.Match.Bare)
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
    /// A number the title wrote without its unit stands only where another confirmed value is glued
    /// straight onto it.
    ///
    /// <para>This is the whole safety case for reading "512SSD" as 512 GB of SSD. The unit is what
    /// normally tells a measurement apart from a model number, and here there is none — so the
    /// evidence has to come from somewhere else, and the only thing available is that the characters
    /// touching the number are themselves a confirmed value of some other attribute. Two values
    /// written with nothing between them is a thing titles do constantly ("1TBSSD" already relied on
    /// it); a number sitting alone in the middle of a title is not.</para>
    ///
    /// <para>So "Pro Max 16" is refused — its "16" has spaces either side and nothing to lean on —
    /// and it is refused even on a row whose screen really is 16 inches, which is the case that makes
    /// this worth writing down. <see cref="BoundaryOk"/> covers the other direction, the "16" inside
    /// "MC16250_3".</para>
    /// </summary>
    /// <summary>Whether another accepted, confirmed value ends exactly where this one begins or
    /// begins exactly where it ends — the two halves of "1TBSSD".</summary>
    static bool Anchored(Candidate candidate, List<Candidate> accepted) =>
        accepted.Any(other =>
            !ReferenceEquals(other, candidate) &&
            other.IsValueMatch &&
            other.Match.Spans.Any(s => s.End == candidate.Start || s.Start == candidate.End));

    /// <summary>
    /// One occurrence per phrase. Two spellings of the same alias group with nothing but separators
    /// between them are the product type written out at length — "Gaming Laptop", "Gaming Notebook",
    /// "Dizüstü Bilgisayar Notebook" — and the repeat guard must see one of them, not two.
    /// </summary>
    /// <remarks>
    /// The two spans have to be spelled differently. The same word twice in a row is a repeat under
    /// any reading, and folding it would hide exactly the case the guard exists for.
    /// </remarks>
    static List<Candidate> OnePhrase(FoldedTitle title, List<Candidate> hits)
    {
        var kept = new List<Candidate>();

        foreach (var candidate in hits.OrderBy(c => c.Start))
        {
            var previous = kept.Count > 0 ? kept[^1] : null;

            var joins =
                previous is not null &&
                string.Equals(previous.Match.Key, candidate.Match.Key, StringComparison.Ordinal) &&
                !string.Equals(previous.Match.Text, candidate.Match.Text, StringComparison.OrdinalIgnoreCase) &&
                OnlySeparators(title.Original, previous.End, candidate.Start);

            if (!joins)
                kept.Add(candidate);
        }

        return kept;
    }

    /// <summary>Whether the stretch between two matches is separator characters and nothing else.</summary>
    static bool OnlySeparators(string title, int from, int to)
    {
        if (from > to || to > title.Length)
            return false;

        for (var i = from; i < to; i++)
        {
            if (!char.IsWhiteSpace(title[i]) && title[i] is not ('-' or '_' or '/' or '.'))
                return false;
        }

        return true;
    }

    static bool BareSupported(Candidate candidate, List<Candidate> accepted)
    {
        if (!candidate.Match.Bare)
            return true;

        return accepted.Any(other =>
            !ReferenceEquals(other, candidate) &&
            other.IsValueMatch &&
            !other.Match.Bare &&
            other.Match.Spans.Any(s => s.Start == candidate.End || s.End == candidate.Start));
    }

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
