using YeniRPA.Web.Models;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// What a cleaned file still carries, and why.
///
/// <para>The module could always say what it did. This is the half that says what it did not do —
/// and the distinction it has to draw is between a column that matched and was told not to remove,
/// and a column that never matched at all. Read by eye those look identical, and they need opposite
/// fixes.</para>
/// </summary>
public class TitleLeftoverReportTests
{
    static IReadOnlyList<TitleLeftover> Report(TitleRuleSet set, List<List<string>> table)
    {
        var rules = CompiledRuleSet.Compile(set);
        return TitleLeftoverReport.Build(rules, TitleCleanBuilder.Clean(rules, table));
    }

    static TitleLeftover Word(IReadOnlyList<TitleLeftover> report, string word) =>
        report.First(l => string.Equals(l.Word, word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A column that found its value and was told to keep it.</summary>
    [Fact]
    public void AColumnThatMayNotRemoveIsNamedAsTheReason()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Malzeme", TitleAttributeKind.Alias, Remove: false,
                Aliases: [["Emaye"]])]);

        List<List<string>> table = [["Başlık", "Malzeme"], ["Acme 205CS Emaye Ocak", "Emaye"]];

        var leftover = Word(Report(set, table), "Emaye");

        Assert.Equal(TitleLeftoverCause.RemoveOff, leftover.Cause);
        Assert.Equal("Malzeme", leftover.Column);
        Assert.Contains("Çıkar", leftover.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The distinction the whole report turns on. This column <em>may</em> remove; it simply never
    /// matched, so telling the operator to switch removal on would send them after nothing.
    /// </summary>
    [Fact]
    public void AColumnThatNeverMatchedIsNotReportedAsARemovalSetting()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Malzeme", TitleAttributeKind.Alias, Aliases: [["Temperli Cam"]])]);

        List<List<string>> table = [["Başlık", "Malzeme"], ["Acme 205CS Siyah Cam Ocak", "Temperli Cam"]];

        var leftover = Word(Report(set, table), "Cam");

        Assert.Equal(TitleLeftoverCause.NeedsPartial, leftover.Cause);
        Assert.Equal("Malzeme", leftover.Column);
    }

    [Fact]
    public void AnInflectedWordIsReportedAsTheSuffixSetting()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias, Aliases: [["Ankastre Ocak"]])]);

        List<List<string>> table = [["Başlık", "Ürün Tipi"], ["Acme 222 Ankastre Ocaklar", "Ankastre Ocak"]];

        var leftover = Word(Report(set, table), "Ocaklar");

        Assert.Equal(TitleLeftoverCause.NeedsSuffix, leftover.Cause);
    }

    /// <summary>A model code is a leftover and is meant to be one.</summary>
    [Fact]
    public void AWordNoColumnCarriesIsReportedAsUnclaimed()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Marka", TitleAttributeKind.Alias, Aliases: [["Acme"]])]);

        List<List<string>> table = [["Başlık", "Marka"], ["Acme GLO 205CS Bask", "Acme"]];

        var leftover = Word(Report(set, table), "205CS");

        Assert.Equal(TitleLeftoverCause.Unclaimed, leftover.Cause);
        Assert.Null(leftover.Column);
    }

    /// <summary>
    /// A free-text column contains nearly every word of the title, so without a length limit it
    /// would be blamed for all of them — and being a column that does not remove, the card offered
    /// would be "turn removal on for the description", which would cut the title to pieces.
    /// </summary>
    [Fact]
    public void AProseColumnIsNeverBlamedForAWordItHappensToContain()
    {
        var set = new TitleRuleSet("Test", "Başlık",
        [
            new TitleAttributeRule("Açıklama", Remove: false),
            new TitleAttributeRule("Marka", TitleAttributeKind.Alias, Aliases: [["Acme"]]),
        ]);

        List<List<string>> table =
        [
            ["Başlık", "Açıklama", "Marka"],
            [
                "Acme GLO 205CS Bask Ocak",
                "Acme GLO 205CS Bask Ocak modeli siyah cam yüzeyi ile mutfağınıza şıklık katar",
                "Acme",
            ],
        ];

        Assert.All(Report(set, table), l => Assert.NotEqual("Açıklama", l.Column));
    }

    /// <summary>The same word across many rows is one line, not many.</summary>
    [Fact]
    public void AWordIsCountedOncePerFileWithItsRowTotal()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Marka", TitleAttributeKind.Alias, Aliases: [["Acme"]])]);

        List<List<string>> table =
        [
            ["Başlık", "Marka"],
            ["Acme GLO Bask", "Acme"],
            ["Acme GLO Rustik", "Acme"],
            ["Acme GLO Agena", "Acme"],
        ];

        Assert.Equal(3, Word(Report(set, table), "GLO").Rows);
    }

    [Fact]
    public void ATitleWithNothingLeftInItReportsNothing()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Marka", TitleAttributeKind.Alias, Aliases: [["Acme Ocak"]])]);

        List<List<string>> table = [["Başlık", "Marka"], ["Acme Ocak", "Acme Ocak"]];

        Assert.Empty(Report(set, table));
    }

    // -----------------------------------------------------------------
    // The cards
    // -----------------------------------------------------------------

    static IReadOnlyList<TitleFix> Cards(TitleRuleSet set, List<List<string>> table)
    {
        var rules = CompiledRuleSet.Compile(set);
        var rows = TitleCleanBuilder.Clean(rules, table);
        return TitleFixSuggester.SuggestSettings(rules, rows, TitleLeftoverReport.Build(rules, rows));
    }

    /// <summary>
    /// The case that forced these cards to be combined. This column is held back twice over — its
    /// value only partly appears <em>and</em> it may not remove — and turning on either one alone
    /// changes nothing, so a card offering one would be dropped for having no effect and the
    /// operator would be offered neither.
    /// </summary>
    [Fact]
    public void OneCardTurnsOnEverySwitchAColumnIsHeldBackBy()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Malzeme", TitleAttributeKind.Alias, Remove: false,
                Aliases: [["Temperli Cam"]])]);

        List<List<string>> table = [["Başlık", "Malzeme"], ["Acme 205CS Siyah Cam Ocak", "Temperli Cam"]];

        var fix = Assert.Single(Cards(set, table));

        Assert.Equal(TitleFixKind.EnableMatching, fix.Kind);
        Assert.Contains("Kısmi", fix.Action, StringComparison.Ordinal);
        Assert.Contains("Çıkar", fix.Action, StringComparison.Ordinal);

        var updated = TitleFixSuggester.Apply(set, [fix], [fix.Id]).AttributeList.Single();
        Assert.True(updated.Remove);
        Assert.True(updated.AllowPartial);

        var rules = CompiledRuleSet.Compile(TitleFixSuggester.Apply(set, [fix], [fix.Id]));
        Assert.Equal("Acme 205CS Siyah Ocak", TitleCleanBuilder.Clean(rules, table).Single().CleanTitle);
    }

    /// <summary>Several words held back by one column are one decision about that column.</summary>
    [Fact]
    public void SeveralWordsFromOneColumnBecomeOneCard()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Malzeme", TitleAttributeKind.Alias,
                Aliases: [["Temperli Cam"], ["Cam Seramik"]])]);

        List<List<string>> table =
        [
            ["Başlık", "Malzeme"],
            ["Acme 205CS Cam Ocak", "Temperli Cam"],
            ["Acme 222 Seramik Ocak", "Cam Seramik"],
        ];

        var fix = Assert.Single(Cards(set, table));
        Assert.Equal(2, fix.Rows);
    }

    /// <summary>A switch nobody is waiting on earns no card.</summary>
    [Fact]
    public void AColumnWithNothingLeftBehindGetsNoCard()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Marka", TitleAttributeKind.Alias, Aliases: [["Acme"]])]);

        List<List<string>> table = [["Başlık", "Marka"], ["Acme GLO 205CS", "Acme"]];

        Assert.Empty(Cards(set, table));
    }

    /// <summary>A switch already on is left on — the card adds, it does not reset.</summary>
    [Fact]
    public void ApplyingACardDoesNotTurnOffWhatWasAlreadyOn()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias, AllowPartial: true,
                Aliases: [["Ankastre Ocak"]])]);

        List<List<string>> table = [["Başlık", "Ürün Tipi"], ["Acme 222 Ankastre Ocaklar", "Ankastre Ocak"]];

        var fix = Assert.Single(Cards(set, table));
        var updated = TitleFixSuggester.Apply(set, [fix], [fix.Id]).AttributeList.Single();

        Assert.True(updated.AllowSuffix);
        Assert.True(updated.AllowPartial);
        Assert.True(updated.Remove);
    }
}
