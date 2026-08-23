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
            _ => null,
        };
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
            "Aynı şeyin iki yazımı — eşanlamlı olarak birleştir",
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
                ? $"{rule.Column} kolonunun tipi Eşanlamlı olarak değişir."
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
        string? warning = null)
    {
        var id = Identify(scenario.Key, kind);

        // A protector's phrase is its own canonical — it is not standing in for a cell value, it is
        // a piece of the title being kept whole.
        var cellValue = kind == TitleFixKind.ProtectPhrase
            ? ""
            : scenario.Attribute.OriginalValue.Trim();

        var draft = new TitleFix(
            id, kind, scenario.Attribute.Column, targetColumn, problem, action, value, cellValue,
            scenario.Rows, scenario.Sample.OriginalTitle, "", needsColumnChoice, warning);

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

        // Words another rule is going to cut out. The phrase stops at them, because a phrase built
        // over text that gets removed would never match again once the rule set runs.
        //
        // Only rules that actually matched this row *and* are allowed to remove count. Taking it from
        // the cell values instead would treat a value the title never carried as claimed — which is
        // what stopped "RTX 5070 8GB" being proposed, since the graphics card's cell reads
        // "GeForce RTX 5070" and that phrase is not in the title at all.
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
