using YeniRPA.Web.Models;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// The rules that decide which characters get cut out of a product title.
///
/// <para>This is the module's whole risk surface. Every other part of it can be checked by looking
/// at the screen; a title that lost four characters too many looks perfectly reasonable and is only
/// noticed weeks later, in the catalogue, with the original gone.</para>
/// </summary>
public class TitleCleanerTests
{
    static readonly MeasureUnit Gb = new("GB", ["gb", "gbyte", "gigabayt"], 1);
    static readonly MeasureUnit Tb = new("TB", ["tb", "tbyte", "terabayt"], 1024);
    static readonly MeasureUnit Inch = new("\"", ["\"", "''", "inç", "inch"]);

    /// <summary>
    /// The laptop standard the team supplied, as a rule set.
    ///
    /// <para>Note the <c>Çözünürlük</c> column, which is not in the attribute list they wrote out.
    /// Their expected result drops "FullHD+" from the title, and nothing may be dropped that a
    /// column does not claim — so the column has to exist for the reference result to be reachable.
    /// "RTXPRO2000" survives for the mirror-image reason: no column claims it.</para>
    /// </summary>
    static TitleRuleSet LaptopRules() => new(
        "Laptop",
        "Başlık",
        [
            new TitleAttributeRule("Ürün Tipi"),
            new TitleAttributeRule("İşlemci"),
            new TitleAttributeRule("Marka"),
            new TitleAttributeRule("Sabit Disk Kapasitesi", TitleAttributeKind.Measure, Units: [Gb, Tb]),
            new TitleAttributeRule("RAM", TitleAttributeKind.Measure, Units: [Gb]),
            new TitleAttributeRule("Ekran Boyutu", TitleAttributeKind.Measure, Units: [Inch]),
            new TitleAttributeRule("Sabit Disk Tipi", TitleAttributeKind.Alias,
                Aliases: [["SSD"], ["HDD"], ["eMMC"]]),
            new TitleAttributeRule("İşletim Sistemi", TitleAttributeKind.Alias,
                Aliases: [["W11P", "Windows 11 Pro", "Win 11 Pro"], ["W11H", "Windows 11 Home"]]),
            new TitleAttributeRule("Çözünürlük", TitleAttributeKind.Alias,
                Aliases: [["FullHD+"], ["FullHD"], ["4K"]]),
        ]);

    const string ReferenceTitle =
        "Dell Pro Max 16 MC16250_3 Ultra 7 265H 32GB 1TBSSD RTXPRO2000 16\" FullHD+ W11P Dizüstü İş İstasyonu";

    static TitleCleanRow ReferenceRow() => Run(
        LaptopRules(),
        ReferenceTitle,
        ("Marka", "Dell"),
        ("İşlemci", "Ultra 7 265H"),
        ("RAM", "32GB"),
        ("Sabit Disk Kapasitesi", "1TB"),
        ("Sabit Disk Tipi", "SSD"),
        ("Ekran Boyutu", "16\""),
        ("İşletim Sistemi", "W11P"),
        ("Ürün Tipi", "Dizüstü İş İstasyonu"),
        ("Çözünürlük", "FullHD+"));

    // -----------------------------------------------------------------
    // 1. The reference case
    // -----------------------------------------------------------------

    [Fact]
    public void TheReferenceTitleCleansToTheModelAndWhatNoColumnClaims()
    {
        Assert.Equal("Pro Max 16 MC16250_3 RTXPRO2000", ReferenceRow().CleanTitle);
    }

    // -----------------------------------------------------------------
    // 2. The one that matters most
    // -----------------------------------------------------------------

    /// <summary>
    /// "16" appears three times in the reference title: in the model name, inside the model code and
    /// as the screen size. Only the screen size may go.
    ///
    /// <para>A plain replace of the attribute value would take all three and leave "Pro Max
    /// MC250_3" — a model that does not exist, written back to the marketplace. This test is the
    /// guard on the two rules that prevent it: a measured value is only matched together with its
    /// unit, and a span may not start or end inside a word.</para>
    /// </summary>
    [Fact]
    public void ANumberInTheModelNameSurvivesWhileTheSameNumberAsAScreenSizeGoes()
    {
        var row = ReferenceRow();

        Assert.Contains("Pro Max 16", row.CleanTitle, StringComparison.Ordinal);
        Assert.Contains("MC16250_3", row.CleanTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("16\"", row.CleanTitle, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // 3. Two attributes glued into one token
    // -----------------------------------------------------------------

    /// <summary>
    /// "1TBSSD" is a disk capacity and a disk type with no separator between them. Both are removed
    /// and the token disappears entirely — neither a stray "SSD" nor a stray "1TB" is left behind.
    /// </summary>
    [Fact]
    public void OneTokenCarryingTwoAttributesComesApartAndBothHalvesGo()
    {
        var row = ReferenceRow();

        Assert.DoesNotContain("1TB", row.CleanTitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SSD", row.CleanTitle, StringComparison.OrdinalIgnoreCase);

        // The type was already canonical; the capacity gets its unit spaced off the number.
        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Sabit Disk Tipi").Status);
        Assert.Equal("SSD", Attr(row, "Sabit Disk Tipi").Value);
        Assert.Equal(TitleAttributeStatus.Corrected, Attr(row, "Sabit Disk Kapasitesi").Status);
        Assert.Equal("1 TB", Attr(row, "Sabit Disk Kapasitesi").Value);
    }

    // -----------------------------------------------------------------
    // 4-5. Spelling differences are not disagreements
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("32 GB")]
    [InlineData("32GB")]
    [InlineData("32 gb")]
    [InlineData("32gb")]
    public void HowTheCellSpacesTheUnitDoesNotChangeWhetherItMatches(string cell)
    {
        var row = Run(LaptopRules(), "Acme Book 32GB 512GB SSD", ("RAM", cell), ("Sabit Disk Kapasitesi", "512GB"));

        var ram = Attr(row, "RAM");
        Assert.True(ram.Status is TitleAttributeStatus.Ok or TitleAttributeStatus.Corrected);
        Assert.Equal("32 GB", ram.Value);
        Assert.DoesNotContain("32GB", row.CleanTitle, StringComparison.Ordinal);
    }

    /// <summary>The team's own example: the cell says "16", the title says "16GB", so the cell is
    /// completed rather than reported.</summary>
    [Fact]
    public void ABareNumberInTheCellTakesItsUnitFromTheTitle()
    {
        var row = Run(LaptopRules(), "Acme Book 16GB 512GB SSD", ("RAM", "16"), ("Sabit Disk Kapasitesi", "512GB"));

        var ram = Attr(row, "RAM");
        Assert.Equal(TitleAttributeStatus.Corrected, ram.Status);
        Assert.Equal("16", ram.OriginalValue);
        Assert.Equal("16 GB", ram.Value);
    }

    /// <summary>A bare number the title could attach to two different units is not guessed at.</summary>
    [Fact]
    public void ABareNumberWithTwoPossibleUnitsIsReportedRatherThanPicked()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Depolama", TitleAttributeKind.Measure, Units: [Gb, Tb])]);

        // 1 GB and 1 TB are both in the title, and the cell says only "1".
        var row = Run(rules, "Acme 1GB 1TB Cihaz", ("Depolama", "1"));

        Assert.Equal(TitleAttributeStatus.Ambiguous, Attr(row, "Depolama").Status);
        Assert.Equal("Acme 1GB 1TB Cihaz", row.CleanTitle);
    }

    // -----------------------------------------------------------------
    // The same value twice
    // -----------------------------------------------------------------

    /// <summary>
    /// A graphics card's own memory sits next to the system RAM, and on this row they are the same
    /// size. Removing every match would delete the card's "8GB" out of the title — and the row above,
    /// where the RAM is 24 GB, would come out perfectly, so the damage shows up on some rows and not
    /// others.
    ///
    /// <para>Taken from a real marketplace export. Nothing is removed and the row is reported.</para>
    /// </summary>
    [Fact]
    public void AValueAppearingTwiceIsReportedRatherThanRemovedTwice()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("RAM", TitleAttributeKind.Measure, Units: [Gb])]);

        var title = "TUF A16 R7 8745HX RTX 5070 8GB 8GB 512GB SSD 16\" FHD+";
        var row = Run(rules, title, ("RAM", "8 GB"));

        Assert.Equal(TitleAttributeStatus.Ambiguous, Attr(row, "RAM").Status);
        Assert.Equal(title, row.CleanTitle);
        Assert.Contains("2 kez", Attr(row, "RAM").Message);
    }

    /// <summary>The same title, with the other column ruled for too: now each occurrence is claimed
    /// by its own attribute and both go. This is the fix the message points the operator at.</summary>
    [Fact]
    public void GivingTheOtherColumnItsOwnRuleResolvesTheRepeat()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
        [
            new TitleAttributeRule("Grafik Bellek", TitleAttributeKind.Measure, Units: [Gb]),
            new TitleAttributeRule("RAM", TitleAttributeKind.Measure, Units: [Gb]),
        ]);

        var row = Run(
            rules,
            "TUF A16 R7 8745HX RTX 5070 8GB 8GB 512GB SSD 16\" FHD+",
            ("Grafik Bellek", "8 GB"), ("RAM", "8 GB"));

        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "RAM").Status);
        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Grafik Bellek").Status);
        Assert.Equal("TUF A16 R7 8745HX RTX 5070 512GB SSD 16\" FHD+", row.CleanTitle);
    }

    /// <summary>Two different sizes are not a repeat: the card's 8 GB stays, the 24 GB of RAM goes.</summary>
    [Fact]
    public void ADifferentSizeNextToItIsNotARepeat()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("RAM", TitleAttributeKind.Measure, Units: [Gb])]);

        var row = Run(rules, "TUF A16 RTX 5070 8GB 24GB 1TB SSD", ("RAM", "24 GB"));

        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "RAM").Status);
        Assert.Equal("TUF A16 RTX 5070 8GB 1TB SSD", row.CleanTitle);
    }

    // -----------------------------------------------------------------
    // Stray whitespace
    // -----------------------------------------------------------------

    /// <summary>
    /// Titles are typed by hand and carry stray double spaces — the real export writes
    /// "RTX 5070  8GB" with two. An exact search lets that invisible difference decide whether a
    /// rule matches, and the operator has no way to see what is wrong because there is nothing to
    /// see. Everything but the width of the gap still has to match exactly.
    /// </summary>
    [Theory]
    [InlineData("Acme RTX 5070  8GB Notebook")]
    [InlineData("Acme RTX 5070   8GB Notebook")]
    [InlineData("Acme RTX 5070\t8GB Notebook")]
    public void AStrayDoubleSpaceInTheTitleDoesNotStopAMatch(string title)
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ekran Kartı", TitleAttributeKind.Alias,
                Aliases: [["RTX 5070 8GB"]])]);

        var row = Run(rules, title, ("Ekran Kartı", "RTX 5070 8GB"));

        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Ekran Kartı").Status);
        Assert.Equal("Acme Notebook", row.CleanTitle);
    }

    /// <summary>
    /// The recipe for a repeated value, end to end and taken from the real export: a rule that claims
    /// the longer phrase around the other occurrence, and is not allowed to remove it, protects it —
    /// leaving the RAM's own "8GB" as the only one left for the RAM rule.
    /// </summary>
    [Fact]
    public void ARuleThatMayNotRemoveProtectsTheTextItClaims()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
        [
            new TitleAttributeRule("Ekran Kartı", TitleAttributeKind.Alias,
                Remove: false, Correct: false,
                Aliases: [["GeForce RTX 5070", "RTX 5070 8GB"]]),
            new TitleAttributeRule("RAM", TitleAttributeKind.Measure, Units: [Gb]),
        ]);

        var row = Run(
            rules,
            "TUF A16 R7 8745HX RTX 5070  8GB 8GB 512GB SSD 16\" FHD+",
            ("Ekran Kartı", "GeForce RTX 5070"), ("RAM", "8 GB"));

        // The card keeps its memory, the system RAM goes, and neither cell is rewritten.
        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "RAM").Status);
        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Ekran Kartı").Status);
        Assert.Equal("GeForce RTX 5070", Attr(row, "Ekran Kartı").Value);
        Assert.False(row.HasConflict);
        Assert.Equal("TUF A16 R7 8745HX RTX 5070 8GB 512GB SSD 16\" FHD+", row.CleanTitle);
    }

    // -----------------------------------------------------------------
    // Bare numbers
    // -----------------------------------------------------------------

    /// <summary>
    /// The whole module rests on "a measured value is only recognised with its unit". A Text or Alias
    /// attribute whose cell holds a bare number is asking to delete an unqualified number from a
    /// title — the same deletion the Measure rules exist to refuse — so it is refused there too.
    /// </summary>
    [Fact]
    public void ATextAttributeHoldingABareNumberIsNeverCutOutOfTheTitle()
    {
        var rules = new TitleRuleSet("Test", "Başlık", [new TitleAttributeRule("İşlemci")]);

        var row = Run(rules, "Dell Pro Max 16 MC16250_3 Notebook", ("İşlemci", "16"));

        Assert.Equal(TitleAttributeStatus.Ambiguous, Attr(row, "İşlemci").Status);
        Assert.Equal("Dell Pro Max 16 MC16250_3 Notebook", row.CleanTitle);
    }

    /// <summary>
    /// And it is refused per row, not per column: one numeric value among a hundred processor models
    /// must not cost the other ninety-nine their removal.
    /// </summary>
    [Fact]
    public void OneNumericValueDoesNotStopTheRestOfTheColumnBeingCleaned()
    {
        var rules = new TitleRuleSet("Test", "Başlık", [new TitleAttributeRule("İşlemci")]);

        var normal = Run(rules, "TUF A16 R7 8745HX Gaming Laptop", ("İşlemci", "8745HX"));
        Assert.Equal(TitleAttributeStatus.Ok, Attr(normal, "İşlemci").Status);
        Assert.Equal("TUF A16 R7 Gaming Laptop", normal.CleanTitle);

        var numeric = Run(rules, "TUF A16 R7 9955 Gaming Laptop", ("İşlemci", "9955"));
        Assert.Equal(TitleAttributeStatus.Ambiguous, Attr(numeric, "İşlemci").Status);
        Assert.Equal("TUF A16 R7 9955 Gaming Laptop", numeric.CleanTitle);
    }

    /// <summary>A measured attribute is unaffected — that is what the unit is for.</summary>
    [Fact]
    public void AMeasuredAttributeStillTakesABareNumberFromItsCell()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("RAM", TitleAttributeKind.Measure, Units: [Gb])]);

        var row = Run(rules, "Acme Book 16GB SSD", ("RAM", "16"));

        Assert.Equal(TitleAttributeStatus.Corrected, Attr(row, "RAM").Status);
        Assert.Equal("16 GB", Attr(row, "RAM").Value);
    }

    // -----------------------------------------------------------------
    // The marketplace's field-code row
    // -----------------------------------------------------------------

    /// <summary>
    /// A Mirakl import template carries the technical field codes on the row under the header. Read
    /// as data it seeds every catalogue with a field code and produces a junk output row — but it
    /// cannot be dropped either, because the marketplace's own importer needs it back.
    /// </summary>
    [Fact]
    public void TheMarketplaceFieldCodeRowIsNotCleanedButIsNotLostEither()
    {
        var rules = CompiledRuleSet.Compile(new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Marka")]));

        List<List<string>> table =
        [
            ["Başlık", "Marka", "EAN", "Kategori", "SHOP_SKU", "Renk"],
            ["TITLE__TR_TR", "BRAND", "EAN", "CATEGORY", "SHOP_SKU", "PROD_FEAT_00003"],
            ["ASUS TUF A16 Gaming Laptop", "ASUS", "078141", "DİZÜSTÜ", "1239866", "Siyah"],
        ];

        var rows = TitleCleanBuilder.Clean(rules, table, out var skipped);

        Assert.True(skipped);
        Assert.Single(rows);
        Assert.Equal(3, rows[0].RowNumber);
        Assert.Equal("TUF A16 Gaming Laptop", rows[0].CleanTitle);
    }

    /// <summary>
    /// The guard has to be narrow: a false positive silently drops a real product. A title always
    /// contains whitespace, and that is the first thing checked.
    /// </summary>
    [Fact]
    public void ARealProductRowIsNeverMistakenForTheFieldCodeRow()
    {
        var rules = CompiledRuleSet.Compile(new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Marka")]));

        List<List<string>> table =
        [
            ["Başlık", "Marka", "EAN", "Kategori", "SHOP_SKU", "Renk"],
            ["ASUS TUF A16 Gaming Laptop", "ASUS", "078141", "DİZÜSTÜ", "1239866", "Siyah"],
        ];

        var rows = TitleCleanBuilder.Clean(rules, table, out var skipped);

        Assert.False(skipped);
        Assert.Single(rows);
    }

    // -----------------------------------------------------------------
    // 6. Disagreement
    // -----------------------------------------------------------------

    /// <summary>
    /// The title says one thing and the cell says another. Which is right is not knowable here, so
    /// the title is left exactly as it was and the row is reported.
    /// </summary>
    [Fact]
    public void ADisagreementLeavesTheTitleAloneAndNamesBothSides()
    {
        var row = Run(LaptopRules(), "Acme Book 16GB 512GB SSD", ("RAM", "32GB"), ("Sabit Disk Kapasitesi", "512GB"));

        var ram = Attr(row, "RAM");
        Assert.Equal(TitleAttributeStatus.Conflict, ram.Status);
        Assert.Equal("32GB", ram.Value);
        Assert.Contains("16 GB", ram.Message);
        Assert.Contains("32GB", ram.Message);

        Assert.Contains("16GB", row.CleanTitle, StringComparison.Ordinal);
        Assert.True(row.HasConflict);
    }

    /// <summary>A disagreement on one attribute does not stop the other seven being cleaned.</summary>
    [Fact]
    public void OneDisagreementDoesNotBlockTheRestOfTheRow()
    {
        var row = Run(LaptopRules(), "Acme Book 16GB 512GB SSD", ("RAM", "32GB"), ("Sabit Disk Kapasitesi", "512GB"));

        Assert.Equal(TitleAttributeStatus.Corrected, Attr(row, "Sabit Disk Kapasitesi").Status);
        Assert.Equal("512 GB", Attr(row, "Sabit Disk Kapasitesi").Value);
        Assert.DoesNotContain("512GB", row.CleanTitle, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // 7. Turkish
    // -----------------------------------------------------------------

    /// <summary>
    /// No built-in comparison collides every Turkish spelling of these words — the reasoning is on
    /// <c>SellerGroupMap.FoldName</c>. "İ" lowercases to "i" plus a combining dot, "ı" is a separate
    /// letter to the invariant culture, and a tr-TR comparison fixes one pair by breaking the other.
    /// </summary>
    [Theory]
    [InlineData("Acme DİZÜSTÜ Bilgisayar", "Dizüstü")]
    [InlineData("Acme dizüstü Bilgisayar", "DİZÜSTÜ")]
    [InlineData("Acme DIZUSTU Bilgisayar", "Dizüstü")]
    [InlineData("Acme dizustu Bilgisayar", "DİZÜSTÜ")]
    public void TurkishCaseAndAccentsDoNotDecideWhetherAValueIsFound(string title, string cell)
    {
        var rules = new TitleRuleSet("Test", "Başlık", [new TitleAttributeRule("Ürün Tipi")]);
        var row = Run(rules, title, ("Ürün Tipi", cell));

        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Ürün Tipi").Status);
        Assert.Equal("Acme Bilgisayar", row.CleanTitle);
    }

    // -----------------------------------------------------------------
    // 8. Decimals and quote marks
    // -----------------------------------------------------------------

    /// <summary>A Turkish sheet writes 15,6 and the marketplace writes 15.6. One value.</summary>
    [Theory]
    [InlineData("15,6\"")]
    [InlineData("15.6\"")]
    [InlineData("15,6 inç")]
    [InlineData("15.6 inch")]
    public void EitherDecimalSeparatorAndEitherInchSpellingReachTheSameValue(string cell)
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ekran Boyutu", TitleAttributeKind.Measure, Units: [Inch])]);

        var row = Run(rules, "Acme Book 15.6\" Notebook", ("Ekran Boyutu", cell));

        var screen = Attr(row, "Ekran Boyutu");
        Assert.True(screen.Status is TitleAttributeStatus.Ok or TitleAttributeStatus.Corrected);
        Assert.Equal("15.6\"", screen.Value);
        Assert.Equal("Acme Book Notebook", row.CleanTitle);
    }

    /// <summary>A title pasted out of Word carries a curly inch mark. Same screen size.</summary>
    [Fact]
    public void ACurlyInchMarkIsStillAnInchMark()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ekran Boyutu", TitleAttributeKind.Measure, Units: [Inch])]);

        var row = Run(rules, "Acme Book 16” Notebook", ("Ekran Boyutu", "16\""));

        Assert.Equal("Acme Book Notebook", row.CleanTitle);
    }

    /// <summary>Two convertible units are one value, so this is not reported as a disagreement.</summary>
    [Fact]
    public void AUnitWithAConversionFactorComparesInTheBaseUnit()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Depolama", TitleAttributeKind.Measure, Units: [Gb, Tb])]);

        var row = Run(rules, "Acme 1TB SSD", ("Depolama", "1024 GB"));

        var storage = Attr(row, "Depolama");
        Assert.Equal(TitleAttributeStatus.Corrected, storage.Status);
        Assert.Equal("1 TB", storage.Value);
    }

    // -----------------------------------------------------------------
    // 9. Never approximately
    // -----------------------------------------------------------------

    /// <summary>
    /// <b>Nothing here matches approximately, and nothing here may be made to.</b> No Levenshtein, no
    /// "starts with", no "closest value" — the same rule as <c>CarrierNames</c> and
    /// <c>SellerGroupMap</c>, for a sharper version of the same reason. Those two would misroute a
    /// message; this one silently deletes the wrong characters out of a product title and writes the
    /// result back to the marketplace, where the original is gone.
    ///
    /// <para>This test exists to fail if anyone widens the matching.</para>
    /// </summary>
    [Theory]
    [InlineData("Dellux Notebook 8GB", "Marka", "Dell")]
    [InlineData("Acme SSDX Book", "Sabit Disk Tipi", "SSD")]
    [InlineData("Acme Book Dizüstüler", "Ürün Tipi", "Dizüstü")]
    public void AValueIsNeverFoundInsideALongerWord(string title, string column, string cell)
    {
        var row = Run(LaptopRules(), title, (column, cell));

        Assert.Equal(TitleAttributeStatus.NotInTitle, Attr(row, column).Status);
        Assert.Equal(title, row.CleanTitle);
    }

    /// <summary>"8GB" is not in "128GB", however much of it is spelled the same way.</summary>
    [Fact]
    public void ASmallerQuantityIsNeverFoundInsideALargerOne()
    {
        var row = Run(LaptopRules(), "Acme Book 128GB SSD", ("RAM", "8GB"));

        var ram = Attr(row, "RAM");
        Assert.Equal(TitleAttributeStatus.Conflict, ram.Status);
        Assert.Contains("128GB", row.CleanTitle, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // Policy
    // -----------------------------------------------------------------

    /// <summary>
    /// Removal is a whitelist. An attribute the rule set does not mark for removal is still checked
    /// and still corrected — it just stays in the title.
    /// </summary>
    [Fact]
    public void AnAttributeNotMarkedForRemovalIsCheckedButKept()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ekran Kartı", Remove: false)]);

        var row = Run(rules, "Acme Book RTXPRO2000 SSD", ("Ekran Kartı", "RTXPRO2000"));

        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Ekran Kartı").Status);
        Assert.Equal("Acme Book RTXPRO2000 SSD", row.CleanTitle);
    }

    /// <summary>An empty cell is not an error, and nothing is written into it unless the rule says so.</summary>
    [Fact]
    public void AnEmptyCellIsLeftAloneByDefault()
    {
        var row = Run(LaptopRules(), "Acme Book 16GB SSD", ("RAM", ""));

        Assert.Equal(TitleAttributeStatus.Empty, Attr(row, "RAM").Status);
        Assert.Equal("Acme Book 16GB SSD", row.CleanTitle);
        Assert.False(row.HasConflict);
    }

    [Fact]
    public void AnEmptyCellIsFilledFromTheTitleOnlyWhenTheRuleAsksForIt()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("RAM", TitleAttributeKind.Measure, FillFromTitle: true, Units: [Gb])]);

        var row = Run(rules, "Acme Book 16GB", ("RAM", ""));

        var ram = Attr(row, "RAM");
        Assert.Equal(TitleAttributeStatus.Filled, ram.Status);
        Assert.Equal("16 GB", ram.Value);
        Assert.Equal("Acme Book", row.CleanTitle);
    }

    /// <summary>
    /// Filling refuses the repeated value for the same reason a filled cell does. "8GB 8GB" is a
    /// graphics card's own memory beside the system RAM; filling from it would write one and remove
    /// both, and the empty-cell path must not be more destructive than the filled one.
    /// </summary>
    [Fact]
    public void FillingRefusesAValueTheTitleCarriesTwice()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("RAM", TitleAttributeKind.Measure, FillFromTitle: true, Units: [Gb])]);

        var row = Run(rules, "Acme Book 8GB 8GB SSD", ("RAM", ""));

        Assert.Equal(TitleAttributeStatus.Empty, Attr(row, "RAM").Status);
        Assert.Equal("", Attr(row, "RAM").Value);
        Assert.Equal("Acme Book 8GB 8GB SSD", row.CleanTitle);
    }

    /// <summary>A value the title simply does not mention is not an error — plenty of true attributes
    /// are left out of a title on purpose.</summary>
    [Fact]
    public void AnAttributeTheTitleNeverMentionsIsNotAnError()
    {
        var row = Run(LaptopRules(), "Acme Book SSD", ("İşlemci", "Ultra 7 265H"));

        Assert.Equal(TitleAttributeStatus.NotInTitle, Attr(row, "İşlemci").Status);
        Assert.False(row.HasConflict);
        Assert.Empty(row.Errors);
    }

    /// <summary>Cutting a value out from between two separators must not leave the separators behind.</summary>
    [Fact]
    public void SeparatorsOrphanedByARemovalGoWithIt()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ekran Boyutu", TitleAttributeKind.Measure, Units: [Inch])]);

        var row = Run(rules, "Acme Book - 16\" - Notebook", ("Ekran Boyutu", "16\""));

        Assert.Equal("Acme Book Notebook", row.CleanTitle);
    }

    /// <summary>
    /// Running the cleaner over its own output changes nothing further. A second pass that kept
    /// eating characters is the failure mode that would corrupt a catalogue one re-run at a time,
    /// and it is the cheapest check that catches it.
    /// </summary>
    [Fact]
    public void CleaningAnAlreadyCleanTitleChangesNothing()
    {
        var first = ReferenceRow();

        var second = Run(
            LaptopRules(),
            first.CleanTitle,
            ("Marka", "Dell"),
            ("İşlemci", "Ultra 7 265H"),
            ("RAM", "32 GB"),
            ("Sabit Disk Kapasitesi", "1 TB"),
            ("Sabit Disk Tipi", "SSD"),
            ("Ekran Boyutu", "16\""),
            ("İşletim Sistemi", "W11P"),
            ("Ürün Tipi", "Dizüstü İş İstasyonu"),
            ("Çözünürlük", "FullHD+"));

        Assert.Equal(first.CleanTitle, second.CleanTitle);
        Assert.False(second.HasConflict);
    }

    // -----------------------------------------------------------------
    // Table driver
    // -----------------------------------------------------------------

    /// <summary>Picking the wrong rule set usually misses several columns, so all of them are named
    /// at once rather than costing the operator a round trip each.</summary>
    [Fact]
    public void EveryMissingColumnIsNamedRatherThanCleaningPartOfTheFile()
    {
        var rules = CompiledRuleSet.Compile(LaptopRules());
        List<List<string>> table = [["Başlık", "Marka"], ["Acme Book", "Acme"]];

        var error = Assert.Throws<InvalidOperationException>(() => TitleCleanBuilder.Clean(rules, table));

        Assert.Contains("Ürün Tipi", error.Message, StringComparison.Ordinal);
        Assert.Contains("İşlemci", error.Message, StringComparison.Ordinal);
        Assert.Contains("RAM", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'Marka'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A measured attribute with no unit is refused at compile time rather than silently
    /// removing bare numbers from every title in the file.</summary>
    [Fact]
    public void AMeasuredAttributeWithNoUnitIsRefused()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("RAM", TitleAttributeKind.Measure)]);

        var error = Assert.Throws<InvalidOperationException>(() => CompiledRuleSet.Compile(rules));
        Assert.Contains("RAM", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTableDriverNumbersRowsAsExcelDoes()
    {
        var rules = CompiledRuleSet.Compile(new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Marka")]));

        List<List<string>> table =
        [
            ["Başlık", "Marka"],
            ["Dell Notebook", "Dell"],
            ["", ""],
            ["Acme Notebook", "Acme"],
        ];

        var rows = TitleCleanBuilder.Clean(rules, table);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].RowNumber);
        Assert.Equal("Notebook", rows[0].CleanTitle);
        Assert.Equal(4, rows[1].RowNumber);
    }

    // -----------------------------------------------------------------

    static TitleCleanRow Run(TitleRuleSet set, string title, params (string Column, string Value)[] cells)
    {
        var map = cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.OrdinalIgnoreCase);

        return TitleCleanBuilder.CleanRow(
            CompiledRuleSet.Compile(set),
            2,
            title,
            name => map.GetValueOrDefault(name, ""));
    }

    static TitleAttributeResult Attr(TitleCleanRow row, string column) =>
        row.Attributes.First(a => string.Equals(a.Column, column, StringComparison.Ordinal));
}
