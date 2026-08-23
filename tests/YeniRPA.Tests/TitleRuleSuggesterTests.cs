using YeniRPA.Web.Models;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// Reading a file and proposing a starting rule set for it. What comes out is a draft for the editor,
/// so being wrong is cheap — except on <c>Remove</c>, which decides what gets deleted, and which is
/// therefore measured against the real engine rather than guessed at.
/// </summary>
public class TitleRuleSuggesterTests
{
    /// <summary>A laptop export shaped like the one the team described.</summary>
    static List<List<string>> LaptopFile() =>
    [
        ["Başlık", "Marka", "İşlemci", "RAM", "Sabit Disk Kapasitesi", "Ekran Boyutu", "İşletim Sistemi", "Ekran Kartı"],
        ["Dell Pro Max 16 MC16250_3 Ultra 7 265H 32GB 1TB SSD 16\" W11P", "Dell", "Ultra 7 265H", "32 GB", "1 TB", "16\"", "W11P", "RTXPRO2000"],
        ["HP ProBook 450 G10 i5-1335U 16GB 512GB SSD 15.6\" W11P", "HP", "i5-1335U", "16 GB", "512 GB", "15.6\"", "W11P", "Iris Xe"],
        ["Lenovo ThinkPad E14 R7-7730U 16GB 512GB SSD 14\" W11H", "Lenovo", "R7-7730U", "16 GB", "512 GB", "14\"", "W11H", "Radeon"],
        ["Asus VivoBook X1504 i7-1355U 32GB 1TB SSD 15.6\" W11H", "Asus", "i7-1355U", "32 GB", "1 TB", "15.6\"", "W11H", "Iris Xe"],
    ];

    static TitleColumnHint Hint(TitleRuleSuggestion suggestion, string column) =>
        suggestion.Columns.First(c => string.Equals(c.Column, column, StringComparison.Ordinal));

    static TitleAttributeRule Rule(TitleRuleSuggestion suggestion, string column) =>
        suggestion.RuleSet.AttributeList.First(r => string.Equals(r.Column, column, StringComparison.Ordinal));

    [Fact]
    public void TheTitleColumnIsFoundByItsHeader()
    {
        Assert.Equal("Başlık", TitleRuleSuggester.Suggest(LaptopFile()).RuleSet.TitleColumn);
    }

    /// <summary>With no recognisable header, the longest text in the file is the title.</summary>
    [Fact]
    public void WithNoRecognisableHeaderTheLongestTextColumnIsTheTitle()
    {
        List<List<string>> table =
        [
            ["Kod", "Açıklama", "Marka"],
            ["A1", "Dell Latitude 5450 i5 16GB 512GB SSD 14\"", "Dell"],
            ["A2", "HP EliteBook 840 G10 i7 32GB 1TB SSD 14\"", "HP"],
        ];

        Assert.Equal("Açıklama", TitleRuleSuggester.Suggest(table).RuleSet.TitleColumn);
    }

    [Fact]
    public void TheTitleColumnIsNeverProposedAsAnAttribute()
    {
        var suggestion = TitleRuleSuggester.Suggest(LaptopFile());

        Assert.DoesNotContain(suggestion.RuleSet.AttributeList, r => r.Column == "Başlık");
    }

    // -----------------------------------------------------------------
    // Column kinds
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("RAM", "GB")]
    [InlineData("Sabit Disk Kapasitesi", "GB")]
    [InlineData("Ekran Boyutu", "\"")]
    public void AColumnWrittenAsNumberPlusUnitBecomesAMeasuredAttribute(string column, string firstUnit)
    {
        var rule = Rule(TitleRuleSuggester.Suggest(LaptopFile()), column);

        Assert.Equal(TitleAttributeKind.Measure, rule.Kind);
        Assert.Equal(firstUnit, rule.UnitList[0].Canonical);
    }

    /// <summary>
    /// Only the units a column actually uses are proposed — the whole family is not handed over.
    ///
    /// <para>A cache column is always written in MB. Given the entire GB/TB/MB family it would treat
    /// every "8GB" in a title — the graphics card's memory, the system RAM — as a candidate for
    /// itself and report a conflict against its own 40 MB on every row. On a real export that was 78
    /// false conflicts out of 100 rows.</para>
    /// </summary>
    [Fact]
    public void AMeasuredAttributeOnlyGetsTheUnitsItsColumnActuallyUses()
    {
        List<List<string>> table =
        [
            ["Başlık", "RAM", "Önbellek Boyutu", "Sabit disk kapasitesi"],
            ["TUF A16 RTX 5070 8GB 512GB SSD", "8 GB", "40 MB", "512 GB"],
            ["TUF F16 RTX 3050 16GB 1TB SSD", "16 GB", "24 MB", "1 TB"],
        ];

        var suggestion = TitleRuleSuggester.Suggest(table);

        Assert.Equal(["MB"], Rule(suggestion, "Önbellek Boyutu").UnitList.Select(u => u.Canonical));
        Assert.Equal(["GB"], Rule(suggestion, "RAM").UnitList.Select(u => u.Canonical));
        Assert.Equal(["GB", "TB"], Rule(suggestion, "Sabit disk kapasitesi").UnitList.Select(u => u.Canonical));
    }

    /// <summary>
    /// The point of the rule above: a cache column stops seeing the graphics card's memory as its own
    /// value and stops disagreeing with it.
    /// </summary>
    [Fact]
    public void ACacheColumnDoesNotReportAConflictAgainstAnotherAttributesGigabytes()
    {
        List<List<string>> table =
        [
            ["Başlık", "RAM", "Önbellek Boyutu"],
            ["TUF A16 RTX 5070 8GB 24GB 512GB SSD", "24 GB", "40 MB"],
            ["TUF F16 RTX 3050 6GB 16GB 1TB SSD", "16 GB", "24 MB"],
        ];

        var suggestion = TitleRuleSuggester.Suggest(table);
        var rows = TitleCleanBuilder.Clean(CompiledRuleSet.Compile(suggestion.RuleSet), table);

        Assert.All(rows, row => Assert.False(row.HasConflict));
        Assert.All(rows, row => Assert.Equal(
            TitleAttributeStatus.NotInTitle,
            row.Attributes.First(a => a.Column == "Önbellek Boyutu").Status));
    }

    /// <summary>A short, closed vocabulary gets a catalogue, which is what lets it notice a title
    /// naming a <em>different</em> known value instead of only failing to find its own.</summary>
    [Theory]
    [InlineData("Marka")]
    [InlineData("İşletim Sistemi")]
    public void AColumnWithFewShortValuesBecomesACatalogue(string column)
    {
        Assert.Equal(TitleAttributeKind.Alias, Rule(TitleRuleSuggester.Suggest(LaptopFile()), column).Kind);
    }

    // -----------------------------------------------------------------
    // Units the catalogue has never heard of
    // -----------------------------------------------------------------

    /// <summary>
    /// The built-in unit catalogue can be kept ahead of laptops. It cannot be kept ahead of a
    /// marketplace's whole category list at once, and every miss would otherwise be a code change.
    /// So a token a column writes consistently after its numbers is taken as its unit, whatever it is.
    ///
    /// <para><c>165 Hz</c> is the case from the operator's own export: it used to be classified as a
    /// catalogue of values because <c>GHz</c> and <c>MHz</c> were known and plain <c>Hz</c> was not.</para>
    /// </summary>
    [Theory]
    [InlineData("Ekran Yenileme Hızı", "165 Hz", "120 Hz", "Hz")]
    [InlineData("Gürültü Seviyesi", "52 dB", "47 dB", "dB")]
    [InlineData("Sıkma Devri", "1400 devir", "1200 devir", "devir")]
    [InlineData("Basınç", "15 bar", "19 bar", "bar")]
    [InlineData("Kamera", "48 MP", "12 MP", "MP")]
    [InlineData("Enerji Tüketimi", "0,8 kWh", "1,2 kWh", "kWh")]
    public void AUnitTheCatalogueDoesNotKnowIsStillAUnit(string column, string a, string b, string unit)
    {
        List<List<string>> table =
        [
            ["Başlık", column],
            ["Acme Model X " + a + " Cihaz", a],
            ["Acme Model Y " + b + " Cihaz", b],
        ];

        var rule = Rule(TitleRuleSuggester.Suggest(table), column);

        Assert.Equal(TitleAttributeKind.Measure, rule.Kind);
        Assert.Equal(unit, rule.UnitList.Single().Canonical);
    }

    /// <summary>
    /// A unit nobody declared has no canonical spelling to correct towards. Rewriting a processor
    /// model of "8745HX" into "8745 HX" would damage the cell, so the value is matched and removed
    /// but never rewritten — and the editor is told why.
    /// </summary>
    [Fact]
    public void AnUndeclaredUnitIsMatchedButTheCellIsNotRewritten()
    {
        List<List<string>> table =
        [
            ["Başlık", "Gürültü Seviyesi"],
            ["Acme Model X 52 dB Bulaşık Makinesi", "52 dB"],
            ["Acme Model Y 47 dB Bulaşık Makinesi", "47 dB"],
        ];

        var suggestion = TitleRuleSuggester.Suggest(table);

        Assert.False(Rule(suggestion, "Gürültü Seviyesi").Correct);
        Assert.NotNull(Hint(suggestion, "Gürültü Seviyesi").Note);
    }

    /// <summary>A unit the catalogue does know keeps its spelling variants and its factor.</summary>
    [Fact]
    public void AKnownUnitStillBringsItsSpellingsAndItsFactor()
    {
        List<List<string>> table =
        [
            ["Başlık", "Kapasite", "Ekran"],
            ["Acme 512 GB 15.6\" Notebook", "512 GB", "15,6 inç"],
            ["Acme 1 TB 14\" Notebook", "1 TB", "14 inç"],
        ];

        var suggestion = TitleRuleSuggester.Suggest(table);

        var storage = Rule(suggestion, "Kapasite");
        Assert.Equal(["GB", "TB"], storage.UnitList.Select(u => u.Canonical));
        Assert.Equal(1024, storage.UnitList.First(u => u.Canonical == "TB").Factor);
        Assert.True(storage.Correct);

        // The inch mark and the word are the same unit, so a title's 15.6" matches a cell's 15,6 inç.
        var screen = Rule(suggestion, "Ekran");
        Assert.Equal("\"", screen.UnitList.Single().Canonical);
    }

    /// <summary>
    /// A column of bare numbers reads its unit off the titles, not off its own name. The old
    /// column-name hint list only ever knew about laptops; this works whatever the column is called.
    /// </summary>
    [Fact]
    public void AColumnOfBareNumbersReadsItsUnitFromTheTitles()
    {
        List<List<string>> table =
        [
            ["Başlık", "Hacim"],
            ["Acme Model X 8L Fritöz", "8"],
            ["Acme Model Y 5L Fritöz", "5"],
            ["Acme Model Z 12L Fritöz", "12"],
        ];

        var suggestion = TitleRuleSuggester.Suggest(table);
        var rule = Rule(suggestion, "Hacim");

        Assert.Equal(TitleAttributeKind.Measure, rule.Kind);
        Assert.Contains(rule.UnitList, u => FoldedTitle.Fold(u.Canonical) == "l");
        Assert.NotNull(Hint(suggestion, "Hacim").Note);
    }

    /// <summary>
    /// A unit read off the titles is a guess about which measurement in the title belongs to this
    /// column, and a bare number can sit next to somebody else's. A core-count column of "16" finds
    /// the screen size's 16" and comes back claiming inches — and whichever of the two rules sorts
    /// first would then take that span, silently attaching the screen size to the wrong column.
    ///
    /// <para>Where a column declares a unit from its own data, it has the better claim, and the
    /// inferred one goes back to being plain text.</para>
    /// </summary>
    [Fact]
    public void AnInferredUnitYieldsToAColumnThatDeclaresItFromItsOwnData()
    {
        List<List<string>> table =
        [
            ["Başlık", "Ekran Boyutu", "İşlemci Çekirdek Sayısı"],
            ["Acme Book 16\" Notebook", "16 inç", "16"],
            ["Acme Book 14\" Notebook", "14 inç", "14"],
            ["Acme Book 16\" Ultrabook", "16 inç", "16"],
        ];

        var suggestion = TitleRuleSuggester.Suggest(table);

        // The screen size keeps the inch unit and keeps working.
        var screen = Rule(suggestion, "Ekran Boyutu");
        Assert.Equal(TitleAttributeKind.Measure, screen.Kind);
        Assert.Equal(3, Hint(suggestion, "Ekran Boyutu").Matched);

        // The core count does not take it.
        var cores = Rule(suggestion, "İşlemci Çekirdek Sayısı");
        Assert.NotEqual(TitleAttributeKind.Measure, cores.Kind);
        Assert.False(cores.Remove);
        Assert.NotNull(Hint(suggestion, "İşlemci Çekirdek Sayısı").Note);
    }

    // -----------------------------------------------------------------
    // Not a laptop
    // -----------------------------------------------------------------

    /// <summary>
    /// The proof that nothing in this module knows about computers. A white-goods file, whose units
    /// and vocabulary share nothing with the laptop one, goes in and comes out cleaned — with the
    /// model surviving and the energy class, which no column claims, left alone.
    /// </summary>
    [Fact]
    public void AWhiteGoodsFileWorksWithNoCodeThatKnowsAboutWhiteGoods()
    {
        List<List<string>> table =
        [
            ["Başlık", "Marka", "Yıkama Kapasitesi", "Sıkma Devri", "Gürültü Seviyesi", "Renk"],
            ["Bosch Serie 6 WGG24400TR 9 kg 1400 devir 52 dB A+++ Beyaz Çamaşır Makinesi",
                "Bosch", "9 kg", "1400 devir", "52 dB", "Beyaz"],
            ["Bosch Serie 4 WGA142X0TR 8 kg 1200 devir 54 dB A+++ Beyaz Çamaşır Makinesi",
                "Bosch", "8 kg", "1200 devir", "54 dB", "Beyaz"],
            ["Siemens iQ500 WM14N2X0TR 9 kg 1400 devir 53 dB A+++ Beyaz Çamaşır Makinesi",
                "Siemens", "9 kg", "1400 devir", "53 dB", "Beyaz"],
        ];

        var suggestion = TitleRuleSuggester.Suggest(table, "Çamaşır Makinesi");

        Assert.Equal(TitleAttributeKind.Measure, Rule(suggestion, "Yıkama Kapasitesi").Kind);
        Assert.Equal(TitleAttributeKind.Measure, Rule(suggestion, "Sıkma Devri").Kind);
        Assert.Equal(TitleAttributeKind.Measure, Rule(suggestion, "Gürültü Seviyesi").Kind);

        var rows = TitleCleanBuilder.Clean(CompiledRuleSet.Compile(suggestion.RuleSet), table);

        // The model survives; "A+++" survives because no column claims it; everything a column
        // names is gone.
        Assert.Equal("Serie 6 WGG24400TR A+++ Çamaşır Makinesi", rows[0].CleanTitle);
        Assert.All(rows, row => Assert.False(row.HasConflict));
    }

    // -----------------------------------------------------------------
    // Remove
    // -----------------------------------------------------------------

    /// <summary>
    /// The judgement that matters. A column whose values are actually in the titles is proposed for
    /// removal; one whose values are not is proposed switched off.
    /// </summary>
    [Theory]
    [InlineData("Marka")]
    [InlineData("İşlemci")]
    [InlineData("RAM")]
    [InlineData("Ekran Boyutu")]
    [InlineData("İşletim Sistemi")]
    public void AColumnFoundInTheTitlesIsProposedForRemoval(string column)
    {
        Assert.True(Rule(TitleRuleSuggester.Suggest(LaptopFile()), column).Remove);
    }

    /// <summary>
    /// "Ekran Kartı" holds RTXPRO2000, Iris Xe and Radeon — none of which appear in these titles the
    /// way the cell spells them. It arrives switched off, which is exactly how "RTXPRO2000" survives
    /// the reference title with nobody configuring anything.
    /// </summary>
    [Fact]
    public void AColumnWhoseValuesAreNotInTheTitlesArrivesSwitchedOff()
    {
        var suggestion = TitleRuleSuggester.Suggest(LaptopFile());

        Assert.False(Rule(suggestion, "Ekran Kartı").Remove);
        Assert.Equal(0, Hint(suggestion, "Ekran Kartı").Matched);
    }

    /// <summary>
    /// Two attributes glued into one token ("1TBSSD") must still be proposed for removal.
    ///
    /// <para>The scan measures each rule by running it, and a span may only cut into a word where
    /// another accepted span continues from it — so the capacity inside "1TBSSD" is valid only while
    /// a disk-type rule is there to take the "SSD". Measuring one column at a time reported zero
    /// matches for both halves of every glued token, which is exactly how these titles are written.
    /// </para>
    /// </summary>
    [Fact]
    public void TwoAttributesGluedIntoOneTokenAreStillProposedForRemoval()
    {
        List<List<string>> table =
        [
            ["Başlık", "Sabit Disk Kapasitesi", "Sabit Disk Tipi"],
            ["Dell Pro Max 16 MC16250_3 1TBSSD", "1TB", "SSD"],
            ["HP ProBook 450 G10 512GBSSD", "512GB", "SSD"],
            ["Lenovo ThinkPad E14 512GBSSD", "512GB", "SSD"],
        ];

        var suggestion = TitleRuleSuggester.Suggest(table);

        Assert.True(Rule(suggestion, "Sabit Disk Kapasitesi").Remove);
        Assert.True(Rule(suggestion, "Sabit Disk Tipi").Remove);
        Assert.Equal(3, Hint(suggestion, "Sabit Disk Kapasitesi").Matched);
        Assert.Equal(3, Hint(suggestion, "Sabit Disk Tipi").Matched);

        var rows = TitleCleanBuilder.Clean(CompiledRuleSet.Compile(suggestion.RuleSet), table);
        Assert.Equal("Dell Pro Max 16 MC16250_3", rows[0].CleanTitle);
    }

    /// <summary>
    /// A column of bare numbers is the one case a literal search must never be trusted with: "16" is
    /// a screen size, a model name and a fragment of a model code at once. Where the column name
    /// does not say what the unit is, removal stays off and the reason is reported.
    /// </summary>
    [Fact]
    public void AColumnOfBareNumbersWithNoRecognisableNameIsNeverProposedForRemoval()
    {
        List<List<string>> table =
        [
            ["Başlık", "Kutu Adedi"],
            ["Dell Pro Max 16 MC16250_3 32GB", "16"],
            ["HP ProBook 450 G10 16GB", "12"],
        ];

        var suggestion = TitleRuleSuggester.Suggest(table);

        Assert.False(Rule(suggestion, "Kutu Adedi").Remove);
        Assert.NotNull(Hint(suggestion, "Kutu Adedi").Note);
    }

    /// <summary>Where the column name does say what the unit is, the bare numbers become a measured
    /// attribute — the team's own "cell says 16, title says 16GB" case — and it is flagged for review.</summary>
    [Fact]
    public void AColumnOfBareNumbersNamedLikeAMeasureGetsItsUnitFromItsName()
    {
        List<List<string>> table =
        [
            ["Başlık", "RAM"],
            ["Dell Pro Max 16 MC16250_3 32GB SSD", "32"],
            ["HP ProBook 450 G10 16GB SSD", "16"],
        ];

        var suggestion = TitleRuleSuggester.Suggest(table);
        var rule = Rule(suggestion, "RAM");

        Assert.Equal(TitleAttributeKind.Measure, rule.Kind);
        Assert.Equal("GB", rule.UnitList[0].Canonical);
        Assert.True(rule.Remove);
        Assert.NotNull(Hint(suggestion, "RAM").Note);
    }

    // -----------------------------------------------------------------
    // Housekeeping
    // -----------------------------------------------------------------

    [Fact]
    public void AnEmptyColumnIsLeftOutAndSaidSo()
    {
        List<List<string>> table =
        [
            ["Başlık", "Marka", "Notlar"],
            ["Dell Latitude 5450 i5", "Dell", ""],
            ["HP EliteBook 840 i7", "HP", ""],
        ];

        var suggestion = TitleRuleSuggester.Suggest(table);

        Assert.DoesNotContain(suggestion.RuleSet.AttributeList, r => r.Column == "Notlar");
        Assert.Contains(suggestion.Notes, n => n.Contains("Notlar", StringComparison.Ordinal));
    }

    /// <summary>Longer values first, so that where two attributes could claim one stretch of title
    /// the more specific one gets it.</summary>
    [Fact]
    public void AttributesAreOrderedLongestTypicalValueFirst()
    {
        var attributes = TitleRuleSuggester.Suggest(LaptopFile()).RuleSet.AttributeList;

        var processor = attributes.ToList().FindIndex(r => r.Column == "İşlemci");
        var brand = attributes.ToList().FindIndex(r => r.Column == "Marka");

        Assert.True(processor < brand, "the longer processor value should be evaluated before the brand");
    }

    /// <summary>
    /// The end of the loop: a proposal, run unchanged, cleans the file it was proposed from. Whatever
    /// the editor is shown has to be what the run does.
    /// </summary>
    [Fact]
    public void TheProposedRuleSetRunsOnTheFileItWasProposedFrom()
    {
        var table = LaptopFile();
        var suggestion = TitleRuleSuggester.Suggest(table, "Laptop");

        var rows = TitleCleanBuilder.Clean(CompiledRuleSet.Compile(suggestion.RuleSet), table);

        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.False(row.HasConflict));

        // Everything a column claims is gone...
        Assert.DoesNotContain("Dell", rows[0].CleanTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("32GB", rows[0].CleanTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("W11P", rows[0].CleanTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("Ultra 7 265H", rows[0].CleanTitle, StringComparison.Ordinal);

        // ...and the model survives, together with "SSD", which this file has no column for. Nothing
        // is removed for looking removable.
        Assert.Equal("Pro Max 16 MC16250_3 SSD", rows[0].CleanTitle);
    }
}
