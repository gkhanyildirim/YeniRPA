using System.Text.RegularExpressions;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>What the scan concluded about one column, so the editor can explain its own proposal.</summary>
/// <param name="Matched">How many of the sampled rows this rule would actually have found in the
/// title. This is measured by running the real engine, not estimated — the number the operator sees
/// is the number they will get.</param>
public sealed record TitleColumnHint(
    string Column,
    TitleAttributeKind Kind,
    bool Remove,
    int Filled,
    int Distinct,
    int Matched,
    IReadOnlyList<string> Samples,
    string? Note = null);

public sealed record TitleRuleSuggestion(
    TitleRuleSet RuleSet,
    IReadOnlyList<TitleColumnHint> Columns,
    IReadOnlyList<string> Notes);

/// <summary>
/// Proposes a starting rule set by reading the uploaded file itself.
///
/// <para>This is what makes "every category gets its own rule set" affordable. What it produces is a
/// <b>draft</b>: it is handed to the editor for the team to correct and save, and is never applied
/// on its own. Guessing wrong here costs a moment in the editor; guessing wrong in the cleaner costs
/// a catalogue.</para>
///
/// <para>The one judgement it makes conservatively is <see cref="TitleColumnHint.Remove"/>. A column
/// is only proposed for removal when its values were actually found in the sampled titles — measured
/// by running <see cref="TitleCleanBuilder"/> over the sample rather than by guessing at it. A GPU
/// column whose values never appear verbatim in a title therefore arrives switched off, which is
/// how "RTXPRO2000" survives the reference title without anybody configuring anything.</para>
/// </summary>
public static class TitleRuleSuggester
{
    /// <summary>Rows read to characterise the columns. Enough to be representative; small enough
    /// that a 40 000-row catalogue is still analysed in a moment.</summary>
    public const int SampleRows = 200;

    /// <summary>Share of a column's filled values that must parse as number + unit before it is
    /// called a measured attribute.</summary>
    const double MeasureShare = 0.8;

    /// <summary>Share of sampled rows a value must be found in before removal is proposed. Well
    /// under half: plenty of true attributes are left out of plenty of titles.</summary>
    const double RemoveShare = 0.35;

    /// <summary>A column with few enough short values is a closed vocabulary, so it can be given a
    /// catalogue and gain the ability to spot a title naming a different one.</summary>
    const int MaxAliasValues = 60;
    const int MaxAliasLength = 30;

    static readonly string[] TitleHeaders =
        ["Başlık", "Ürün Adı", "Ürün İsmi", "Urun Adi", "Title", "Product Name", "Name", "Ad"];

    static readonly MeasureUnit[] DataUnits =
    [
        new("GB", ["gb", "gbyte", "gigabayt", "gigabyte"], 1),
        new("TB", ["tb", "tbyte", "terabayt", "terabyte"], 1024),
        new("MB", ["mb", "mbyte", "megabayt", "megabyte"], 1.0 / 1024),
    ];

    static readonly MeasureUnit[] InchUnits =
    [
        new("\"", ["\"", "''", "inç", "inc", "inch", "inches"]),
    ];

    static readonly MeasureUnit[] ClockUnits =
    [
        new("GHz", ["ghz"], 1),
        new("MHz", ["mhz"], 0.001),
    ];

    /// <summary>
    /// The unit families this module knows. Two readers: <see cref="DetectKnownUnits"/> matches a
    /// column's values against them, and the rule editor offers them as ready-made choices so the
    /// cell format does not have to be typed from memory.
    ///
    /// <para>Order and contents are load-bearing — <see cref="DetectKnownUnits"/> works by index, and
    /// each family's declaration order is what keeps its canonical spellings and factors as written.
    /// Adding a family at the end is safe; reordering one is not.</para>
    /// </summary>
    public static readonly IReadOnlyList<MeasureFamily> Families =
    [
        new("Veri boyutu (GB / TB / MB)", DataUnits),
        new("Ekran boyutu (inç)", InchUnits),
        new("Frekans (GHz / MHz)", ClockUnits),
        new("Batarya (mAh)", [new("mAh", ["mah"])]),
        new("Güç (W / kW)", [new("W", ["w", "watt"], 1), new("kW", ["kw"], 1000)]),
        new("Ağırlık (kg / g)", [new("kg", ["kg"], 1), new("g", ["g", "gr", "gram"], 0.001)]),
        new("Uzunluk (cm / mm / m)",
            [new("cm", ["cm"], 1), new("mm", ["mm"], 0.1), new("m", ["m", "metre"], 100)]),
        new("Hacim (L / ml)",
            [new("L", ["l", "lt", "litre", "liter"], 1), new("ml", ["ml"], 0.001)]),
    ];

    static readonly Regex NumberThenUnit = new(
        @"^(?<n>\d+(?:[.,]\d+)?)\s*(?<u>\S.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex BareNumber = new(
        @"^\d+(?:[.,]\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static TitleRuleSuggestion Suggest(List<List<string>> table, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (table.Count < 2)
            throw new InvalidOperationException("The uploaded file has a header but no rows to read.");

        var header = table[0];
        var sample = table.Skip(1).Where(row => row.Any(c => c.Trim().Length > 0)).Take(SampleRows).ToList();

        if (sample.Count == 0)
            throw new InvalidOperationException("The uploaded file has no rows to read.");

        var titleIndex = FindTitleColumn(header, sample);
        var titleColumn = header[titleIndex].Trim();
        var notes = new List<string>();

        // A marketplace import template puts its technical field codes on the row under the header.
        // Read as data it seeds every catalogue with a field code and makes a column that is
        // genuinely empty look like it holds one value — which is how a 40-column file proposes 300
        // rules. See TitleCleanBuilder.IsFieldCodeRow.
        if (sample.Count > 0 && TitleCleanBuilder.IsFieldCodeRow(sample[0], titleIndex))
        {
            sample = sample.Skip(1).ToList();
            notes.Add(
                "Dosyanın 2. satırı ürün değil, pazaryerinin teknik alan kodlarını taşıyor " +
                "(TITLE__TR_TR, BRAND, PROD_FEAT_…). Kural önerisinde dikkate alınmadı.");

            if (sample.Count == 0)
                throw new InvalidOperationException("The uploaded file has no product rows to read.");
        }

        // Phase 1 — decide what kind of thing each column holds.
        var proposals = new List<Proposal>();

        // Gathered rather than reported one by one. A marketplace export carries hundreds of columns
        // that are empty for this category, and a note apiece buried the two or three that actually
        // needed reading under forty that said the same thing.
        var empty = new List<string>();

        for (var c = 0; c < header.Count; c++)
        {
            if (c == titleIndex)
                continue;

            var column = header[c].Trim();
            if (column.Length == 0)
                continue;

            // Cell and title are kept side by side: a column of bare numbers reads its unit off the
            // title on the same row, so the two lists have to stay index-aligned.
            var pairs = sample
                .Select(row => (
                    Value: TabularFile.GetCell(row, c).Trim(),
                    Title: TabularFile.GetCell(row, titleIndex)))
                .Where(p => p.Value.Length > 0)
                .ToList();

            if (pairs.Count == 0)
            {
                empty.Add(column);
                continue;
            }

            var values = pairs.Select(p => p.Value).ToList();
            var (rule, note, inferred) = Propose(column, values, pairs.Select(p => p.Title).ToList());
            proposals.Add(new Proposal(c, rule, note, values) { UnitInferred = inferred });
        }

        // A unit read off the titles is a guess about which measurement in the title belongs to this
        // column, and a bare number can sit next to somebody else's. A core-count column of "16"
        // found the screen size's 16" and came back claiming inches. Where the unit it inferred is
        // one another column declares from its own data, that column has the better claim and this
        // one goes back to being plain text.
        var declared = proposals
            .Where(p => !p.UnitInferred)
            .SelectMany(p => p.Rule.UnitList.Select(u => FoldedTitle.Fold(u.Canonical)))
            .ToHashSet(StringComparer.Ordinal);

        for (var i = 0; i < proposals.Count; i++)
        {
            var proposal = proposals[i];
            if (!proposal.UnitInferred)
                continue;

            if (!proposal.Rule.UnitList.Any(u => declared.Contains(FoldedTitle.Fold(u.Canonical))))
                continue;

            var taken = proposal.Rule.UnitList[0].Canonical;
            proposals[i] = proposal with
            {
                Rule = new TitleAttributeRule(proposal.Rule.Column),
                Note = $"'{proposal.Rule.Column}' birimsiz sayılar taşıyor ve başlıkta bunların " +
                       $"ardından \"{taken}\" geçiyor — ama o birimi kendi verisiyle tanımlayan başka " +
                       "bir kolon var, o yüzden bu kolon ölçü sayılmadı.",
            };
        }

        if (empty.Count > 0)
        {
            var shown = string.Join(", ", empty.Take(6));
            var rest = empty.Count > 6 ? $" ve {empty.Count - 6} tane daha" : "";
            notes.Add(
                $"{empty.Count} kolon örneklemde tamamen boş olduğu için kural setine alınmadı " +
                $"({shown}{rest}).");
        }

        // Longest typical value first: where two attributes could both claim a stretch of title the
        // longer match wins, and ties fall to whichever comes first here.
        proposals = proposals.OrderByDescending(p => p.Values.Average(v => v.Length)).ToList();

        // Phase 2 — run the whole proposed set over the sample, measured against the real engine
        // rather than estimated, so the count in the editor is the count the run will produce.
        //
        // All of them together, and that is not an optimisation. A span may cut into a word only
        // where another accepted span continues from it, so "1TBSSD" only comes apart when the
        // capacity rule and the disk-type rule are both present. Probing one column at a time
        // reported zero matches for both halves of every glued token — which is precisely the shape
        // these titles are written in.
        var matches = CountMatches(titleColumn, proposals, sample, titleIndex);

        // Phase 3 — decide what may be removed.
        var hints = new List<TitleColumnHint>();
        var rules = new List<TitleAttributeRule>();

        for (var i = 0; i < proposals.Count; i++)
        {
            var proposal = proposals[i];
            var column = proposal.Rule.Column;
            var note = proposal.Note;
            var matched = matches[i];
            var remove = matched >= Math.Max(1, (int)Math.Ceiling(proposal.Values.Count * RemoveShare));

            // Bare numbers used to switch removal off for the whole column here. They no longer need
            // to: the engine refuses to delete an unqualified number for a Text or Alias attribute
            // per row, so those rows already count as unmatched above and a genuinely numeric column
            // arrives switched off on the measurement alone. One stray numeric value among a hundred
            // processor models no longer costs the other ninety-nine their removal.
            //
            // The note stays, because a column that matched nothing deserves to say why.
            if (proposal.Rule.Kind != TitleAttributeKind.Measure &&
                proposal.Values.Count(v => BareNumber.IsMatch(v)) >= proposal.Values.Count * 0.5)
            {
                note ??= $"'{column}' ağırlıklı olarak birimsiz sayı taşıyor. Başlıktaki çıplak sayılar " +
                         "model adının parçası olabileceği için çıkarılmıyor — bu kolon bir ölçüyse " +
                         "Tip'ini 'Ölçü' yapıp birimini yazın.";
            }

            rules.Add(proposal.Rule with { Remove = remove });
            hints.Add(new TitleColumnHint(
                column,
                proposal.Rule.Kind,
                remove,
                proposal.Values.Count,
                proposal.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                matched,
                proposal.Values.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList(),
                note));

            if (note is not null)
                notes.Add(note);
        }

        return new TitleRuleSuggestion(
            new TitleRuleSet(
                string.IsNullOrWhiteSpace(name) ? "Yeni kural seti" : name.Trim(),
                titleColumn,
                rules),
            hints,
            notes);
    }

    /// <param name="UnitInferred">The unit came from reading the titles, not from the column's own
    /// values — a guess, and one that can land on another column's measurement.</param>
    sealed record Proposal(int ColumnIndex, TitleAttributeRule Rule, string? Note, List<string> Values)
    {
        public bool UnitInferred { get; init; }
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// The header the titles are in. A recognised name wins; otherwise the column with the longest
    /// average text, which a product title reliably is.
    /// </summary>
    static int FindTitleColumn(List<string> header, List<List<string>> sample)
    {
        for (var c = 0; c < header.Count; c++)
        {
            var folded = FoldedTitle.Fold(header[c]);
            if (TitleHeaders.Any(h => string.Equals(FoldedTitle.Fold(h), folded, StringComparison.Ordinal)))
                return c;
        }

        var best = -1;
        var bestLength = 0d;

        for (var c = 0; c < header.Count; c++)
        {
            if (header[c].Trim().Length == 0)
                continue;

            var lengths = sample.Select(row => TabularFile.GetCell(row, c).Trim().Length).ToList();
            var average = lengths.Count > 0 ? lengths.Average() : 0;

            if (average > bestLength)
            {
                best = c;
                bestLength = average;
            }
        }

        if (best < 0)
            throw new InvalidOperationException("No column in the uploaded file looks like a product title.");

        return best;
    }

    static (TitleAttributeRule Rule, string? Note, bool UnitInferred) Propose(
        string column, List<string> values, List<string> titles)
    {
        // A unit the catalogue recognises brings its spelling variants and its conversion factor
        // with it — "inç"/"inch"/'"' as one thing, GB and TB as comparable.
        var known = DetectKnownUnits(values);
        if (known is not null)
            return (new TitleAttributeRule(column, TitleAttributeKind.Measure, Units: known), null, false);

        // A unit it does not recognise is still a unit. The catalogue cannot be kept ahead of a
        // marketplace's category list — dB, bar, devir, kWh, MP, ay — so what the column consistently
        // writes after its numbers is taken as the unit, whatever it is.
        var literal = DetectLiteralUnit(values);
        if (literal is not null)
        {
            return (
                // Correct is off: the canonical spelling of a unit nobody declared is not knowable,
                // and "correcting" a processor model of "8745HX" into "8745 HX" would damage the
                // cell. The value still matches and still leaves the title; only the rewrite stops.
                new TitleAttributeRule(column, TitleAttributeKind.Measure, Correct: false, Units: [literal]),
                $"'{column}' için birim, kolonun kendi verisinden okundu: \"{literal.Canonical}\". " +
                "Yazımı standartlaştırmadığı için 'Düzelt' kapalı bırakıldı — kanonik yazımı siz " +
                "belirlerseniz açabilirsiniz.",
                false);
        }

        // Numbers with no unit at all — the case the team described first, where the cell says "16"
        // and the title says "16GB". The titles are the only place left to read the unit off, and
        // reading it there works whatever the column happens to be called.
        var bare = values.Count(v => BareNumber.IsMatch(v));
        if (bare >= values.Count * MeasureShare)
        {
            var inferred = InferUnitFromTitles(values, titles);
            if (inferred is not null)
            {
                return (
                    new TitleAttributeRule(column, TitleAttributeKind.Measure, Units: inferred),
                    $"'{column}' birimsiz sayılar taşıyor; başlıklarda bu sayıların ardından " +
                    $"\"{inferred[0].Canonical}\" geçtiği için birim o kabul edildi — kontrol edin.",
                    true);
            }
        }

        var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (distinct.Count <= MaxAliasValues && distinct.All(v => v.Length <= MaxAliasLength))
        {
            return (
                new TitleAttributeRule(
                    column,
                    TitleAttributeKind.Alias,
                    Aliases: distinct.OrderByDescending(v => v.Length).Select(v => (IReadOnlyList<string>)[v]).ToList()),
                null,
                false);
        }

        return (new TitleAttributeRule(column), null, false);
    }

    /// <summary>
    /// The units a column's values are written in, when the catalogue recognises them — or
    /// <c>null</c>, which sends the column on to <see cref="DetectLiteralUnit"/>.
    ///
    /// <para><b>Only the units observed are proposed, not the whole family.</b> Handing a cache
    /// column (always MB) the entire GB/TB/MB family makes every "8GB" in a title — the graphics
    /// card's memory, the system RAM — a candidate for it, and the column then reports a conflict
    /// against its own 40 MB on every row. On a real export that was 78 false conflicts out of 100
    /// rows, all of them noise, and noise on this table is what stops anyone reading it.</para>
    ///
    /// <para>The cost is the mirror case: a disk column whose sample is all GB will not recognise a
    /// later "1TB". That failure announces itself — the row lands under <em>Başlıkta yok</em> with
    /// the value still in the title — and is fixed by adding the unit. A false conflict announces
    /// nothing and is fixed by nobody.</para>
    /// </summary>
    static MeasureUnit[]? DetectKnownUnits(List<string> values)
    {
        var counts = new Dictionary<int, int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in values)
        {
            var index = FamilyIndexOf(value, out var unit);
            if (index < 0)
                continue;

            counts[index] = counts.GetValueOrDefault(index) + 1;
            seen.Add(unit!.Canonical);
        }

        if (counts.Count == 0)
            return null;

        var (bestIndex, bestCount) = counts.OrderByDescending(p => p.Value).First();
        if (bestCount < values.Count * MeasureShare)
            return null;

        // Family order is kept, so the factors and the canonical spellings stay as declared.
        var family = Families[bestIndex].Units;
        var observed = family.Where(u => seen.Contains(u.Canonical)).ToArray();
        return observed.Length > 0 ? observed : [.. family];
    }

    static int FamilyIndexOf(string value, out MeasureUnit? unit)
    {
        unit = null;

        var match = NumberThenUnit.Match(FoldedTitle.Fold(value));
        if (!match.Success)
            return -1;

        var token = match.Groups["u"].Value.Trim();

        for (var f = 0; f < Families.Count; f++)
        {
            foreach (var candidate in Families[f].Units)
            {
                if (string.Equals(FoldedTitle.Fold(candidate.Canonical), token, StringComparison.Ordinal))
                {
                    unit = candidate;
                    return f;
                }

                foreach (var spelling in candidate.Spellings ?? [])
                {
                    if (string.Equals(FoldedTitle.Fold(spelling), token, StringComparison.Ordinal))
                    {
                        unit = candidate;
                        return f;
                    }
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// The unit a column writes after its numbers, when the catalogue has never heard of it.
    ///
    /// <para>This is what keeps the module usable across a marketplace's whole category list. The
    /// catalogue below can be kept ahead of laptops; it cannot be kept ahead of dishwashers, coffee
    /// machines and cameras at once — <c>dB</c>, <c>bar</c>, <c>devir</c>, <c>kWh</c>, <c>MP</c>,
    /// <c>ay</c> would each need a code change, which is a standing tax on every new category. So a
    /// token the column uses consistently is taken as its unit, whatever it is.</para>
    ///
    /// <para>Safe because the unit still has to be <em>present</em> for the engine to match — a bare
    /// number is never a candidate — and because a span may not cut into a word. A stray unit that
    /// happens to read like a common word ("ay" inside "ayarlı") is rejected on the boundary check,
    /// not on a list of words this class would otherwise have to keep.</para>
    /// </summary>
    static MeasureUnit? DetectLiteralUnit(List<string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var written = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var value in values)
        {
            var match = NumberThenUnit.Match(value.Trim());
            if (!match.Success)
                continue;

            var token = match.Groups["u"].Value.Trim();
            if (token.Length == 0 || token.Length > 16)
                continue;

            var key = FoldedTitle.Fold(token);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            written.TryAdd(key, token);
        }

        if (counts.Count == 0)
            return null;

        var (bestKey, bestCount) = counts.OrderByDescending(p => p.Value).First();
        if (bestCount < values.Count * MeasureShare)
            return null;

        // Spelled the way the column spells it, and offered as its own only spelling — inventing
        // variants for a unit nobody declared would be guessing twice over.
        return new MeasureUnit(written[bestKey], [written[bestKey]]);
    }

    /// <summary>
    /// The unit for a column of bare numbers, read off the titles themselves.
    ///
    /// <para>The team's first description of this job was "the cell says 16 and the title says
    /// 16GB". The unit has to come from somewhere, and the titles are the one place it is actually
    /// written. Reading it there works whatever the column is called — the alternative, a list of
    /// column-name hints, only ever knew about laptops and could not be extended to a marketplace's
    /// whole category list.</para>
    /// </summary>
    static MeasureUnit[]? InferUnitFromTitles(List<string> values, List<string> titles)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        var found = 0;

        for (var i = 0; i < values.Count && i < titles.Count; i++)
        {
            if (!BareNumber.IsMatch(values[i]))
                continue;

            // The number as the cell writes it, immediately followed by something that is not a
            // digit — the same shape the engine will look for once the unit is known.
            var pattern = @"(?<![\p{L}\d])" + Regex.Escape(values[i].Replace(',', '.')) +
                          @"\s*(?<u>[^\s\d]{1,10})";

            var match = Regex.Match(
                FoldedTitle.Fold(titles[i]).Replace(',', '.'), pattern, RegexOptions.CultureInvariant);

            if (!match.Success)
                continue;

            found++;
            var token = match.Groups["u"].Value.Trim();
            if (token.Length == 0)
                continue;

            counts[token] = counts.GetValueOrDefault(token) + 1;
            written.TryAdd(token, token);
        }

        if (found == 0 || counts.Count == 0)
            return null;

        var (bestKey, bestCount) = counts.OrderByDescending(p => p.Value).First();
        if (bestCount < found * 0.5)
            return null;

        // A unit the catalogue knows brings its spellings and its factor; one it does not is taken
        // as written, the same as DetectLiteralUnit.
        var family = FamilyOfToken(bestKey);
        return family ?? [new MeasureUnit(written[bestKey], [written[bestKey]])];
    }

    /// <summary>The catalogue units sharing a family with this token, or <c>null</c>.</summary>
    static MeasureUnit[]? FamilyOfToken(string foldedToken)
    {
        foreach (var family in Families)
        {
            foreach (var unit in family.Units)
            {
                if (string.Equals(FoldedTitle.Fold(unit.Canonical), foldedToken, StringComparison.Ordinal))
                    return [.. family.Units];

                foreach (var spelling in unit.Spellings ?? [])
                {
                    if (string.Equals(FoldedTitle.Fold(spelling), foldedToken, StringComparison.Ordinal))
                        return [.. family.Units];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// How many sampled rows each proposed rule would actually match, run through the real engine so
    /// the proposal cannot promise something the cleaner will not do.
    ///
    /// <para>The whole set runs together. That is not an optimisation: whether a span is allowed to
    /// cut into a word depends on which <em>other</em> attributes claimed the characters next to it,
    /// so a capacity of "1TB" inside "1TBSSD" is only valid while a disk-type rule is there to take
    /// the "SSD". Probed alone, both halves of every glued token match nothing.</para>
    /// </summary>
    static int[] CountMatches(
        string titleColumn,
        List<Proposal> proposals,
        List<List<string>> sample,
        int titleIndex)
    {
        var matched = new int[proposals.Count];
        if (proposals.Count == 0)
            return matched;

        CompiledRuleSet compiled;
        try
        {
            compiled = CompiledRuleSet.Compile(
                new TitleRuleSet("probe", titleColumn, proposals.Select(p => p.Rule).ToList()));
        }
        catch (InvalidOperationException)
        {
            // A set the engine will not accept matches nothing; the editor reports it as such rather
            // than the whole scan failing over one odd column.
            return matched;
        }

        // Column name -> where to read it from, so the probe reads the real row rather than a stub.
        var indexByColumn = proposals.ToDictionary(
            p => p.Rule.Column, p => p.ColumnIndex, StringComparer.OrdinalIgnoreCase);

        foreach (var row in sample)
        {
            var result = TitleCleanBuilder.CleanRow(
                compiled,
                0,
                TabularFile.GetCell(row, titleIndex),
                name => indexByColumn.TryGetValue(name.Trim(), out var index)
                    ? TabularFile.GetCell(row, index)
                    : "");

            for (var i = 0; i < proposals.Count; i++)
            {
                if (result.Attributes[i].Status is TitleAttributeStatus.Ok or TitleAttributeStatus.Corrected)
                    matched[i]++;
            }
        }

        return matched;
    }
}
