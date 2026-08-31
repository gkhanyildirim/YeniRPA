using YeniRPA.Web.Models;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// Turning the review list into decisions. These rules write into the rule set and therefore act on
/// the whole file, so what may be suggested — and what may not — is the substance of this class.
/// </summary>
public class TitleFixSuggesterTests
{
    static readonly MeasureUnit Gb = new("GB", ["gb"], 1);

    static (CompiledRuleSet Rules, IReadOnlyList<TitleCleanRow> Rows) Run(
        TitleRuleSet set, List<List<string>> table)
    {
        var rules = CompiledRuleSet.Compile(set);
        return (rules, TitleCleanBuilder.Clean(rules, table));
    }

    static IReadOnlyList<TitleFix> Fixes(TitleRuleSet set, List<List<string>> table)
    {
        var (rules, rows) = Run(set, table);
        return TitleFixSuggester.Suggest(rules, rows);
    }

    // -----------------------------------------------------------------
    // Grouping
    // -----------------------------------------------------------------

    /// <summary>
    /// The point of the feature. Eighteen review rows on the real export were three scenarios, and
    /// one of them was the same disagreement on 78 rows — asked per row, that is the same question
    /// 78 times.
    /// </summary>
    [Fact]
    public void RowsWithTheSameProblemBecomeOneScenarioCarryingTheCount()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias,
                Aliases: [["Oyun Bilgisayarı"], ["Notebook"]])]);

        List<List<string>> table =
        [
            ["Başlık", "Ürün Tipi"],
            ["Acme A1 Notebook", "Oyun Bilgisayarı"],
            ["Acme A2 Notebook", "Oyun Bilgisayarı"],
            ["Acme A3 Notebook", "Oyun Bilgisayarı"],
        ];

        var fix = Assert.Single(Fixes(set, table));

        Assert.Equal(TitleFixKind.MergeAlias, fix.Kind);
        Assert.Equal(3, fix.Rows);
        Assert.Equal("Notebook", fix.Value);
        Assert.Equal("Oyun Bilgisayarı", fix.CellValue);
    }

    /// <summary>The apply request recomputes the suggestions and matches on the id, so an id has to
    /// mean the same thing on both runs.</summary>
    [Fact]
    public void AScenarioKeepsItsIdAcrossTwoSeparateRuns()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias,
                Aliases: [["Oyun Bilgisayarı"], ["Notebook"]])]);

        List<List<string>> table = [["Başlık", "Ürün Tipi"], ["Acme A1 Notebook", "Oyun Bilgisayarı"]];

        Assert.Equal(Fixes(set, table).Single().Id, Fixes(set, table).Single().Id);
    }

    // -----------------------------------------------------------------
    // A — merging two spellings
    // -----------------------------------------------------------------

    /// <summary>The cell's own spelling stays at the head of the group. It is what a cell gets
    /// rewritten to, so putting the title's spelling first would overwrite the catalogue with title
    /// text.</summary>
    [Fact]
    public void MergingKeepsTheCellSpellingAsTheCanonicalOne()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias,
                Aliases: [["Oyun Bilgisayarı"], ["Notebook"]])]);

        List<List<string>> table = [["Başlık", "Ürün Tipi"], ["Acme A1 Notebook", "Oyun Bilgisayarı"]];

        var fixes = Fixes(set, table);
        var updated = TitleFixSuggester.Apply(set, fixes, [fixes[0].Id]);

        var group = updated.AttributeList.Single().AliasGroups
            .First(g => g[0] == "Oyun Bilgisayarı");

        Assert.Equal(["Oyun Bilgisayarı", "Notebook"], group);
    }

    /// <summary>Applying the fix actually clears the rows it was offered for.</summary>
    [Fact]
    public void ApplyingTheMergeTakesThoseRowsOutOfReview()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias,
                Aliases: [["Oyun Bilgisayarı"], ["Notebook"]])]);

        List<List<string>> table =
        [
            ["Başlık", "Ürün Tipi"],
            ["Acme A1 Notebook", "Oyun Bilgisayarı"],
            ["Acme A2 Notebook", "Oyun Bilgisayarı"],
        ];

        var fixes = Fixes(set, table);
        var updated = TitleFixSuggester.Apply(set, fixes, [fixes[0].Id]);
        var (_, rows) = Run(updated, table);

        Assert.All(rows, row => Assert.False(row.HasConflict));
        Assert.Equal("Acme A1", rows[0].CleanTitle);
    }

    /// <summary>
    /// A title naming two different known values for one attribute gives no way to tell which the
    /// cell was meant to be. Merging on a guess would write the wrong spelling into the catalogue for
    /// the whole file, so nothing is offered and the rows stay in review.
    /// </summary>
    [Fact]
    public void NoMergeIsOfferedWhenTheTitleNamesTwoKnownValues()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias,
                Aliases: [["Oyun Bilgisayarı"], ["Notebook"], ["Ultrabook"]])]);

        List<List<string>> table =
        [
            ["Başlık", "Ürün Tipi"],
            ["Acme A1 Notebook Ultrabook", "Oyun Bilgisayarı"],
        ];

        Assert.Empty(Fixes(set, table));
    }

    // -----------------------------------------------------------------
    // B — protecting a repeated value
    // -----------------------------------------------------------------

    /// <summary>
    /// The graphics card's own memory beside the system RAM, taken from the real export. The fix
    /// gives the longer phrase to the column that owns it and bars that column from removing
    /// anything, which leaves the RAM's own copy as the only one its rule can take.
    /// </summary>
    [Fact]
    public void ARepeatedValueIsResolvedByProtectingThePhraseAroundTheOtherOne()
    {
        var set = new TitleRuleSet("Test", "Başlık",
        [
            new TitleAttributeRule("Ekran Kartı", TitleAttributeKind.Alias,
                Aliases: [["GeForce RTX 5070"]]),
            new TitleAttributeRule("RAM", TitleAttributeKind.Measure, Units: [Gb]),
        ]);

        List<List<string>> table =
        [
            ["Başlık", "Ekran Kartı", "RAM"],
            ["TUF A16 RTX 5070 8GB 8GB 512GB SSD", "GeForce RTX 5070", "8 GB"],
        ];

        var fix = Fixes(set, table).Single(f => f.Kind == TitleFixKind.ProtectPhrase);

        Assert.Equal("Ekran Kartı", fix.TargetColumn);
        Assert.Equal("RTX 5070 8GB", fix.Value);
        Assert.False(fix.NeedsColumnChoice);
        Assert.NotNull(fix.Warning);

        var updated = TitleFixSuggester.Apply(set, [fix], [fix.Id]);
        var gpu = updated.AttributeList.First(a => a.Column == "Ekran Kartı");

        Assert.False(gpu.Remove);
        Assert.False(gpu.Correct);

        var (_, rows) = Run(updated, table);
        Assert.Equal("TUF A16 RTX 5070 8GB 512GB SSD", rows[0].CleanTitle);
        Assert.False(rows[0].HasConflict);
    }

    // -----------------------------------------------------------------
    // C — a bare number in the cell
    // -----------------------------------------------------------------

    /// <summary>
    /// A processor cell holding only the model number. The fix adopts the title's full phrase as that
    /// value's spelling, so what leaves the title is a phrase rather than an unqualified number.
    /// </summary>
    [Fact]
    public void ABareNumberIsResolvedByAdoptingTheTitlesPhrase()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("İşlemci", TitleAttributeKind.Alias, Aliases: [["465"]])]);

        List<List<string>> table =
        [
            ["Başlık", "İşlemci"],
            ["Acme Core Ultra 5 465 Notebook", "465"],
        ];

        var fix = Fixes(set, table).Single();

        Assert.Equal(TitleFixKind.AdoptPhrase, fix.Kind);
        Assert.Equal("Ultra 5 465", fix.Value);

        var updated = TitleFixSuggester.Apply(set, [fix], [fix.Id]);
        var (_, rows) = Run(updated, table);

        Assert.Equal("Acme Core Notebook", rows[0].CleanTitle);
        Assert.False(rows[0].HasConflict);
    }

    /// <summary>The phrase stops at a word another attribute already accounts for — reaching over a
    /// value that gets removed would build a phrase that never matches again.</summary>
    [Fact]
    public void TheProposedPhraseStopsAtAnotherAttributesValue()
    {
        var set = new TitleRuleSet("Test", "Başlık",
        [
            new TitleAttributeRule("Marka", TitleAttributeKind.Alias, Aliases: [["Acme"]]),
            new TitleAttributeRule("İşlemci", TitleAttributeKind.Alias, Aliases: [["465"]]),
        ]);

        List<List<string>> table =
        [
            ["Başlık", "Marka", "İşlemci"],
            ["Acme 465 Notebook", "Acme", "465"],
        ];

        // "Acme" belongs to the brand rule, so there is nothing left to build a phrase from.
        Assert.DoesNotContain(Fixes(set, table), f => f.Kind == TitleFixKind.AdoptPhrase);
    }

    // -----------------------------------------------------------------
    // What is never offered
    // -----------------------------------------------------------------

    /// <summary>A clean file has nothing to decide.</summary>
    [Fact]
    public void ACleanFileProducesNoSuggestions()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Marka", TitleAttributeKind.Alias, Aliases: [["Acme"]])]);

        List<List<string>> table = [["Başlık", "Marka"], ["Acme Notebook", "Acme"]];

        Assert.Empty(Fixes(set, table));
    }

    /// <summary>An id nobody offered changes nothing.</summary>
    [Fact]
    public void AnUnknownIdIsIgnored()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias,
                Aliases: [["Oyun Bilgisayarı"], ["Notebook"]])]);

        List<List<string>> table = [["Başlık", "Ürün Tipi"], ["Acme A1 Notebook", "Oyun Bilgisayarı"]];

        var updated = TitleFixSuggester.Apply(set, Fixes(set, table), ["deadbeef"]);

        Assert.Equal(
            set.AttributeList.Single().AliasGroups.Select(g => string.Join("|", g)),
            updated.AttributeList.Single().AliasGroups.Select(g => string.Join("|", g)));
    }

    // -----------------------------------------------------------------
    // D — adopting a spelling the catalogue does not have
    // -----------------------------------------------------------------
    //
    // The real case: a title reading "İndüksiyon Ocak" against a cell reading "İndüksiyonlu ocak".
    // Nothing about that row is wrong — it reports "not in the title", the way any attribute a title
    // leaves out does — so the whole feature is the narrowing that tells the two apart.

    static TitleRuleSet Hobs(TitleAttributeKind kind = TitleAttributeKind.Alias) =>
        new("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", kind, Aliases: [["İndüksiyonlu Ocak"]])]);

    static List<List<string>> HobTable() =>
    [
        ["Başlık", "Ürün Tipi"],
        ["Teka IR 8430 5 Zone 80 cm Siyah İndüksiyon Ocak", "İndüksiyonlu ocak"],
    ];

    /// <summary>
    /// The phrase is pinned, not guessed: the anchor is the title word that equals one of the cell's
    /// own words ("Ocak"), and the length is the cell's own word count — two, so "Siyah" is left out
    /// even though nothing else claims it.
    /// </summary>
    [Fact]
    public void ATitleSpellingTheCatalogueLacksIsOfferedAsASpelling()
    {
        var fix = Assert.Single(Fixes(Hobs(), HobTable()));

        Assert.Equal(TitleFixKind.AdoptPhrase, fix.Kind);
        Assert.Equal("İndüksiyon Ocak", fix.Value);
        Assert.Equal("İndüksiyonlu ocak", fix.CellValue);
        Assert.Null(fix.Warning);
    }

    /// <summary>Applying it both cuts the phrase out of the title and rewrites the cell — the two
    /// things the operator wanted, from one decision.</summary>
    [Fact]
    public void AdoptingTheSpellingRemovesItFromTheTitleAndCorrectsTheCell()
    {
        var set = Hobs();
        var table = HobTable();

        var fixes = Fixes(set, table);
        var updated = TitleFixSuggester.Apply(set, fixes, [fixes[0].Id]);

        Assert.Equal(
            ["İndüksiyonlu Ocak", "İndüksiyon Ocak"],
            updated.AttributeList.Single().AliasGroups.Single());

        var row = Run(updated, table).Rows.Single();
        var attribute = row.Attributes.Single();

        Assert.Equal("Teka IR 8430 5 Zone 80 cm Siyah", row.CleanTitle);
        Assert.Equal(TitleAttributeStatus.Corrected, attribute.Status);
        Assert.Equal("İndüksiyonlu Ocak", attribute.Value);
    }

    /// <summary>
    /// The filter that keeps this from becoming a second review table. Every attribute a title does
    /// not mention arrives here, and most of them are simply absent — no shared word, no card.
    /// </summary>
    [Fact]
    public void AValueTheTitleSharesNoWordWithIsNotOffered()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias, Aliases: [["Ankastre Fırın"]])]);

        List<List<string>> table =
        [
            ["Başlık", "Ürün Tipi"],
            ["Teka IR 8430 5 Zone 80 cm Siyah İndüksiyon Ocak", "Ankastre Fırın"],
        ];

        Assert.Empty(Fixes(set, table));
    }

    /// <summary>A word too short to carry an identity of its own cannot anchor a phrase: "80 cm" in
    /// the title against a cell reading "60 cm" would otherwise propose deleting the wrong width.</summary>
    [Fact]
    public void ATwoLetterWordCannotAnchorAPhrase()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Genişlik", TitleAttributeKind.Alias, Aliases: [["60 cm"]])]);

        List<List<string>> table =
        [
            ["Başlık", "Genişlik"],
            ["Teka IR 8430 5 Zone 80 cm Siyah İndüksiyon Ocak", "60 cm"],
        ];

        Assert.Empty(Fixes(set, table));
    }

    /// <summary>A card that would not change the title is not a decision. The column may not remove,
    /// so adopting the spelling leaves the title exactly as it was.</summary>
    [Fact]
    public void ASpellingThatWouldChangeNothingIsNotOffered()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias, Remove: false,
                Aliases: [["İndüksiyonlu Ocak"]])]);

        Assert.Empty(Fixes(set, HobTable()));
    }

    /// <summary>A plain text column has nowhere to put a second spelling, so the fix changes its type
    /// — an effect beyond the rows on the card, which is what the warning is for.</summary>
    [Fact]
    public void AdoptingOnATextColumnWarnsThatTheTypeChanges()
    {
        var fix = Assert.Single(Fixes(Hobs(TitleAttributeKind.Text), HobTable()));

        Assert.Equal("İndüksiyon Ocak", fix.Value);
        Assert.NotNull(fix.Warning);
        Assert.Contains("Değer Listesi", fix.Warning, StringComparison.Ordinal);
    }

    /// <summary>The preview on a card is produced by the real engine, so what the operator is shown
    /// is what they get.</summary>
    [Fact]
    public void TheCardPreviewIsWhatApplyingActuallyProduces()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias,
                Aliases: [["Oyun Bilgisayarı"], ["Notebook"]])]);

        List<List<string>> table = [["Başlık", "Ürün Tipi"], ["Acme A1 Notebook", "Oyun Bilgisayarı"]];

        var fix = Fixes(set, table).Single();
        var updated = TitleFixSuggester.Apply(set, [fix], [fix.Id]);
        var (_, rows) = Run(updated, table);

        Assert.Equal("Acme A1 Notebook", fix.SampleBefore);
        Assert.Equal(fix.SampleAfter, rows[0].CleanTitle);
    }
}
