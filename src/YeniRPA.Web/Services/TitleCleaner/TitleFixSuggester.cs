using System.Security.Cryptography;
using System.Text;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>
/// Turns the review list into a short list of decisions.
///
/// <para>The review table reports problems; it does not resolve them. Working through it meant
/// reading a row, working out which rule was wrong, finding that rule in a table of forty and
/// editing the right cell — and then doing it again for the next row saying the same thing. On a real
/// export eighteen review rows were <b>three scenarios</b>: the same "Gaming Laptop ≠ Oyun
/// Bilgisayarı" on 78 rows, the same "FHD+ ≠ Full-HD+" on 94, the same repeated "8GB" on 4. So rows
/// are grouped into scenarios and each scenario gets one proposed rule change.</para>
///
/// <para><b>Only changes that generalise are offered.</b> A fix here is written into the rule set and
/// therefore applies to the whole file, so "the cell on this row is wrong" — a data error in one
/// product — is never suggested. Those stay in the review list and go out with the workbook, which is
/// where a per-row correction belongs.</para>
///
/// <para>Nothing here decides anything on its own: it proposes, the operator picks, and the rule set
/// is only written when they press Save.</para>
/// </summary>
public static class TitleFixSuggester
{
    /// <summary>Words the proposed phrase may reach back over. Enough for "Ultra 5 465" and
    /// "RTX 5070 8GB"; short enough that a wrong guess is obvious on the card.</summary>
    const int MaxPhraseWords = 3;

    /// <summary>Scenarios offered at once. Beyond this the list stops being a decision and starts
    /// being a second review table.</summary>
    const int MaxFixes = 40;

    public static IReadOnlyList<TitleFix> Suggest(CompiledRuleSet rules, IReadOnlyList<TitleCleanRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rows);

        var scenarios = new Dictionary<string, Scenario>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            foreach (var attribute in row.Attributes)
            {
                if (attribute.Reason == TitleAttributeReason.None)
                    continue;

                var key = string.Join(
                    '',
                    attribute.Reason, attribute.Column, attribute.TitleSaid ?? "", attribute.OriginalValue);

                if (!scenarios.TryGetValue(key, out var scenario))
                {
                    scenario = new Scenario(attribute, row, key);
                    scenarios[key] = scenario;
                }

                scenario.Rows++;
            }
        }

        var fixes = new List<TitleFix>();

        foreach (var scenario in scenarios.Values.OrderByDescending(s => s.Rows))
        {
            var proposed = Propose(rules, scenario);
            if (proposed is not null)
                fixes.Add(proposed);

            if (fixes.Count >= MaxFixes)
                break;
        }

        return fixes;
    }

    sealed class Scenario(TitleAttributeResult attribute, TitleCleanRow row, string key)
    {
        public TitleAttributeResult Attribute { get; } = attribute;

        /// <summary>The first row that showed this problem — what the card previews.</summary>
        public TitleCleanRow Sample { get; } = row;

        public string Key { get; } = key;
        public int Rows { get; set; }
    }

    // ---------------------------------------------------------------------

    static TitleFix? Propose(CompiledRuleSet rules, Scenario scenario)
    {
        var attribute = scenario.Attribute;
        var rule = rules.Attributes.FirstOrDefault(a =>
            string.Equals(a.Rule.Column, attribute.Column, StringComparison.Ordinal))?.Rule;

        if (rule is null)
            return null;

        return attribute.Reason switch
        {
            TitleAttributeReason.Disagreement => ProposeMerge(rules, scenario, rule),
            TitleAttributeReason.ValueRepeated => ProposeProtect(rules, scenario, rule),
            TitleAttributeReason.BareNumber => ProposeAdopt(rules, scenario, rule),
            TitleAttributeReason.SpellingUnknown => ProposeAdoptSpelling(rules, scenario, rule),
            _ => null,
        };
    }

    /// <summary>
    /// The title names this value under a spelling the rule does not carry. Adopt the title's phrase
    /// into the cell value's group, which both cuts it out of the title and lets Düzelt rewrite the
    /// cell to the canonical spelling.
    ///
    /// <para>This one arrives from a row that reported <em>nothing wrong</em> — every attribute the
    /// title simply does not mention comes through here — so the narrowing is what makes it a
    /// decision rather than a second review table. Two filters do it: the title has to share a word
    /// with the cell (<see cref="PhraseSharingAWord"/>), and the change has to actually alter what
    /// this row's title becomes. A coincidental shared word earns no card.</para>
    /// </summary>
    static TitleFix? ProposeAdoptSpelling(CompiledRuleSet rules, Scenario scenario, TitleAttributeRule rule)
    {
        // A unit family is not a catalogue: a measured attribute is matched by number and unit, and a
        // phrase has nowhere to live on it.
        if (rule.Kind == TitleAttributeKind.Measure)
            return null;

        var phrase = PhraseSharingAWord(rules, scenario) ?? MisspeltWord(scenario);
        if (phrase is null)
            return null;

        var fix = Build(
            rules, scenario, rule.Column,
            TitleFixKind.AdoptPhrase,
            $"{rule.Column}: özellikte \"{scenario.Attribute.OriginalValue}\", başlıkta bu yazımla geçmiyor",
            "Başlıktaki ifadeyi bu değerin yazımı olarak ekle",
            phrase,
            ApplyAdopt,
            warning: rule.Kind != TitleAttributeKind.Alias
                ? $"{rule.Column} kolonunun tipi Değer Listesi olarak değişir."
                : null);

        // Offered only when it changes the outcome. Compared against what this row's title already
        // becomes, not against the raw title — other rules have their own effect on it.
        return fix is not null &&
               !string.Equals(fix.SampleAfter, scenario.Sample.CleanTitle, StringComparison.Ordinal)
            ? fix
            : null;
    }

    /// <summary>
    /// The cell and the title spell one thing two ways. Fold the title's spelling into the cell
    /// value's alias group.
    /// </summary>
    static TitleFix? ProposeMerge(CompiledRuleSet rules, Scenario scenario, TitleAttributeRule rule)
    {
        var attribute = scenario.Attribute;
        var said = (attribute.TitleSaid ?? "").Trim();

        if (said.Length == 0 || attribute.OriginalValue.Trim().Length == 0)
            return null;

        // Only when the title offered exactly one value. Two means the title names two different
        // known values for one attribute, and which of them the cell is supposed to be is not
        // something to guess at — that scenario keeps its place in the review list.
        if (said.Contains(", ", StringComparison.Ordinal))
            return null;

        // A catalogue is what makes the merge expressible; a plain text column has nowhere to put it.
        if (rule.Kind != TitleAttributeKind.Alias)
            return null;

        return Build(
            rules, scenario, rule.Column,
            TitleFixKind.MergeAlias,
            $"{rule.Column}: başlıkta \"{said}\", özellikte \"{attribute.OriginalValue}\"",
            "Aynı şeyin iki yazımı — değer listesinde birleştir",
            said,
            ApplyMerge);
    }

    /// <summary>
    /// The value appears more than once and one of the occurrences belongs to something else. Give
    /// the longer phrase around it to a column that may not remove it.
    /// </summary>
    static TitleFix? ProposeProtect(CompiledRuleSet rules, Scenario scenario, TitleAttributeRule rule)
    {
        var attribute = scenario.Attribute;
        var phrase = PhraseAround(rules, scenario, out var occurrences);

        if (phrase is null || occurrences < 2)
            return null;

        var owner = FindOwner(rules, phrase) ?? "";

        return Build(
            rules, scenario, owner,
            TitleFixKind.ProtectPhrase,
            $"{rule.Column}: \"{attribute.TitleSaid}\" başlıkta {occurrences} kez geçiyor",
            owner.Length > 0
                ? $"\"{phrase}\" ifadesini {owner} kolonuna ver ve koru"
                : $"\"{phrase}\" ifadesini koruyacak kolonu seçin",
            phrase,
            ApplyProtect,
            needsColumnChoice: owner.Length == 0,
            warning: owner.Length > 0
                ? $"{owner} kolonunun Çıkar ve Düzelt kutuları kapatılır — o kolon artık başlıktan bir şey silmez."
                : null);
    }

    /// <summary>
    /// A bare number in the cell. Treat the title's full phrase as that value's spelling, so what
    /// gets removed is a phrase rather than an unqualified number.
    /// </summary>
    static TitleFix? ProposeAdopt(CompiledRuleSet rules, Scenario scenario, TitleAttributeRule rule)
    {
        var phrase = PhraseAround(rules, scenario, out _);

        if (phrase is null ||
            string.Equals(phrase, scenario.Attribute.OriginalValue.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        return Build(
            rules, scenario, rule.Column,
            TitleFixKind.AdoptPhrase,
            $"{rule.Column}: hücrede birimsiz sayı (\"{scenario.Attribute.OriginalValue}\"), " +
            $"başlıkta \"{phrase}\"",
            $"Başlıktaki \"{phrase}\" ifadesini bu değerin karşılığı say",
            phrase,
            ApplyAdopt,
            warning: rule.Kind != TitleAttributeKind.Alias
                ? $"{rule.Column} kolonunun tipi Değer Listesi olarak değişir."
                : null);
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// Fills in the parts every card shares, including the before/after preview — which is computed
    /// by running the real engine with the fix applied, so what the operator is shown is what they
    /// will get.
    /// </summary>
    static TitleFix? Build(
        CompiledRuleSet rules,
        Scenario scenario,
        string targetColumn,
        TitleFixKind kind,
        string problem,
        string action,
        string value,
        Func<TitleRuleSet, TitleFix, TitleRuleSet> apply,
        bool needsColumnChoice = false,
        string? warning = null,
        bool preselected = true,
        string? cellValue = null)
    {
        var id = Identify(scenario.Key, kind);

        // A protector's phrase is its own canonical — it is not standing in for a cell value, it is
        // a piece of the title being kept whole. A category-type card passes its own, because the
        // group it joins is headed by the RuleSet's canonical rather than by whatever this row's
        // cell happens to say.
        var cell = cellValue
            ?? (kind == TitleFixKind.ProtectPhrase ? "" : scenario.Attribute.OriginalValue.Trim());

        var draft = new TitleFix(
            id, kind, scenario.Attribute.Column, targetColumn, problem, action, value, cell,
            scenario.Rows, scenario.Sample.OriginalTitle, "", needsColumnChoice, warning, preselected);

        var after = scenario.Sample.CleanTitle;

        if (!needsColumnChoice)
        {
            try
            {
                var applied = CompiledRuleSet.Compile(apply(rules.Source, draft));
                after = Rerun(applied, scenario.Sample).CleanTitle;
            }
            catch (InvalidOperationException)
            {
                // A fix the engine will not accept is not offered at all, rather than offered with a
                // preview nobody can trust.
                return null;
            }
        }

        return draft with { SampleAfter = after };
    }

    /// <summary>
    /// Cleans one sample row again under a changed rule set. The row carries its own attribute
    /// values, so the cells can be read back off it without the file.
    /// </summary>
    static TitleCleanRow Rerun(CompiledRuleSet rules, TitleCleanRow sample)
    {
        var cells = sample.Attributes.ToDictionary(
            a => a.Column, a => a.OriginalValue, StringComparer.OrdinalIgnoreCase);

        return TitleCleanBuilder.CleanRow(
            rules, sample.RowNumber, sample.OriginalTitle,
            name => cells.GetValueOrDefault(name.Trim(), ""));
    }

    /// <summary>
    /// A scenario's identity, stable across recomputation. The apply request re-derives the
    /// suggestions server-side and matches on this, so a position in a list would not do.
    /// </summary>
    static string Identify(string key, TitleFixKind kind)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(kind + "" + key));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    // ---------------------------------------------------------------------
    // Settings the leftover report says are in the way
    // ---------------------------------------------------------------------

    /// <summary>
    /// Cards drawn from <see cref="TitleLeftoverReport"/>: a word is still in the title, the column's
    /// own cell carries it, and one setting stands between the two.
    ///
    /// <para>One card per column rather than per word. A material column with its removal switched
    /// off leaves "Emaye", "Cam" and "Seramik" behind, and that is one decision about one column, not
    /// three about three words.</para>
    /// </summary>
    public static IReadOnlyList<TitleFix> SuggestSettings(
        CompiledRuleSet rules,
        IReadOnlyList<TitleCleanRow> rows,
        IReadOnlyList<TitleLeftover> leftovers)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(leftovers);

        var fixes = new List<TitleFix>();

        var groups = leftovers
            .Where(l => l.Column is not null && Switch(l.Cause) is not null)
            .GroupBy(l => l.Column!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(l => l.Rows));

        foreach (var group in groups)
        {
            var column = group.Key;

            var rule = rules.Attributes.FirstOrDefault(a =>
                string.Equals(a.Rule.Column, column, StringComparison.Ordinal))?.Rule;

            if (rule is null)
                continue;

            // Every switch this column is held back by, together. One of them alone often changes
            // nothing — see TitleFixKind.EnableMatching.
            //
            // Removal is read off the rule rather than off the report. The report names the reason a
            // word was not matched; a column that may not remove is held back on top of that, and
            // fixing only the matching would leave the card with nothing to show for itself.
            var switches = group
                .Select(l => Switch(l.Cause)!)
                .Concat(rule.Remove ? [] : new[] { SwitchRemove })
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            var sample = rows.FirstOrDefault(r =>
                string.Equals(r.OriginalTitle, group.First().Sample, StringComparison.Ordinal));

            var attribute = sample?.Attributes.FirstOrDefault(a =>
                string.Equals(a.Column, column, StringComparison.Ordinal));

            if (sample is null || attribute is null)
                continue;

            var words = string.Join(", ",
                group.OrderByDescending(l => l.Rows).Select(l => $"\"{l.Word}\"").Take(4));
            var names = string.Join(" ve ", switches.Select(Label));
            var key = string.Join('', "setting", column);

            var proposed = Build(
                rules, new Scenario(attribute, sample, key) { Rows = group.Sum(l => l.Rows) },
                column, TitleFixKind.EnableMatching,
                $"{column}: başlıkta {words} duruyor, hücrede de var",
                $"Bu kolonda {names} ayarını aç",
                // Nothing to type: the card turns switches. An editable box would invite an edit
                // that could not be honoured.
                "",
                ApplyEnable,
                // The switches ride in CellValue rather than Value: CellValue is not rendered as an
                // editable box, and these are not something to hand-edit.
                cellValue: string.Join('|', switches));

            if (proposed is not null &&
                !string.Equals(proposed.SampleAfter, sample.CleanTitle, StringComparison.Ordinal))
            {
                fixes.Add(proposed);
            }

            if (fixes.Count >= MaxFixes)
                break;
        }

        return fixes;
    }

    const string SwitchRemove = "remove";
    const string SwitchSuffix = "suffix";
    const string SwitchPartial = "partial";

    static string? Switch(TitleLeftoverCause cause) => cause switch
    {
        TitleLeftoverCause.RemoveOff => SwitchRemove,
        TitleLeftoverCause.NeedsSuffix => SwitchSuffix,
        TitleLeftoverCause.NeedsPartial => SwitchPartial,
        _ => null,
    };

    static string Label(string name) => name switch
    {
        SwitchRemove => "\"Çıkar\"",
        SwitchSuffix => "\"Ek\"",
        _ => "\"Kısmi\"",
    };

    /// <summary>Turns on what the card named and nothing else — a switch already on stays on.</summary>
    static TitleRuleSet ApplyEnable(TitleRuleSet set, TitleFix fix)
    {
        var switches = fix.CellValue.Split('|', StringSplitOptions.RemoveEmptyEntries);

        return WithRule(set, fix.TargetColumn, rule => rule with
        {
            Remove = rule.Remove || switches.Contains(SwitchRemove, StringComparer.Ordinal),
            AllowSuffix = rule.AllowSuffix || switches.Contains(SwitchSuffix, StringComparer.Ordinal),
            AllowPartial = rule.AllowPartial || switches.Contains(SwitchPartial, StringComparer.Ordinal),
        });
    }

    // ---------------------------------------------------------------------
    // The marketplace's own category rules
    // ---------------------------------------------------------------------

    /// <summary>How a product-type column is recognised in the operator's rule set, folded. Matched
    /// as a prefix, because a product file names the column for its locale: "Ürün Tipi (tr_TR)".</summary>
    const string TypeColumnPrefix = "urun tipi";

    /// <summary>
    /// Cards drawn from the RuleSet workbook: a product type the title names and the marketplace
    /// defines, offered as a group for the product-type column's catalogue.
    ///
    /// <para>Kept apart from <see cref="Suggest"/> because it starts somewhere else. Every other card
    /// begins with a row that reported a problem; these begin with the RuleSet, and would be found
    /// even on a file where nothing is wrong — the column simply does not know the vocabulary yet.</para>
    ///
    /// <para><b>Category is checked, never corrected.</b> Where the RuleSet files a type under a
    /// different category from the one the file declares, the card still appears — that is how the
    /// operator learns of it — but it arrives unticked and says so. Which of the two is right is not
    /// something a title cleaner can know.</para>
    /// </summary>
    public static IReadOnlyList<TitleFix> SuggestCategoryTypes(
        CompiledRuleSet rules,
        IReadOnlyList<TitleCleanRow> rows,
        IReadOnlyList<CategoryTypeRule>? categoryRules,
        string? fileCategory)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rows);

        if (categoryRules is null || categoryRules.Count == 0)
            return [];

        var target = rules.Attributes.FirstOrDefault(a =>
            FoldedTitle.Fold(a.Rule.Column).StartsWith(TypeColumnPrefix, StringComparison.Ordinal))?.Rule;

        if (target is null)
            return [];

        var known = target.AliasGroups
            .Where(g => g.Count > 0)
            .Select(g => FoldedTitle.Fold(g[0]))
            .ToHashSet(StringComparer.Ordinal);

        // Longest first, so a title carrying "Tekli İndüksiyon Ocak" is not claimed by a rule that
        // only knows "İndüksiyon Ocak" — the same precedence the alias matcher uses.
        var spellings = categoryRules
            .SelectMany(rule => rule.Types.Select(type => (Folded: FoldedTitle.Fold(type), Type: type, Rule: rule)))
            .Where(s => s.Folded.Length > 0 && !known.Contains(FoldedTitle.Fold(s.Rule.Types[0])))
            .OrderByDescending(s => s.Folded.Length)
            .ToList();

        if (spellings.Count == 0)
            return [];

        var scenarios = new Dictionary<string, (Scenario Scenario, string Spelling, CategoryTypeRule Rule)>(
            StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var attribute = row.Attributes.FirstOrDefault(a =>
                string.Equals(a.Column, target.Column, StringComparison.Ordinal));

            if (attribute is null)
                continue;

            var folded = FoldedTitle.Fold(row.OriginalTitle);
            if (folded.Length == 0)
                continue;

            var hit = spellings.FirstOrDefault(s => folded.Contains(s.Folded, StringComparison.Ordinal));
            if (hit.Rule is null)
                continue;

            // Keyed on the rule's own canonical and category rather than on the spelling that was
            // found: two titles writing the same type two ways are one decision, not two.
            var key = string.Join('', TypeColumnPrefix, hit.Rule.Types[0], hit.Rule.CategoryTr);

            if (scenarios.TryGetValue(key, out var existing))
                existing.Scenario.Rows++;
            else
                scenarios[key] = (new Scenario(attribute, row, key) { Rows = 1 }, hit.Type, hit.Rule);
        }

        var fixes = new List<TitleFix>();

        foreach (var (scenario, spelling, rule) in scenarios.Values.OrderByDescending(s => s.Scenario.Rows))
        {
            var proposed = ProposeCategoryType(rules, scenario, target, spelling, rule, fileCategory);
            if (proposed is not null)
                fixes.Add(proposed);

            if (fixes.Count >= MaxFixes)
                break;
        }

        return fixes;
    }

    static TitleFix? ProposeCategoryType(
        CompiledRuleSet rules,
        Scenario scenario,
        TitleAttributeRule target,
        string spelling,
        CategoryTypeRule rule,
        string? fileCategory)
    {
        var covers = CategoryRuleStore.Covers(rule, fileCategory);

        var warnings = new List<string>();

        if (!covers)
        {
            warnings.Add(
                $"Bu tip RuleSet'te \"{rule.CategoryTr}\" altında tanımlı" +
                (string.IsNullOrWhiteSpace(fileCategory)
                    ? ", dosyanın kategorisi okunamadı."
                    : $", dosyanın kategorisi \"{fileCategory}\"."));
        }

        if (target.Kind != TitleAttributeKind.Alias)
            warnings.Add($"{target.Column} kolonunun tipi Değer Listesi olarak değişir.");

        // Spellings that only differ by case or by a Turkish i fold to one thing for the matcher, so
        // listing both would show the operator a proposal half of which does nothing.
        var spellings = Distinct(rule.Types);

        var fix = Build(
            rules, scenario, target.Column,
            TitleFixKind.AdoptCategoryType,
            $"{target.Column}: başlıkta \"{spelling}\" — RuleSet bunu \"{rule.CategoryTr}\" altında tanıyor",
            spellings.Count > 1
                ? $"RuleSet'teki {spellings.Count} yazımı Değer Listesi'ne ekle"
                : "Değer Listesi'ne ekle",
            // The cell format the operator already reads in the Değerler box, so trimming a spelling
            // off the proposal here works the same way as editing that box.
            string.Join("|", spellings),
            ApplyCategoryType,
            warning: warnings.Count > 0 ? string.Join(" ", warnings) : null,
            preselected: covers,
            cellValue: spellings[0]);

        return fix is not null && Changes(rules, scenario, target.Column, fix) ? fix : null;
    }

    /// <summary>
    /// Whether taking this card would change anything on its sample row.
    ///
    /// <para>Both halves count. The title is the obvious one, but a card can be worth taking for the
    /// cell alone — a text column that already cuts "Ankastre Ocak" out of the title still writes
    /// "Ankastre ocak" back into the catalogue until the marketplace's own spelling is adopted.</para>
    /// </summary>
    static bool Changes(CompiledRuleSet rules, Scenario scenario, string column, TitleFix fix)
    {
        TitleCleanRow after;
        try
        {
            after = Rerun(CompiledRuleSet.Compile(ApplyCategoryType(rules.Source, fix)), scenario.Sample);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!string.Equals(after.CleanTitle, scenario.Sample.CleanTitle, StringComparison.Ordinal))
            return true;

        var before = Value(scenario.Sample, column);
        return !string.Equals(Value(after, column), before, StringComparison.Ordinal);
    }

    static string Value(TitleCleanRow row, string column) =>
        row.Attributes
            .FirstOrDefault(a => string.Equals(a.Column, column, StringComparison.Ordinal))?.Value ?? "";

    /// <summary>The spellings that are distinct once folded, first occurrence kept — so the group's
    /// canonical stays at its head.</summary>
    static List<string> Distinct(IReadOnlyList<string> spellings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();

        foreach (var spelling in spellings)
        {
            if (spelling.Trim().Length > 0 && seen.Add(FoldedTitle.Fold(spelling)))
                kept.Add(spelling.Trim());
        }

        return kept;
    }

    // ---------------------------------------------------------------------
    // Reading a phrase out of the title
    // ---------------------------------------------------------------------

    /// <summary>
    /// The words around the value in the sample title, reaching left over whatever no other rule has
    /// claimed. "8GB" in "RTX 5070  8GB 8GB" becomes "RTX 5070 8GB"; "465" in "Core Ultra 5 465"
    /// becomes "Ultra 5 465".
    ///
    /// <para>A proposal, not a conclusion — the card lets the operator correct it before it is
    /// applied, which is why reaching a word too far is cheap.</para>
    /// </summary>
    static string? PhraseAround(CompiledRuleSet rules, Scenario scenario, out int occurrences)
    {
        occurrences = 0;

        var title = scenario.Sample.OriginalTitle;
        var needle = scenario.Attribute.OriginalValue.Trim();
        if (needle.Length == 0 || title.Length == 0)
            return null;

        var words = SplitWords(title);
        var folded = FoldedTitle.Fold(needle);
        var claimed = ClaimedWords(rules, scenario);

        // Compared with the spaces taken out. A cell writes "8 GB" and the title writes "8GB"; they
        // are the same value, and the gap is not what decides whether the phrase can be found. The
        // pair check covers the mirror case, a title that writes the value across two words.
        var target = Squeeze(folded);
        var at = -1;
        var end = -1;

        for (var i = 0; i < words.Count; i++)
        {
            var one = Squeeze(FoldedTitle.Fold(words[i]));

            if (one.Contains(target, StringComparison.Ordinal))
            {
                occurrences++;
                if (at < 0) (at, end) = (i, i);
                continue;
            }

            if (i + 1 < words.Count &&
                (one + Squeeze(FoldedTitle.Fold(words[i + 1]))).Contains(target, StringComparison.Ordinal))
            {
                occurrences++;
                if (at < 0) (at, end) = (i, i + 1);
            }
        }

        if (at < 0)
            return null;

        var from = at;
        while (from > 0 && end - from + 1 < MaxPhraseWords)
        {
            var previous = FoldedTitle.Fold(words[from - 1]);
            if (previous.Length == 0 || claimed.Contains(previous))
                break;

            from--;
        }

        return from == at ? null : string.Join(' ', words.Skip(from).Take(end - from + 1));
    }

    /// <summary>
    /// Words another rule is going to cut out of this title. A proposed phrase stops at them, because
    /// a phrase built over text that gets removed would never match again once the rule set runs.
    ///
    /// <para>Only rules that actually matched this row <em>and</em> are allowed to remove count.
    /// Taking it from the cell values instead would treat a value the title never carried as claimed
    /// — which is what stopped "RTX 5070 8GB" being proposed, since the graphics card's cell reads
    /// "GeForce RTX 5070" and that phrase is not in the title at all.</para>
    /// </summary>
    static HashSet<string> ClaimedWords(CompiledRuleSet rules, Scenario scenario)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var other in scenario.Sample.Attributes)
        {
            if (string.Equals(other.Column, scenario.Attribute.Column, StringComparison.Ordinal))
                continue;

            if (other.Status is not (TitleAttributeStatus.Ok or TitleAttributeStatus.Corrected
                or TitleAttributeStatus.Filled))
            {
                continue;
            }

            var otherRule = rules.Attributes.FirstOrDefault(a =>
                string.Equals(a.Rule.Column, other.Column, StringComparison.Ordinal))?.Rule;

            if (otherRule is null || !otherRule.Remove)
                continue;

            foreach (var word in SplitWords(other.OriginalValue.Trim()).Concat(SplitWords(other.TitleSaid ?? "")))
                claimed.Add(FoldedTitle.Fold(word));
        }

        return claimed;
    }

    /// <summary>
    /// The phrase in the title that appears to be this cell value written another way — "İndüksiyon
    /// Ocak" against a cell reading "İndüksiyonlu ocak".
    ///
    /// <para><b>Nothing here is approximate.</b> The ban on fuzzy matching in
    /// <see cref="AttributeMatcher"/> applies just as much to a proposal, because an approved
    /// proposal becomes a catalogue spelling that deletes title text forever after. So the phrase is
    /// pinned by two exact rules rather than guessed at:</para>
    ///
    /// <list type="number">
    ///   <item>The anchor is the <em>last</em> title word that equals one of the cell's own words,
    ///   character for character once folded. Turkish builds these phrases toward their noun, so the
    ///   shared word ends the phrase rather than starting it.</item>
    ///   <item>The length is the cell value's own word count. Reaching until something stops it would
    ///   be a guess; taking the shape of the value it stands in for is not.</item>
    /// </list>
    ///
    /// <para>A wrong phrase is still cheap — the card shows it in an editable box beside a before and
    /// after computed by running the real engine.</para>
    /// </summary>
    static string? PhraseSharingAWord(CompiledRuleSet rules, Scenario scenario)
    {
        var title = scenario.Sample.OriginalTitle;
        var value = scenario.Attribute.OriginalValue.Trim();
        if (value.Length == 0 || title.Length == 0)
            return null;

        var group = GroupOf(rules, scenario, value);

        // Two characters and under carry no identity of their own — "cm", "ve", "8" — and anchoring
        // on one would propose a phrase assembled around a coincidence.
        var shared = group
            .SelectMany(SplitWords)
            .Select(FoldedTitle.Fold)
            .Where(w => w.Length > 2)
            .ToHashSet(StringComparer.Ordinal);

        if (shared.Count == 0)
            return null;

        var words = SplitWords(title);

        var anchor = -1;
        for (var i = 0; i < words.Count; i++)
        {
            if (shared.Contains(FoldedTitle.Fold(words[i])))
                anchor = i;
        }

        if (anchor < 0)
            return null;

        var claimed = ClaimedWords(rules, scenario);
        if (claimed.Contains(FoldedTitle.Fold(words[anchor])))
            return null;

        var reach = group.Max(spelling => SplitWords(spelling).Count);
        var from = Math.Max(0, anchor - Math.Min(reach, MaxPhraseWords) + 1);
        while (from < anchor && claimed.Contains(FoldedTitle.Fold(words[from])))
            from++;

        var phrase = string.Join(' ', words.Skip(from).Take(anchor - from + 1));

        // The same thing the cell already says is not a spelling worth adding — and it would not have
        // reached here as "not in the title" if it were.
        return string.Equals(FoldedTitle.Fold(phrase), FoldedTitle.Fold(value), StringComparison.Ordinal)
            ? null
            : phrase;
    }

    /// <summary>
    /// Every spelling the value already answers to — its alias group, or just itself.
    ///
    /// <para>The reason this is a group and not the cell's own text is the product type column. A
    /// marketplace RuleSet defines "Notebook OR Laptop OR Dizüstü Bilgisayar OR …" as one value, and
    /// a seller writes titles ending "Taşınabilir Bilgisayar" — a spelling that group has never heard
    /// of. Anchored on the cell alone the word "Notebook" is nowhere in that title and no card is
    /// offered; anchored on the group, "Bilgisayar" is, and the phrase around it is the spelling
    /// worth adopting.</para>
    ///
    /// <para><b>This is also what keeps the proposal from being a correlation.</b> Widening it to
    /// "any phrase that turns up on the rows carrying this value" would, on a real file, notice that
    /// "Gümüş" and "Aspire Lite" appear on exactly the same rows and offer to make one a spelling of
    /// the other. A value with a single spelling gets a single spelling's worth of anchor words, and
    /// nothing about a colour ever reaches a model name.</para>
    /// </summary>
    static IReadOnlyList<string> GroupOf(CompiledRuleSet rules, Scenario scenario, string value)
    {
        var attr = rules.Attributes.FirstOrDefault(a =>
            string.Equals(a.Rule.Column, scenario.Attribute.Column, StringComparison.Ordinal));

        if (attr is null || attr.AliasSpellings.Count == 0)
            return [value];

        var folded = FoldedTitle.Fold(value);

        var key = attr.AliasSpellings
            .FirstOrDefault(s => string.Equals(s.Folded, folded, StringComparison.Ordinal))
            .Key;

        if (string.IsNullOrEmpty(key))
            return [value];

        var group = attr.AliasSpellings
            .Where(s => string.Equals(s.Key, key, StringComparison.Ordinal))
            .Select(s => s.Folded)
            .ToList();

        // The cell's own spelling stays in, whatever the catalogue is keyed on.
        group.Add(folded);
        return group;
    }

    /// <summary>How short a word may be before one letter of difference stops being a typo. "Krem"
    /// and "Kreb" are four letters apart from being two different things; "Emaye" and "Emaya" are
    /// not.</summary>
    const int TypoWordLength = 5;

    /// <summary>
    /// A word in the title that is one letter away from one of the cell's — "Emaya" against a cell
    /// reading "Emaye". The title is simply misspelt, and no amount of exact matching will ever find
    /// it.
    ///
    /// <para><b>This is the only place in the module that measures similarity, and it may never move
    /// out of it.</b> What it produces is a <em>proposal</em>: the operator reads it on a card beside
    /// a before and after, edits it if it is wrong, and approves it — at which point the spelling
    /// joins the catalogue and matching goes on being exact. The engine never consults this. That
    /// distinction is the whole of why it does not break the rule on
    /// <see cref="AttributeMatcher"/>: a wrong guess here costs a card the operator declines, not
    /// characters cut out of a title nobody checked.</para>
    /// </summary>
    static string? MisspeltWord(Scenario scenario)
    {
        var value = scenario.Attribute.OriginalValue.Trim();
        var title = scenario.Sample.OriginalTitle;
        if (value.Length == 0 || title.Length == 0)
            return null;

        var needles = SplitWords(value)
            .Select(FoldedTitle.Fold)
            .Where(w => w.Length >= TypoWordLength)
            .ToList();

        if (needles.Count == 0)
            return null;

        foreach (var word in SplitWords(title))
        {
            var folded = FoldedTitle.Fold(word);
            if (folded.Length < TypoWordLength)
                continue;

            if (needles.Any(n => OneLetterApart(n, folded)))
                return word;
        }

        return null;
    }

    /// <summary>
    /// Whether two words differ by exactly one letter — substituted, inserted or dropped. Deliberately
    /// not a distance function: nothing here is allowed to ask "how close", only "is it one".
    /// </summary>
    static bool OneLetterApart(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
            return false;

        if (string.Equals(a, b, StringComparison.Ordinal))
            return false;

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        var i = 0;
        var j = 0;
        var spent = false;

        while (i < shorter.Length && j < longer.Length)
        {
            if (shorter[i] == longer[j])
            {
                i++;
                j++;
                continue;
            }

            if (spent)
                return false;

            spent = true;

            // A substitution steps both; an insertion steps only the longer one.
            if (shorter.Length == longer.Length)
                i++;

            j++;
        }

        return true;
    }

    static List<string> SplitWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The same text with its spaces taken out, for comparing a cell's "8 GB" against a
    /// title's "8GB".</summary>
    static string Squeeze(string text) =>
        new(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    /// <summary>The column whose own value overlaps the phrase — the one the phrase belongs to. On
    /// the real export "RTX 5070 8GB" finds the graphics card column, whose cell reads
    /// "GeForce RTX 5070".</summary>
    static string? FindOwner(CompiledRuleSet rules, string phrase)
    {
        var words = SplitWords(phrase).Select(FoldedTitle.Fold).Where(w => w.Length > 1).ToList();
        if (words.Count == 0)
            return null;

        foreach (var attr in rules.Attributes)
        {
            foreach (var group in attr.Rule.AliasGroups)
            {
                foreach (var spelling in group)
                {
                    var folded = FoldedTitle.Fold(spelling);
                    if (folded.Length > 0 && words.Count(w => folded.Contains(w, StringComparison.Ordinal)) >= 2)
                        return attr.Rule.Column;
                }
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Writing the change into the rule set
    // ---------------------------------------------------------------------

    /// <summary>Applies the chosen fixes and hands back the updated rule set. Selection arrives as
    /// ids alone; what gets written is decided here.</summary>
    public static TitleRuleSet Apply(
        TitleRuleSet source, IReadOnlyList<TitleFix> fixes, IReadOnlyCollection<string> chosen)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fixes);
        ArgumentNullException.ThrowIfNull(chosen);

        var result = source;

        foreach (var candidate in fixes.Where(f => chosen.Contains(f.Id)))
        {
            if (candidate.NeedsColumnChoice && candidate.TargetColumn.Trim().Length == 0)
                continue;

            result = candidate.Kind switch
            {
                TitleFixKind.MergeAlias => ApplyMerge(result, candidate),
                TitleFixKind.ProtectPhrase => ApplyProtect(result, candidate),
                TitleFixKind.AdoptPhrase => ApplyAdopt(result, candidate),
                TitleFixKind.AdoptCategoryType => ApplyCategoryType(result, candidate),
                TitleFixKind.EnableMatching => ApplyEnable(result, candidate),
                _ => result,
            };
        }

        return result;
    }

    static TitleRuleSet ApplyMerge(TitleRuleSet set, TitleFix fix) =>
        WithRule(set, fix.TargetColumn, rule => rule with
        {
            Kind = TitleAttributeKind.Alias,
            Aliases = AddSpelling(rule.AliasGroups, fix.CellValue, fix.Value),
        });

    /// <summary>
    /// The RuleSet's whole group joins the column's catalogue, headed by the marketplace's own
    /// canonical spelling. <c>Correct</c> is turned on with it: adopting the vocabulary and then not
    /// pulling the cells onto it would leave the column disagreeing with a title the same rule had
    /// just edited.
    ///
    /// <para><c>FillFromTitle</c> is deliberately left alone. It writes in the opposite direction
    /// from everything else here, and turning it on is a decision of its own rather than a rider on
    /// a card about spellings.</para>
    /// </summary>
    static TitleRuleSet ApplyCategoryType(TitleRuleSet set, TitleFix fix) =>
        WithRule(set, fix.TargetColumn, rule =>
        {
            // The spellings ride in one string in the same "|" format the Değerler box uses, so an
            // operator who trimmed one off the card gets exactly what they left behind.
            var spellings = fix.Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var groups = rule.AliasGroups;

            foreach (var spelling in spellings)
                groups = AddSpelling(groups, fix.CellValue, spelling);

            return rule with
            {
                Kind = TitleAttributeKind.Alias,
                Correct = true,
                Aliases = groups,
            };
        });

    static TitleRuleSet ApplyAdopt(TitleRuleSet set, TitleFix fix) =>
        WithRule(set, fix.TargetColumn, rule => rule with
        {
            Kind = TitleAttributeKind.Alias,
            Aliases = AddSpelling(rule.AliasGroups, fix.CellValue, fix.Value),
        });

    /// <summary>
    /// The protector: the phrase joins the owning column's catalogue, and that column is barred from
    /// removing or rewriting anything. Claiming the longer phrase is what keeps the other rule's
    /// shorter candidate off it.
    /// </summary>
    static TitleRuleSet ApplyProtect(TitleRuleSet set, TitleFix fix) =>
        WithRule(set, fix.TargetColumn, rule => rule with
        {
            Kind = TitleAttributeKind.Alias,
            Remove = false,
            Correct = false,
            Aliases = AddSpelling(rule.AliasGroups, OwnGroupFor(rule, fix.Value), fix.Value),
        });

    /// <summary>
    /// Which of the owning column's groups the protected phrase joins: the one whose spelling the
    /// phrase overlaps, so "RTX 5070 8GB" lands beside "GeForce RTX 5070" rather than starting a
    /// group of its own that no cell would ever resolve to.
    /// </summary>
    static string? OwnGroupFor(TitleAttributeRule rule, string phrase)
    {
        var words = SplitWords(phrase).Select(FoldedTitle.Fold).Where(w => w.Length > 1).ToList();

        foreach (var group in rule.AliasGroups)
        {
            if (group.Count == 0)
                continue;

            var head = FoldedTitle.Fold(group[0]);
            if (head.Length > 0 && words.Count(w => head.Contains(w, StringComparison.Ordinal)) >= 2)
                return group[0];
        }

        return null;
    }

    /// <summary>
    /// Adds a spelling to the group whose canonical is <paramref name="canonical"/>, creating the
    /// group if there is none.
    ///
    /// <para>The canonical stays first. It is what the cell gets rewritten to, so putting the
    /// title's spelling ahead of it would quietly overwrite the catalogue with title text.</para>
    /// </summary>
    static IReadOnlyList<IReadOnlyList<string>> AddSpelling(
        IReadOnlyList<IReadOnlyList<string>> groups, string? canonical, string spelling)
    {
        var value = spelling.Trim();
        if (value.Length == 0)
            return groups;

        var updated = groups.Select(g => g.ToList()).ToList();
        var wanted = FoldedTitle.Fold(canonical ?? value);

        foreach (var group in updated)
        {
            if (group.Count == 0)
                continue;

            var isTarget = string.Equals(FoldedTitle.Fold(group[0]), wanted, StringComparison.Ordinal);
            var alreadyHere = group.Any(s =>
                string.Equals(FoldedTitle.Fold(s), FoldedTitle.Fold(value), StringComparison.Ordinal));

            if (alreadyHere)
                return groups;

            if (isTarget)
            {
                group.Add(value);
                return updated.Select(g => (IReadOnlyList<string>)g).ToList();
            }
        }

        updated.Add(canonical is null || FoldedTitle.Fold(canonical) == FoldedTitle.Fold(value)
            ? [value]
            : [canonical, value]);

        return updated.Select(g => (IReadOnlyList<string>)g).ToList();
    }

    static TitleRuleSet WithRule(
        TitleRuleSet set, string column, Func<TitleAttributeRule, TitleAttributeRule> change)
    {
        var name = column.Trim();
        var found = false;

        var attributes = set.AttributeList.Select(rule =>
        {
            if (!string.Equals(rule.Column, name, StringComparison.OrdinalIgnoreCase))
                return rule;

            found = true;
            return change(rule);
        }).ToList();

        // A protected phrase can name a column that has no rule yet — it only needs to exist so the
        // phrase has somewhere to live.
        if (!found && name.Length > 0)
            attributes.Add(change(new TitleAttributeRule(name)));

        return set with { Attributes = attributes };
    }
}
