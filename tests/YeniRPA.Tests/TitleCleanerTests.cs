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

    /// <summary>
    /// A real export writes "512SSD": the capacity with no unit at all, glued onto the disk type. The
    /// number is readable as the cell's value only because the confirmed disk type continues straight
    /// from it — which is the same evidence "1TBSSD" always relied on, minus the unit.
    /// </summary>
    [Fact]
    public void ABareMeasureIsAcceptedOnlyWhenGluedToAnAcceptedValue()
    {
        var row = Run(
            LaptopRules(),
            "Lenovo ThinkPad E16 21ST0058TX003 512SSD 16\" WUXGA",
            ("Marka", "Lenovo"),
            ("Sabit Disk Kapasitesi", "512 GB"),
            ("Sabit Disk Tipi", "SSD"),
            ("Ekran Boyutu", "16\""));

        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Sabit Disk Kapasitesi").Status);
        Assert.Equal("ThinkPad E16 21ST0058TX003 WUXGA", row.CleanTitle);
    }

    /// <summary>
    /// The mirror case, and the one that makes the rule worth having. The screen really is 16 inches
    /// and the title really does carry a "16" — but it is the model name's, it has nothing glued to
    /// it, and taking it would write "Pro Max" back to the marketplace.
    /// </summary>
    [Fact]
    public void ABareMeasureStandingAloneIsStillRefused()
    {
        var row = Run(
            LaptopRules(),
            "Dell Pro Max 16 MC16250_3 Notebook",
            ("Marka", "Dell"),
            ("Ekran Boyutu", "16\""));

        Assert.Equal("Pro Max 16 MC16250_3 Notebook", row.CleanTitle);
    }

    /// <summary>A bare number inside a model code has a letter on its left that nothing accounts for,
    /// and is refused there too — on a row whose RAM really is 16 GB.</summary>
    [Fact]
    public void ABareMeasureInsideAModelCodeIsRefused()
    {
        var row = Run(
            LaptopRules(),
            "Dell Pro Max MC16250_3 Notebook",
            ("Marka", "Dell"),
            ("RAM", "16GB"));

        Assert.Equal("Pro Max MC16250_3 Notebook", row.CleanTitle);
    }

    // -----------------------------------------------------------------
    // A cell that holds more than one measurement
    // -----------------------------------------------------------------

    static TitleRuleSet Disks() => new("Test", "Başlık",
    [
        new TitleAttributeRule("Marka"),
        new TitleAttributeRule("Sabit Disk Kapasitesi", TitleAttributeKind.Measure, Units: [Gb, Tb]),
        new TitleAttributeRule("Sabit Disk Tipi", TitleAttributeKind.Alias, Aliases: [["SSD"]]),
    ]);

    /// <summary>
    /// A machine with two disks says so in one cell. Reading only the front of it made the second
    /// "1TBSSD" look like a repeat nobody had asked for, and the row went out uncleaned.
    /// </summary>
    [Fact]
    public void ACellHoldingTwoDisksRemovesBoth()
    {
        var row = Run(
            Disks(),
            "Lenovo ThinkPad P16 21RQ000JTX001 1TBSSD+1TBSSD WUXGA",
            ("Marka", "Lenovo"),
            ("Sabit Disk Kapasitesi", "1 TB + 1 TB"),
            ("Sabit Disk Tipi", "SSD"));

        Assert.Equal("ThinkPad P16 21RQ000JTX001 WUXGA", row.CleanTitle);
    }

    /// <summary>And two different ones, each answering its own half of the title.</summary>
    [Fact]
    public void ACellHoldingTwoDifferentDisksRemovesEach()
    {
        var row = Run(
            Disks(),
            "Lenovo ThinkPad P16 21RQ000JTX008 1TBSSD+2TBSSD WUXGA",
            ("Marka", "Lenovo"),
            ("Sabit Disk Kapasitesi", "2 TB + 1 TB"),
            ("Sabit Disk Tipi", "SSD"));

        Assert.Equal("ThinkPad P16 21RQ000JTX008 WUXGA", row.CleanTitle);
    }

    /// <summary>
    /// The guard this had every chance of breaking. A cell that says "8 GB" once asserts one 8 GB,
    /// and the second one in the title is a graphics card's own memory — still reported, still not
    /// removed. What changed is only that the number of occurrences a row is entitled to is read off
    /// the cell rather than assumed to be one.
    /// </summary>
    [Fact]
    public void ARepeatTheCellDoesNotAssertIsStillAmbiguous()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("RAM", TitleAttributeKind.Measure, Units: [Gb])]);

        var row = Run(rules, "Asus TUF RTX 5070 8GB 8GB FHD", ("RAM", "8 GB"));

        Assert.Equal(TitleAttributeStatus.Ambiguous, Attr(row, "RAM").Status);
        Assert.Equal("Asus TUF RTX 5070 8GB 8GB FHD", row.CleanTitle);
    }

    /// <summary>
    /// A multi-valued cell is never rewritten. One match has one canonical form, and writing it back
    /// over "1 TB + 1 TB" would throw the second disk away.
    /// </summary>
    [Fact]
    public void AMultiValueCellIsNeverRewritten()
    {
        var row = Run(
            Disks(),
            "Lenovo ThinkPad 1TBSSD+1TBSSD",
            ("Marka", "Lenovo"),
            ("Sabit Disk Kapasitesi", "1 TB + 1 TB"),
            ("Sabit Disk Tipi", "SSD"));

        var disk = Attr(row, "Sabit Disk Kapasitesi");

        Assert.Equal(TitleAttributeStatus.Ok, disk.Status);
        Assert.Equal("1 TB + 1 TB", disk.Value);
    }

    // -----------------------------------------------------------------
    // Values the title glues together
    // -----------------------------------------------------------------

    /// <summary>
    /// Which character separates a processor family from its model number is arbitrary, and one file
    /// writes it all three ways: the catalogue says "AMD Ryzen 7 7735HS" and "Intel Core i5-14450HX",
    /// the titles say "Ryzen7-7735HS" and "i5 14450HX".
    /// </summary>
    [Theory]
    [InlineData("Acer Predator Helios Ultra9-275HX 64GB", "Intel Core Ultra 9", "Intel Core Ultra 9 275HX")]
    [InlineData("HP OMEN 15 i5 14450HX 32GB", "Intel Core i5", "Intel Core i5-14450HX")]
    [InlineData("Lenovo LOQ Ryzen7-7735HS 16GB", "AMD Ryzen 7", "AMD Ryzen 7 7735HS")]
    public void AHyphenAndASpaceAreTheSameSeparator(string title, string cell, string entry)
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("İşlemci", AllowPartial: true, ReferenceList: "İşlemciler")]);

        var list = new TitleReferenceList("İşlemciler", "test", [entry]);

        var row = TitleCleanBuilder.CleanRow(
            CompiledRuleSet.Compile(rules, [list]), 2, title, _ => cell);

        Assert.DoesNotContain("HX", row.CleanTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("HS", row.CleanTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// A catalogue entry earns its place by carrying the words the cell does not. "Intel Core Ultra
    /// 5 225" is a real processor listed before "…225U", and it matched nothing but the "Ultra5" the
    /// cell alone would have found — then stopped the search, so the entry that would have taken the
    /// model code never got a turn.
    /// </summary>
    [Fact]
    public void AReferenceEntryThatAddsNothingDoesNotBlockTheOneThatDoes()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("İşlemci", AllowPartial: true, ReferenceList: "İşlemciler")]);

        // Catalogue order, shorter first — which is how the published list has them.
        var list = new TitleReferenceList("İşlemciler", "test",
            ["Intel Core Ultra 5 225", "Intel Core Ultra 5 225U"]);

        var row = TitleCleanBuilder.CleanRow(
            CompiledRuleSet.Compile(rules, [list]), 2,
            "HP ProBook D21PFET Ultra5 225U WUXGA", _ => "Intel Core Ultra 5");

        Assert.Equal("HP ProBook D21PFET WUXGA", row.CleanTitle);
    }

    // -----------------------------------------------------------------
    // A title that is not a product name
    // -----------------------------------------------------------------

    static TitleRuleSet Wide() => new("Test", "Başlık",
    [
        new TitleAttributeRule("Marka"),
        new TitleAttributeRule("Renk"),
        new TitleAttributeRule("İşletim Sistemi"),
        new TitleAttributeRule("Ürün Tipi"),
        new TitleAttributeRule("RAM", TitleAttributeKind.Measure, Units: [Gb]),
    ]);

    /// <summary>
    /// A real export puts marketing copy in the title column. The one thing such a line has in common
    /// with its row is the brand, so cleaning it faithfully cut the brand out and wrote the rest back
    /// to the marketplace.
    /// </summary>
    [Fact]
    public void ATitleThatMatchesOnlyOneOfManyFilledCellsIsLeftAlone()
    {
        const string title = "2 YIL LENOVO TÜRKİYE GARANTİLİ - ADINIZA FATURALI - HIZLI KARGO";

        var row = Run(Wide(), title,
            ("Marka", "LENOVO"), ("Renk", "Siyah"), ("İşletim Sistemi", "Windows 11 Pro"),
            ("Ürün Tipi", "Notebook"), ("RAM", "64 GB"));

        Assert.True(row.TitleSuspect);
        Assert.True(row.HasConflict);
        Assert.Equal(title, row.CleanTitle);
    }

    /// <summary>A title that answers its row properly is untouched by the guard.</summary>
    [Fact]
    public void AProperTitleIsNotSuspected()
    {
        var row = Run(Wide(), "LENOVO Siyah Notebook 64GB Windows 11 Pro",
            ("Marka", "LENOVO"), ("Renk", "Siyah"), ("İşletim Sistemi", "Windows 11 Pro"),
            ("Ürün Tipi", "Notebook"), ("RAM", "64 GB"));

        Assert.False(row.TitleSuspect);
        Assert.Equal("", row.CleanTitle);
    }

    /// <summary>
    /// And a rule set too small to draw the conclusion from. One column of two matching is an
    /// ordinary row, not a broken title.
    /// </summary>
    [Fact]
    public void ASmallRuleSetIsNotSuspectedOfANonTitle()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Marka"), new TitleAttributeRule("Renk")]);

        var row = Run(rules, "LENOVO Bir Şey", ("Marka", "LENOVO"), ("Renk", "Siyah"));

        Assert.False(row.TitleSuspect);
        Assert.Equal("Bir Şey", row.CleanTitle);
    }

    /// <summary>
    /// The marketplace writes "Ryzen™ 5" and "Core™ 5" in its processor column; the seller's titles
    /// write "Ryzen5 220" and "Core5 120U". Nothing separates the two but a space one side chose not
    /// to type, and a letter/digit change is a word boundary whether it is written or not.
    /// </summary>
    [Theory]
    [InlineData("Lenovo ThinkPad E16 Ryzen5 220 Notebook", "Ryzen™ 5", "ThinkPad E16 220 Notebook")]
    [InlineData("Asus Vivobook 15 Core5 120U Notebook", "Core™ 5", "Vivobook 15 120U Notebook")]
    [InlineData("Lenovo ThinkPad T16 Ultra7 255U Notebook", "Ultra 7", "ThinkPad T16 255U Notebook")]
    public void AGluedSpellingMatchesAtALetterDigitBoundary(string title, string cell, string expected)
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Marka"), new TitleAttributeRule("İşlemci")]);

        var brand = title.Split(' ')[0];
        var row = Run(rules, title, ("Marka", brand), ("İşlemci", cell));

        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "İşlemci").Status);
        Assert.Equal(expected, row.CleanTitle);
    }

    /// <summary>
    /// Two letters running together are two words the writer joined, not one word they split — so the
    /// tolerance stops at the letter/digit change and "Pro Max" never answers "ProMax". Without this
    /// the whole thing would be a prefix search wearing a different name.
    /// </summary>
    [Fact]
    public void AGluedSpellingIsRefusedBetweenTwoLetters()
    {
        var rules = new TitleRuleSet("Test", "Başlık", [new TitleAttributeRule("Seri")]);

        var row = Run(rules, "Dell ProMax 16 Notebook", ("Seri", "Pro Max"));

        Assert.Equal(TitleAttributeStatus.NotInTitle, Attr(row, "Seri").Status);
        Assert.Equal("Dell ProMax 16 Notebook", row.CleanTitle);
    }

    /// <summary>
    /// The same tolerance has to reach the partial path, or a cell reading "Intel Core Ultra 5" finds
    /// nothing in a title that plainly says "Ultra5" — the words are split differently on the two
    /// sides, which is exactly what the partial search is for.
    /// </summary>
    [Fact]
    public void APartialValueMatchesAGluedTitleSpelling()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("İşlemci", AllowPartial: true)]);

        var row = Run(rules, "Acer Aspire Lite Ultra5 125H Notebook", ("İşlemci", "Intel Core Ultra 5"));

        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "İşlemci").Status);
        Assert.Equal("Acer Aspire Lite 125H Notebook", row.CleanTitle);
    }

    // -----------------------------------------------------------------
    // Repeated words
    // -----------------------------------------------------------------

    static TitleRuleSet Repeats(bool on) => new(
        "Test", "Başlık", [new TitleAttributeRule("Marka")], ".", CollapseRepeats: on);

    /// <summary>
    /// A seller typed the series twice. This is the one thing the module removes without a column
    /// claiming it, so it happens only where the rule set asks for it.
    /// </summary>
    [Fact]
    public void ARepeatedWordIsCollapsedOnlyWhenTheSettingIsOn()
    {
        const string title = "Lenovo Ideapad Ideapad Slim3 82XQ0129TX002";

        Assert.Equal(
            "Ideapad Ideapad Slim3 82XQ0129TX002",
            Run(Repeats(false), title, ("Marka", "Lenovo")).CleanTitle);

        Assert.Equal(
            "Ideapad Slim3 82XQ0129TX002",
            Run(Repeats(true), title, ("Marka", "Lenovo")).CleanTitle);
    }

    /// <summary>
    /// The danger this rule has to survive. "RTX 5070 8GB 8GB" is a graphics card's own memory beside
    /// the system RAM on a row where the two are the same size — collapsing it deletes the card's
    /// memory, and only on some rows, which is the hardest kind of damage to notice.
    /// </summary>
    [Fact]
    public void ARepeatedMeasurementIsNeverCollapsed()
    {
        var row = Run(Repeats(true), "Asus TUF RTX 5070 8GB 8GB FHD", ("Marka", "Asus"));

        Assert.Equal("TUF RTX 5070 8GB 8GB FHD", row.CleanTitle);
    }

    /// <summary>
    /// Read off the original title, not the cleaned one. Cutting the middle out of "Ocak Siyah Ocak"
    /// leaves two words nobody wrote together, and collapsing those would delete a word the operator
    /// never accounted for.
    /// </summary>
    [Fact]
    public void CleaningNeverCreatesARepeatToCollapse()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Renk")], ".", CollapseRepeats: true);

        var row = Run(rules, "Ocak Siyah Ocak", ("Renk", "Siyah"));

        Assert.Equal("Ocak Ocak", row.CleanTitle);
    }

    // -----------------------------------------------------------------
    // Titles the marketplace cut short
    // -----------------------------------------------------------------

    /// <summary>
    /// A marketplace caps its title field, and a seller writing up to the cap loses the last word
    /// mid-letter — five of forty-eight rows in one real export end "Dizüstü Bi". Nothing else here
    /// can find those: every other step compares whole words.
    /// </summary>
    [Theory]
    [InlineData("Acer Aspire Lite FreeDOS Dizüstü Bi", "Aspire Lite FreeDOS")]
    [InlineData("Acer Aspire Lite FreeDOS Dizüstü Bil", "Aspire Lite FreeDOS")]
    [InlineData("Acer Aspire Lite FreeDOS Dizüstü Bilgisayar", "Aspire Lite FreeDOS")]
    public void ATruncatedTrailingValueIsTakenWithWhatIsLeftOfIt(string title, string expected)
    {
        var rules = new TitleRuleSet("Test", "Başlık",
        [
            new TitleAttributeRule("Marka"),
            new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias,
                Aliases: [["Notebook", "Dizüstü Bilgisayar"]]),
        ]);

        var row = Run(rules, title, ("Marka", "Acer"), ("Ürün Tipi", "Notebook"));

        Assert.Equal(expected, row.CleanTitle);
    }

    /// <summary>
    /// Only at the end, and only mid-word. A title that stops on a word boundary is saying less than
    /// the cell does, which is what the Kısmi permission is for — this must not answer it and take
    /// the decision away from the column's own setting.
    /// </summary>
    [Fact]
    public void ATitleEndingOnAWholeWordIsLeftToThePartialSetting()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
        [
            new TitleAttributeRule("Marka"),
            new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias,
                Aliases: [["Notebook", "Dizüstü Bilgisayar"]]),
        ]);

        var row = Run(rules, "Acer Aspire Lite Dizüstü", ("Marka", "Acer"), ("Ürün Tipi", "Notebook"));

        Assert.Equal("Aspire Lite Dizüstü", row.CleanTitle);
    }

    /// <summary>A cut-off word in the middle of a title is a word, not a truncation — nothing gets
    /// cut short except the end of the field.</summary>
    [Fact]
    public void AShortWordInTheMiddleOfATitleIsNotReadAsATruncation()
    {
        var rules = new TitleRuleSet("Test", "Başlık",
        [
            new TitleAttributeRule("Marka"),
            new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias,
                Aliases: [["Notebook", "Dizüstü Bilgisayar"]]),
        ]);

        var row = Run(rules, "Acer Dizüstü Bi Aspire Lite", ("Marka", "Acer"), ("Ürün Tipi", "Notebook"));

        Assert.Equal("Dizüstü Bi Aspire Lite", row.CleanTitle);
    }

    // -----------------------------------------------------------------
    // Reference lists
    // -----------------------------------------------------------------

    static readonly TitleReferenceList Processors = new("İşlemciler", "test",
    [
        "Intel Core Ultra 5 125H",
        "Intel Core Ultra 5 125HL",
        "AMD Ryzen 5 220",
        "AMD Ryzen 5 PRO 220",
        "Intel Core i5-13420H",
    ]);

    static TitleCleanRow RunWithCatalogue(
        TitleRuleSet set, string title, params (string Column, string Value)[] cells)
    {
        var map = cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.OrdinalIgnoreCase);

        return TitleCleanBuilder.CleanRow(
            CompiledRuleSet.Compile(set, [Processors]),
            2,
            title,
            name => map.GetValueOrDefault(name, ""));
    }

    static TitleRuleSet CpuRules() => new("Test", "Başlık",
    [
        new TitleAttributeRule("Marka"),
        new TitleAttributeRule("İşlemci", AllowPartial: true, ReferenceList: "İşlemciler"),
    ]);

    /// <summary>
    /// The case the whole feature exists for. The cell says "Intel Core Ultra 5" and the title says
    /// "Ultra5 125H"; no cell in the file carries the model code, so nothing but a catalogue can say
    /// that those five characters belong to the processor.
    /// </summary>
    [Fact]
    public void AReferenceEntryRemovesTheModelCodeNoCellCarries()
    {
        var row = RunWithCatalogue(
            CpuRules(),
            "Acer Aspire Lite Ultra5 125H Notebook",
            ("Marka", "Acer"),
            ("İşlemci", "Intel Core Ultra 5"));

        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "İşlemci").Status);
        Assert.Equal("Aspire Lite Notebook", row.CleanTitle);
    }

    /// <summary>
    /// Removal stays a whitelist. The catalogue holds five thousand processors; the only ones it may
    /// look for are the ones this row's own cell is part of, so a title naming a processor the cell
    /// disagrees with keeps it.
    /// </summary>
    [Fact]
    public void AReferenceEntryIsOnlyUsedWhenTheCellAgreesWithIt()
    {
        var row = RunWithCatalogue(
            CpuRules(),
            "Acer Aspire Lite Ultra5 125H Notebook",
            ("Marka", "Acer"),
            ("İşlemci", "AMD Ryzen 5"));

        Assert.Equal("Aspire Lite Ultra5 125H Notebook", row.CleanTitle);
    }

    /// <summary>
    /// "125HL" is a different processor from "125H", and both are consistent with the same cell. The
    /// entry is only the one the title actually writes — checked on the words it adds, before any
    /// search is run.
    /// </summary>
    [Fact]
    public void AReferenceEntryTheTitleDoesNotWriteIsNotUsed()
    {
        var row = RunWithCatalogue(
            CpuRules(),
            "Acer Aspire Lite Ultra5 125HX Notebook",
            ("Marka", "Acer"),
            ("İşlemci", "Intel Core Ultra 5"));

        Assert.Contains("125HX", row.CleanTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The regression this cost a whole file. "AMD Ryzen 5 PRO 220" is consistent with a cell reading
    /// "Ryzen 5" and has more words than "AMD Ryzen 5 220", so it is tried first — and because "PRO"
    /// is nowhere in the title, a partial search falls back to the one word both sides share: the bare
    /// "220". Every word an entry adds has to be a word the title writes, or the entry is not it.
    /// </summary>
    [Fact]
    public void AReferenceEntryWhoseExtraWordsAreMissingIsNotUsed()
    {
        var row = RunWithCatalogue(
            CpuRules(),
            "Lenovo ThinkPad E16 Ryzen5 220 Notebook",
            ("Marka", "Lenovo"),
            ("İşlemci", "Ryzen 5"));

        Assert.Equal("ThinkPad E16 Notebook", row.CleanTitle);
    }

    /// <summary>
    /// Intel's own catalogue joins the family and the model code with a hyphen — "Intel Core
    /// i5-13420H" — against a cell that reads "Intel Core i5". The cell's last word may therefore end
    /// part-way into the entry's, but only on a boundary.
    /// </summary>
    [Fact]
    public void AReferenceEntryMayContinueInsideTheCellsLastWord()
    {
        var row = RunWithCatalogue(
            CpuRules(),
            "Acer Nitro V15 i5-13420H Notebook",
            ("Marka", "Acer"),
            ("İşlemci", "Intel Core i5"));

        Assert.Equal("Nitro V15 Notebook", row.CleanTitle);
    }

    /// <summary>
    /// The limit on that. "Ryzen 5" must not reach into "Ryzen 50" — digit meeting digit is not a
    /// boundary, and treating it as one would make every catalogue entry a prefix search.
    /// </summary>
    [Fact]
    public void AReferenceEntryDoesNotContinueMidNumber()
    {
        var list = new TitleReferenceList("İşlemciler", "test", ["AMD Ryzen 50 900X"]);

        var row = TitleCleanBuilder.CleanRow(
            CompiledRuleSet.Compile(CpuRules(), [list]),
            2,
            "Lenovo ThinkPad Ryzen5 900X Notebook",
            name => name == "İşlemci" ? "Ryzen 5" : name == "Marka" ? "Lenovo" : "");

        Assert.Contains("900X", row.CleanTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The catalogue says what the title says, never what the cell ought to say. Rewriting an
    /// attribute column out of a reference file is a far larger claim than removing text from a
    /// title, and nothing here makes it.
    /// </summary>
    [Fact]
    public void AReferenceEntryNeverRewritesTheCell()
    {
        var row = RunWithCatalogue(
            CpuRules(),
            "Acer Aspire Lite Ultra5 125H Notebook",
            ("Marka", "Acer"),
            ("İşlemci", "Intel Core Ultra 5"));

        Assert.Equal("Intel Core Ultra 5", Attr(row, "İşlemci").Value);
    }

    /// <summary>A rule naming a list nobody loaded is refused while the operator is looking at it,
    /// rather than running on and leaving every title carrying what they asked to have removed.</summary>
    [Fact]
    public void ARuleNamingAnUnloadedReferenceListIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => CompiledRuleSet.Compile(CpuRules()));

        Assert.Contains("İşlemciler", error.Message, StringComparison.Ordinal);
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

        // Both sides quoted as each of them wrote it. The title spelled it "16GB", so that is what
        // the operator is shown — not the rule's canonical "16 GB", which is a form nobody typed.
        Assert.Contains("16GB", ram.Message);
        Assert.DoesNotContain("16 GB", ram.Message);
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

    /// <summary>
    /// A group's second spelling is what a catalogue is for: the title writes "İndüksiyon Ocak", the
    /// cell writes "İndüksiyonlu ocak", and one group makes them one value. The phrase leaves the
    /// title and the cell is rewritten to the head of the group.
    ///
    /// <para>Written down because it is the whole of the answer to a case the team hit, and because
    /// both halves have to hold together: a group that removed the phrase without correcting the cell
    /// would leave the catalogue disagreeing with a title it had just edited.</para>
    /// </summary>
    [Fact]
    public void ASecondSpellingInAGroupLeavesTheTitleAndPullsTheCellToTheCanonicalOne()
    {
        var set = new TitleRuleSet("Ocak", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias,
                Aliases: [["İndüksiyonlu Ocak", "İndüksiyon Ocak"]])]);

        var row = Run(
            set,
            "Teka IR 8430 5 Zone 80 cm Siyah İndüksiyon Ocak",
            ("Ürün Tipi", "İndüksiyonlu ocak"));

        Assert.Equal("Teka IR 8430 5 Zone 80 cm Siyah", row.CleanTitle);

        var attribute = Attr(row, "Ürün Tipi");
        Assert.Equal(TitleAttributeStatus.Corrected, attribute.Status);
        Assert.Equal("İndüksiyonlu Ocak", attribute.Value);
    }

    // -----------------------------------------------------------------
    // A value the title writes in pieces
    // -----------------------------------------------------------------

    const string SplitTitle = "GL General GLO 022SARS Rustik 60 cm Siyah Emaye Ankastre Ocak";

    static readonly MeasureUnit Cm = new("cm", ["cm"], 1);
    static readonly MeasureUnit Mm = new("mm", ["mm"], 0.1);

    /// <summary>
    /// The real case. "Rustik siyah" is one colour that the title splits in half with the width, and
    /// the width is itself a rule that removes what it finds — so the two words really are one value
    /// with another value inserted into it.
    /// </summary>
    [Fact]
    public void AValueTheTitleSplitsIsMatchedWhenSomebodyOwnsWhatSplitsIt()
    {
        var set = new TitleRuleSet("Ocak", "Başlık",
        [
            new TitleAttributeRule("Renk", TitleAttributeKind.Alias, Aliases: [["Rustik siyah"]]),
            new TitleAttributeRule("Genişlik", TitleAttributeKind.Measure, Units: [Cm, Mm]),
        ]);

        var row = Run(set, SplitTitle, ("Renk", "Rustik siyah"), ("Genişlik", "600 mm"));

        Assert.Equal("GL General GLO 022SARS Emaye Ankastre Ocak", row.CleanTitle);
        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Renk").Status);
    }

    /// <summary>
    /// The safety case, and the reason the rule above is allowed to exist at all. Take the width rule
    /// away and the same two words are just two words with unclaimed text between them — reading them
    /// as one colour would delete "Rustik" and "Siyah" out of a title nobody had accounted for.
    /// </summary>
    [Fact]
    public void TheSameTwoWordsDoNotMatchWhenNothingOwnsWhatIsBetweenThem()
    {
        var set = new TitleRuleSet("Ocak", "Başlık",
            [new TitleAttributeRule("Renk", TitleAttributeKind.Alias, Aliases: [["Rustik siyah"]])]);

        var row = Run(set, SplitTitle, ("Renk", "Rustik siyah"));

        Assert.Equal(SplitTitle, row.CleanTitle);
        Assert.Equal(TitleAttributeStatus.NotInTitle, Attr(row, "Renk").Status);
    }

    /// <summary>A rule that recognises the gap but may not cut it leaves that text in the title, and
    /// a value cannot be said to span text that stays.</summary>
    [Fact]
    public void AGapHeldByARuleThatMayNotRemoveDoesNotCount()
    {
        var set = new TitleRuleSet("Ocak", "Başlık",
        [
            new TitleAttributeRule("Renk", TitleAttributeKind.Alias, Aliases: [["Rustik siyah"]]),
            new TitleAttributeRule("Genişlik", TitleAttributeKind.Measure, Remove: false, Units: [Cm, Mm]),
        ]);

        var row = Run(set, SplitTitle, ("Renk", "Rustik siyah"), ("Genişlik", "600 mm"));

        Assert.Equal(TitleAttributeStatus.NotInTitle, Attr(row, "Renk").Status);
        Assert.Contains("Rustik 60 cm Siyah", row.CleanTitle, StringComparison.Ordinal);
    }

    /// <summary>Order is part of the value. Two words of a colour found back to front are not that
    /// colour.</summary>
    [Fact]
    public void TheWordsHaveToAppearInTheOrderTheValueWritesThem()
    {
        var set = new TitleRuleSet("Ocak", "Başlık",
        [
            new TitleAttributeRule("Renk", TitleAttributeKind.Alias, Aliases: [["Rustik siyah"]]),
            new TitleAttributeRule("Genişlik", TitleAttributeKind.Measure, Units: [Cm, Mm]),
        ]);

        var row = Run(set, "GL General Siyah 60 cm Rustik Emaye", ("Renk", "Rustik siyah"), ("Genişlik", "600 mm"));

        Assert.Equal(TitleAttributeStatus.NotInTitle, Attr(row, "Renk").Status);
    }

    /// <summary>A value the title writes as one stretch takes the ordinary path — the scattered
    /// search only runs where the contiguous one found nothing.</summary>
    [Fact]
    public void AContiguousValueIsStillMatchedTheOrdinaryWay()
    {
        var set = new TitleRuleSet("Ocak", "Başlık",
            [new TitleAttributeRule("Renk", TitleAttributeKind.Alias, Aliases: [["Rustik siyah"]])]);

        var row = Run(set, "GL General Rustik Siyah Emaye", ("Renk", "Rustik siyah"));

        Assert.Equal("GL General Emaye", row.CleanTitle);
        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Renk").Status);
    }

    // -----------------------------------------------------------------
    // Turkish inflections
    // -----------------------------------------------------------------

    const string PluralTitle = "Çetintaş Evii CSA VE 222 Seramik Siyah 2 Gözlü Elektrikli Ankastre Ocaklar";

    static TitleRuleSet Hobs(bool allowSuffix) => new("Ocak", "Başlık",
        [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias, AllowSuffix: allowSuffix,
            Aliases: [["Ankastre Ocak"]])]);

    /// <summary>
    /// "Ankastre Ocaklar" answers a cell reading "Ankastre ocak" once the column allows it, and the
    /// whole word goes — leaving "lar" behind is exactly what the boundary rule refuses to do, and
    /// this must not become a way of doing it.
    /// </summary>
    [Fact]
    public void AnInflectedWordIsMatchedWholeWhenTheColumnAllowsIt()
    {
        var row = Run(Hobs(allowSuffix: true), PluralTitle, ("Ürün Tipi", "Ankastre ocak"));

        Assert.Equal("Çetintaş Evii CSA VE 222 Seramik Siyah 2 Gözlü Elektrikli", row.CleanTitle);
        Assert.DoesNotContain("lar", row.CleanTitle, StringComparison.Ordinal);
        Assert.Equal(TitleAttributeStatus.Corrected, Attr(row, "Ürün Tipi").Status);
        Assert.Equal("Ankastre Ocak", Attr(row, "Ürün Tipi").Value);
    }

    /// <summary>Off by default, and off means the behaviour this module had before.</summary>
    [Fact]
    public void WithoutThatPermissionTheInflectedWordIsLeftAlone()
    {
        var row = Run(Hobs(allowSuffix: false), PluralTitle, ("Ürün Tipi", "Ankastre ocak"));

        Assert.Equal(PluralTitle, row.CleanTitle);
        Assert.Equal(TitleAttributeStatus.NotInTitle, Attr(row, "Ürün Tipi").Status);
    }

    /// <summary>
    /// The list is closed, and that is the whole of its safety. "Ocakçı" is a different word, not an
    /// inflection of "Ocak", and no permission may turn one into the other.
    /// </summary>
    [Fact]
    public void AWordEndingThatIsNotAnInflectionIsNotSwallowed()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Meslek", TitleAttributeKind.Alias, AllowSuffix: true,
                Aliases: [["Ocak"]])]);

        var row = Run(set, "Acme Ocakçı Seti", ("Meslek", "Ocak"));

        Assert.Equal("Acme Ocakçı Seti", row.CleanTitle);
        Assert.Equal(TitleAttributeStatus.NotInTitle, Attr(row, "Meslek").Status);
    }

    /// <summary>A model code is why this is opt-in. Even with the permission on, a code is not a word
    /// and its tail is not a suffix.</summary>
    [Fact]
    public void AModelCodeIsNotTreatedAsAnInflectedWord()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Seri", TitleAttributeKind.Alias, AllowSuffix: true,
                Aliases: [["GLO 022"]])]);

        var row = Run(set, "GL General GLO 022SARS Rustik", ("Seri", "GLO 022"));

        Assert.Contains("022SARS", row.CleanTitle, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // Part of a value standing for the whole of it
    // -----------------------------------------------------------------

    static TitleRuleSet Brand(bool allowPartial, params string[] group) => new("Test", "Başlık",
        [new TitleAttributeRule("Marka", TitleAttributeKind.Alias, AllowPartial: allowPartial,
            Aliases: [group])]);

    /// <summary>The real case: the catalogue carries the legal name, the title carries the one people
    /// use.</summary>
    [Fact]
    public void PartOfAValueAnswersForItWhenTheRestIsNowhereInTheTitle()
    {
        var row = Run(
            Brand(allowPartial: true, "CETINTAS EVII"),
            "Çetintaş 848 CT SLIM EI Siyah Cam Set Üstü Ocak",
            ("Marka", "CETINTAS EVII"));

        Assert.Equal("848 CT SLIM EI Siyah Cam Set Üstü Ocak", row.CleanTitle);
        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Marka").Status);
    }

    /// <summary>The same shape on a material column, where the title names the substance and the cell
    /// qualifies it.</summary>
    [Fact]
    public void APartialMatchWorksWhereverTheCellSaysMoreThanTheTitle()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Malzeme", TitleAttributeKind.Alias, AllowPartial: true,
                Aliases: [["Temperli Cam"]])]);

        var row = Run(set, "Acme 205CS Siyah Cam Ankastre Ocak", ("Malzeme", "Temperli Cam"));

        Assert.Equal("Acme 205CS Siyah Ankastre Ocak", row.CleanTitle);
    }

    /// <summary>Punctuation must not make a word look absent: "(vitroseramik)" is that word in
    /// brackets, and the title genuinely does not carry it.</summary>
    [Fact]
    public void ABracketedWordIsComparedAsTheWordItIs()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Yüzey", TitleAttributeKind.Alias, AllowPartial: true,
                Aliases: [["Elektrikli (vitroseramik)"]])]);

        var row = Run(set, "Acme CSA VE 222 2 Gözlü Elektrikli Ankastre", ("Yüzey", "Elektrikli (vitroseramik)"));

        Assert.Equal("Acme CSA VE 222 2 Gözlü Ankastre", row.CleanTitle);
    }

    /// <summary>
    /// The condition that makes this safe. The title says "Ocak", so the word the run leaves out is
    /// present — cutting "Ankastre" alone would take half a value out of a title that carries all of
    /// it, just not together.
    /// </summary>
    [Fact]
    public void PartOfAValueIsRefusedWhenTheRestIsInTheTitleAfterAll()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ürün Tipi", TitleAttributeKind.Alias, AllowPartial: true,
                Aliases: [["Ankastre Ocak"]])]);

        var row = Run(set, "Acme Ankastre Cam Ocak Seti", ("Ürün Tipi", "Ankastre Ocak"));

        Assert.Equal("Acme Ankastre Cam Ocak Seti", row.CleanTitle);
        Assert.Equal(TitleAttributeStatus.NotInTitle, Attr(row, "Ürün Tipi").Status);
    }

    /// <summary>
    /// Off by default, and this is the case that says why. A catalogue holding "Windows 11 Pro" must
    /// not read the word "Pro" in a laptop's model name as an operating system.
    /// </summary>
    [Fact]
    public void WithoutThatPermissionAWordOfAValueMeansNothingOnItsOwn()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("İşletim Sistemi", TitleAttributeKind.Alias,
                Aliases: [["W11P", "Windows 11 Pro"]])]);

        var row = Run(set, "Dell Pro Max 16 MC16250_3", ("İşletim Sistemi", "W11P"));

        Assert.Equal("Dell Pro Max 16 MC16250_3", row.CleanTitle);
    }

    /// <summary>A word too short to carry an identity cannot stand for a value by itself.</summary>
    [Fact]
    public void ATwoLetterWordCannotStandForAValue()
    {
        var row = Run(
            Brand(allowPartial: true, "EI Teknoloji"),
            "Acme 848 CT SLIM EI Siyah",
            ("Marka", "EI Teknoloji"));

        Assert.Equal("Acme 848 CT SLIM EI Siyah", row.CleanTitle);
    }

    /// <summary>A single-word value either is in the title or is not; there is no part of it.</summary>
    [Fact]
    public void ASingleWordValueIsNeverMatchedInPart()
    {
        var row = Run(Brand(allowPartial: true, "Çetintaş"), "Acme Çetin Siyah", ("Marka", "Çetintaş"));

        Assert.Equal("Acme Çetin Siyah", row.CleanTitle);
    }

    /// <summary>Where one spelling is written out in full, the others are not taken apart looking for
    /// a second opinion.</summary>
    [Fact]
    public void AFullMatchOnOneSpellingStopsThePartialSearchEntirely()
    {
        var row = Run(
            Brand(allowPartial: true, "Çetintaş", "CETINTAS EVII"),
            "Çetintaş Evii 848 CT SLIM",
            ("Marka", "Çetintaş"));

        // "Çetintaş Evii" is the longer spelling and is present in full, so it goes whole.
        Assert.Equal("848 CT SLIM", row.CleanTitle);
    }

    // -----------------------------------------------------------------
    // A title that rounds
    // -----------------------------------------------------------------

    static readonly MeasureUnit CmR = new("cm", ["cm"], 1);
    static readonly MeasureUnit MmR = new("mm", ["mm"], 0.1);

    /// <summary>
    /// 745 mm of width is written "75 cm" in the title. Refusing that is a false conflict — and an
    /// expensive one, because the span then belongs to nobody.
    /// </summary>
    [Fact]
    public void ATitleMayWriteAMeasurementLessPreciselyThanTheCell()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Genişlik", TitleAttributeKind.Measure, Units: [CmR, MmR])]);

        var row = Run(set, "Hoover HVG7PB/TK 5 Gözlü 75 cm Döküm", ("Genişlik", "745 mm"));

        Assert.Equal("Hoover HVG7PB/TK 5 Gözlü Döküm", row.CleanTitle);
        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Genişlik").Status);

        // The cell is the precise one. Rewriting it as the title's "75 cm" would delete a figure.
        Assert.Equal("745 mm", Attr(row, "Genişlik").Value);
    }

    /// <summary>Less precise, never differently precise. The title wrote a decimal, and it is a
    /// different one.</summary>
    [Fact]
    public void ATitleThatWritesADifferentDecimalIsStillAConflict()
    {
        var set = new TitleRuleSet("Test", "Başlık",
            [new TitleAttributeRule("Ekran", TitleAttributeKind.Measure,
                Units: [new MeasureUnit("cm", ["cm"], 1)])]);

        var row = Run(set, "Acme Notebook 15,7 cm Siyah", ("Ekran", "15,6 cm"));

        Assert.Equal(TitleAttributeStatus.Conflict, Attr(row, "Ekran").Status);
    }

    /// <summary>
    /// The whole of row 8. One unmatched width made three columns disagree about the same "75 cm";
    /// once the width owns it, the other two fall silent.
    /// </summary>
    [Fact]
    public void OnceOneColumnOwnsTheMeasurementTheOthersStopDisagreeingAboutIt()
    {
        var set = new TitleRuleSet("Test", "Başlık",
        [
            new TitleAttributeRule("Genişlik", TitleAttributeKind.Measure, Units: [CmR, MmR]),
            new TitleAttributeRule("Derinlik", TitleAttributeKind.Measure, Units: [CmR, MmR]),
            new TitleAttributeRule("Yükseklik", TitleAttributeKind.Measure, Units: [CmR, MmR]),
        ]);

        var row = Run(
            set,
            "Hoover HVG7PB/TK 5 Gözlü 75 cm Döküm Izgara Siyah",
            ("Genişlik", "745 mm"), ("Derinlik", "510 mm"), ("Yükseklik", "45 mm"));

        Assert.Equal(TitleAttributeStatus.Ok, Attr(row, "Genişlik").Status);
        Assert.Equal(TitleAttributeStatus.NotInTitle, Attr(row, "Derinlik").Status);
        Assert.Equal(TitleAttributeStatus.NotInTitle, Attr(row, "Yükseklik").Status);
        Assert.False(row.HasConflict);
    }

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

    // -----------------------------------------------------------------
    // Two sizes the operator has declared equal
    // -----------------------------------------------------------------
    //
    // A marketplace's screen-size attribute is a list of whole inches, so a 15.6" panel is filed
    // under 16 and the title and the cell disagree on every row. Nothing in the file says which of
    // them is right — the operator is asked, and their answer is the value-list line these exercise.

    static TitleRuleSet ScreenRules(params string[] pairs) => new(
        "Laptop",
        "Başlık",
        [
            new TitleAttributeRule("Ekran Boyutu", TitleAttributeKind.Measure, Units: [Inch],
                Aliases: pairs.Select(p => (IReadOnlyList<string>)p.Split('|')).ToList()),
        ]);

    /// <summary>The operator picked the title's reading: the title loses it and the cell is pulled
    /// onto the size that is actually true of the product.</summary>
    [Fact]
    public void ADeclaredPairLetsTheTitleWinAndRewritesTheCell()
    {
        var row = Run(
            ScreenRules("15.6\"|16\""),
            "Acer Aspire Lite AL16-51P NX.DCLEY 15.6\" FullHD",
            ("Ekran Boyutu", "16 inç"));

        var screen = Attr(row, "Ekran Boyutu");

        Assert.Equal(TitleAttributeStatus.Corrected, screen.Status);
        Assert.Equal("15.6\"", screen.Value);
        Assert.Equal("Acer Aspire Lite AL16-51P NX.DCLEY FullHD", row.CleanTitle);
        Assert.False(row.HasConflict);
    }

    /// <summary>The other answer. The title still loses the size — both readings name one screen —
    /// but the cell keeps what the marketplace filed it under.</summary>
    [Fact]
    public void ADeclaredPairCanKeepTheCellsReadingInstead()
    {
        var row = Run(
            ScreenRules("16\"|15.6\""),
            "Acer Aspire Lite AL16-51P NX.DCLEY 15.6\" FullHD",
            ("Ekran Boyutu", "16 inç"));

        var screen = Attr(row, "Ekran Boyutu");

        Assert.Equal("16\"", screen.Value);
        Assert.Equal("Acer Aspire Lite AL16-51P NX.DCLEY FullHD", row.CleanTitle);
        Assert.False(row.HasConflict);
    }

    /// <summary>
    /// Without a declaration the row is reported exactly as it was before. This is the guard that
    /// matters most: the feature exists because the engine must <em>not</em> decide which side is
    /// right on its own, and a pair that silently generalised would delete real data errors.
    /// </summary>
    [Fact]
    public void AnUndeclaredSizeDifferenceIsStillAConflict()
    {
        var row = Run(
            ScreenRules(),
            "Acer Aspire Lite AL16-51P NX.DCLEY 15.6\" FullHD",
            ("Ekran Boyutu", "16 inç"));

        Assert.Equal(TitleAttributeStatus.Conflict, Attr(row, "Ekran Boyutu").Status);
        Assert.Contains("15.6\"", row.CleanTitle, StringComparison.Ordinal);
    }

    /// <summary>A pair says nothing about any other size. 17.3" against a cell of 17 is a different
    /// question, and it keeps its place in the review list until it is asked.</summary>
    [Fact]
    public void ADeclaredPairDoesNotCoverASizeItDoesNotName()
    {
        var row = Run(
            ScreenRules("15.6\"|16\""),
            "Hp Omen C2EZ2EA003 17.3\" FHD",
            ("Ekran Boyutu", "17 inç"));

        Assert.Equal(TitleAttributeStatus.Conflict, Attr(row, "Ekran Boyutu").Status);
    }

    /// <summary>
    /// A value line the column's own units cannot read is refused at compile time. It was typed to
    /// make a row come out a certain way; dropping it quietly leaves the operator staring at an
    /// unchanged row with nothing to explain it.
    /// </summary>
    [Fact]
    public void AMeasuredValueLineThatIsNotAMeasurementIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => CompiledRuleSet.Compile(ScreenRules("16\"|1 TB")));

        Assert.Contains("Ekran Boyutu", error.Message, StringComparison.Ordinal);
        Assert.Contains("1 TB", error.Message, StringComparison.Ordinal);
    }
}
