using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// The settings that let a rule set <em>fill</em> empty cells and clean words no cell holds:
/// "~" spellings (found inside a word), Boşsa Değer, Koşul Kolonu, Kolondan Al, Yalnızca Sil and the
/// rule-set level Büyük Harf. Every one of them is off unless a rule asks, and the ordinary rules are
/// pinned by <see cref="TitleCleanerTests"/>.
/// </summary>
public class TitleRuleFillTests
{
    static TitleCleanRow Run(TitleRuleSet set, string title, params (string Column, string Value)[] cells)
    {
        var map = cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.OrdinalIgnoreCase);

        return TitleCleanBuilder.CleanRow(
            CompiledRuleSet.Compile(set), 2, title, name => map.GetValueOrDefault(name, ""));
    }

    static TitleAttributeResult Attr(TitleCleanRow row, string column) =>
        row.Attributes.First(a => a.Column == column);

    static TitleAttributeRule Chipset(bool remove = true) => new(
        "Chipset", TitleAttributeKind.Alias, Remove: remove, FillFromTitle: true,
        Aliases: [["AMD B850", "~B850"], ["Intel® B760", "~B760"]]);

    // -----------------------------------------------------------------
    // "~" spellings
    // -----------------------------------------------------------------

    [Fact]
    public void ATildeSpellingFillsFromInsideAWordAndRemovesNothing()
    {
        var row = Run(new("x", "Başlık", [Chipset()]), "ASUS PRIME B850M-K WIFI");

        Assert.Equal(TitleAttributeStatus.Filled, Attr(row, "Chipset").Status);
        Assert.Equal("AMD B850", Attr(row, "Chipset").Value);
        Assert.Equal("ASUS PRIME B850M-K WIFI", row.CleanTitle);
    }

    [Fact]
    public void ATildeSpellingOnlyMatchesFromTheStartOfAWord()
    {
        var row = Run(new("x", "Başlık", [Chipset()]), "ASUS PRIME XB850 WIFI");

        Assert.Equal(TitleAttributeStatus.Empty, Attr(row, "Chipset").Status);
    }

    [Fact]
    public void TheLongestInsideReadingWins()
    {
        var rule = new TitleAttributeRule(
            "Form", TitleAttributeKind.Alias, FillFromTitle: true,
            Aliases: [["mATX", "~B850M"], ["ATX", "~B850"]]);

        Assert.Equal("mATX", Attr(Run(new("x", "Başlık", [rule]), "PRIME B850M-K"), "Form").Value);
        Assert.Equal("ATX", Attr(Run(new("x", "Başlık", [rule]), "PRIME B850-PLUS"), "Form").Value);
    }

    [Fact]
    public void TwoDifferentValuesOfTheSameLengthFillNothing()
    {
        var rule = new TitleAttributeRule(
            "Form", TitleAttributeKind.Alias, FillFromTitle: true,
            Aliases: [["mATX", "~B850M"], ["ATX", "~A850M"]]);

        Assert.Equal(TitleAttributeStatus.Empty, Attr(Run(new("x", "Başlık", [rule]), "B850M A850M"), "Form").Status);
    }

    /// <summary>The model code outranks a bare word of the title: B760M is a micro-ATX board whatever
    /// the seller wrote after it. The word is still cut from the title.</summary>
    [Fact]
    public void TheModelCodeOutranksAWordTheTitleDisagreesWith()
    {
        var rule = new TitleAttributeRule(
            "Form", TitleAttributeKind.Alias, FillFromTitle: true,
            Aliases: [["mATX", "MATX", "~B760M"], ["ATX"]]);

        var row = Run(new("x", "Başlık", [rule]), "PRO B760M-P DDR4 ATX 1700P");

        Assert.Equal("mATX", Attr(row, "Form").Value);
        Assert.Equal("PRO B760M-P DDR4 1700P", row.CleanTitle);
    }

    // -----------------------------------------------------------------
    // A value written twice
    // -----------------------------------------------------------------

    [Fact]
    public void ASpellingOfTheSameValueWrittenTwiceStillFillsAnEmptyCell()
    {
        var rule = new TitleAttributeRule(
            "Soket", TitleAttributeKind.Alias, FillFromTitle: true, Aliases: [["AM5"]]);

        var row = Run(new("x", "Başlık", [rule]), "PRIME A620M AM5 DDR5 (AMD AM5 SERİLERİ)");

        Assert.Equal("AM5", Attr(row, "Soket").Value);
        Assert.DoesNotContain("AM5", row.CleanTitle, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // Kolondan Al, Boşsa Değer, Koşul Kolonu
    // -----------------------------------------------------------------

    static TitleRuleSet Board() => new(
        "x", "Başlık",
        [
            Chipset(remove: false),
            new TitleAttributeRule("Marka", TitleAttributeKind.Alias, FillFromTitle: true,
                FillWhen: "Chipset", Aliases: [["ASUS"]]),
            new TitleAttributeRule("Üretici", TitleAttributeKind.Alias, FillFromTitle: true,
                FillWhen: "Chipset", CopyFrom: "Marka"),
            new TitleAttributeRule("Tip", TitleAttributeKind.Alias, FillFromTitle: true,
                FillWhen: "Chipset", FillDefault: "Mainboard", Aliases: [["Mainboard", "ANAKART"]]),
        ]);

    [Fact]
    public void AColumnCopiesAnotherColumnsValueAndTheDefaultFillsWhatIsLeft()
    {
        var row = Run(Board(), "ASUS PRIME B850M-K ANAKART");

        Assert.Equal("ASUS", Attr(row, "Marka").Value);
        Assert.Equal("ASUS", Attr(row, "Üretici").Value);
        Assert.Equal("Mainboard", Attr(row, "Tip").Value);
        Assert.Equal("PRIME B850M-K", row.CleanTitle);

        var bare = Run(Board(), "PRIME B850M-K");
        Assert.Equal("Mainboard", Attr(bare, "Tip").Value);
        Assert.Equal(TitleAttributeStatus.Empty, Attr(bare, "Üretici").Status);
    }

    /// <summary>A row whose condition column stays empty is not this category's product: nothing is
    /// filled and nothing is cut — the words a rule had already removed come back.</summary>
    [Fact]
    public void ARowWhoseConditionColumnIsEmptyIsLeftAlone()
    {
        var row = Run(Board(), "ASUS Kingston Beast 32G ANAKART");

        Assert.All(row.Attributes, a => Assert.Equal(TitleAttributeStatus.Empty, a.Status));
        Assert.Equal("ASUS Kingston Beast 32G ANAKART", row.CleanTitle);
    }

    [Fact]
    public void ADefaultNeverOverwritesACell()
    {
        var row = Run(Board(), "PRIME B850M-K", ("Tip", "Gaming Mainboard"));

        Assert.NotEqual(TitleAttributeStatus.Filled, Attr(row, "Tip").Status);
        Assert.Equal("Gaming Mainboard", Attr(row, "Tip").Value);
    }

    [Fact]
    public void ACopyHappensInRuleOrderSoItCanCopyAColumnThatTookItsDefault()
    {
        var set = new TitleRuleSet("x", "Başlık",
        [
            new TitleAttributeRule("Renk", TitleAttributeKind.Alias, FillFromTitle: true, FillDefault: "Siyah",
                Aliases: [["Beyaz", "BEYAZ"]]),
            new TitleAttributeRule("Renk2", TitleAttributeKind.Alias, FillFromTitle: true, CopyFrom: "Renk"),
        ]);

        Assert.Equal("Siyah", Attr(Run(set, "PRIME"), "Renk2").Value);
        Assert.Equal("Beyaz", Attr(Run(set, "PRIME BEYAZ"), "Renk2").Value);
    }

    // -----------------------------------------------------------------
    // Yalnızca Sil
    // -----------------------------------------------------------------

    [Fact]
    public void ARemoveOnlyColumnCutsWhatItsListFindsAndNeverWritesACell()
    {
        var set = new TitleRuleSet("x", "Başlık",
        [
            new TitleAttributeRule("Portlar", TitleAttributeKind.Alias, RemoveOnly: true,
                Aliases: [["HDMI"], ["1XDP"]]),
        ]);

        var row = Run(set, "PRIME B850M-K HDMI 1XDP WIFI");

        Assert.Equal("PRIME B850M-K WIFI", row.CleanTitle);
        Assert.Equal(TitleAttributeStatus.Empty, Attr(row, "Portlar").Status);
        Assert.Equal("", Attr(row, "Portlar").Value);
    }

    [Fact]
    public void ARemoveOnlyMeasureCutsANumberWithItsUnit()
    {
        var set = new TitleRuleSet("x", "Başlık",
        [
            new TitleAttributeRule("Hız", TitleAttributeKind.Measure, RemoveOnly: true,
                Units: [new MeasureUnit("MHz", ["mhz", "(oc)", "mhz (oc)"], 1)]),
        ]);

        // The 650 inside B650M is not a measurement — it has a letter hard against it.
        Assert.Equal("B650M-K DDR5", Run(set, "B650M-K DDR5 8000MHZ").CleanTitle);
        Assert.Equal("B650M-K DDR5", Run(set, "B650M-K DDR5 6800(OC)").CleanTitle);
    }

    // -----------------------------------------------------------------
    // Büyük Harf, and the separators a seller typed
    // -----------------------------------------------------------------

    [Fact]
    public void UpperCaseWritesTheCleanedTitleInCapitalsOnlyWhereARuleActed()
    {
        var rule = new TitleAttributeRule(
            "Marka", TitleAttributeKind.Alias, FillFromTitle: true, Aliases: [["ASUS"]]);
        var set = new TitleRuleSet("x", "Başlık", [rule], UpperCase: true);

        Assert.Equal("PRIME H810M-E CSM", Run(set, "Asus Prime H810M-E Csm").CleanTitle);
        Assert.Equal("Kingston Beast", Run(set, "Kingston Beast").CleanTitle);
    }

    [Fact]
    public void ASeparatorBesideNoCutSurvivesAndOneBesideACutGoes()
    {
        var set = new TitleRuleSet("x", "Başlık",
        [
            new TitleAttributeRule("Marka", TitleAttributeKind.Alias, FillFromTitle: true, Aliases: [["ASUS"]]),
        ]);

        Assert.Equal("PRIME + WIFI", Run(set, "ASUS PRIME + WIFI").CleanTitle);
        Assert.Equal("PRIME", Run(set, "ASUS - PRIME").CleanTitle);
    }

    // -----------------------------------------------------------------
    // Başlığa Ekle, a "~" standard value, and a gate two columns hold for each other
    // -----------------------------------------------------------------

    static TitleRuleSet Standard() => new(
        "x", "Başlık",
        [
            new TitleAttributeRule("Chipset", TitleAttributeKind.Alias, FillFromTitle: true, FillWhen: "Marka",
                Aliases: [["~B850", "AMD B850", "b850 chipset"], ["~B550", "AMD B550"]]),
            new TitleAttributeRule("Marka", TitleAttributeKind.Alias, Remove: false, FillFromTitle: true,
                FillWhen: "Chipset", Aliases: [["MSI"], ["ASUS"]]),
            new TitleAttributeRule("Tip", TitleAttributeKind.Alias, FillFromTitle: true, FillWhen: "Marka",
                AppendToTitle: "ANAKART", Aliases: [["Mainboard", "ANAKART", "MB"]]),
        ],
        UpperCase: true);

    [Fact]
    public void TheProductTypeIsWrittenOnceAtTheEndWhetherOrNotTheSellerWroteIt()
    {
        Assert.Equal("MSI PRIME B850M-K ANAKART", Run(Standard(), "MSI Prime B850M-K").CleanTitle);
        Assert.Equal("MSI PRIME B850M-K ANAKART", Run(Standard(), "MSI Prime B850M-K MB Anakart").CleanTitle);
        Assert.Equal("MSI PRIME B850M-K ANAKART", Run(Standard(), "MSI Prime ANAKART B850M-K").CleanTitle);
    }

    [Fact]
    public void ABrandKeepsItsPlaceAndAChipsetWrittenOutBesideItsMakerGoes()
    {
        var row = Run(Standard(), "MSI B850M-K AMD B850 DDR5");

        Assert.Equal("MSI B850M-K DDR5 ANAKART", row.CleanTitle);
        Assert.Equal("B850", Attr(row, "Chipset").Value);
        Assert.Equal("MSI", Attr(row, "Marka").Value);
    }

    /// <summary>A model that begins with its chipset keeps it — also on the second pass, when the cell
    /// already holds "B550" and would otherwise be searched for as a whole word.</summary>
    [Fact]
    public void ACellHoldingTheChipsetNeverCutsTheModelNameThatBeginsWithIt()
    {
        var row = Run(Standard(), "MSI B550 GAMING WIFI6 ANAKART", ("Chipset", "B550"), ("Marka", "MSI"), ("Tip", "Mainboard"));

        Assert.Equal("MSI B550 GAMING WIFI6 ANAKART", row.CleanTitle);
    }

    /// <summary>A known brand and a known chipset each vouch for the other: with only one of them the
    /// row is not this category's product and is left as it came.</summary>
    [Theory]
    [InlineData("Kingston Beast B850 DDR5")]       // a chipset, no brand
    [InlineData("MSI Beast 32G DDR5")]             // a brand, no chipset
    public void ARowMissingEitherTheBrandOrTheChipsetIsLeftAlone(string title)
    {
        var row = Run(Standard(), title);

        Assert.Equal(title, row.CleanTitle);
        Assert.All(row.Attributes, a => Assert.Equal(TitleAttributeStatus.Empty, a.Status));
    }

    [Fact]
    public void ASecondPassOverACleanedTitleChangesNothing()
    {
        var first = Run(Standard(), "asus prime b850m-k ddr5 mb");
        var second = Run(
            Standard(), first.CleanTitle,
            ("Chipset", Attr(first, "Chipset").Value), ("Marka", Attr(first, "Marka").Value), ("Tip", Attr(first, "Tip").Value));

        Assert.Equal("ASUS PRIME B850M-K DDR5 ANAKART", first.CleanTitle);
        Assert.Equal(first.CleanTitle, second.CleanTitle);
    }

    // -----------------------------------------------------------------
    // Persistence
    // -----------------------------------------------------------------

    [Fact]
    public void EveryFillSettingSurvivesTheExcelRoundTripAndTheEditorForm()
    {
        var set = Board() with { UpperCase = true };
        set = set with
        {
            Attributes = set.AttributeList
                .Append(new TitleAttributeRule("Sil", TitleAttributeKind.Alias, RemoveOnly: true, AppendToTitle: "ANAKART", Aliases: [["HDMI"]]))
                .ToList(),
        };

        using var stream = new MemoryStream(TitleRuleStore.BuildWorkbook([set]));
        var read = Assert.Single(TitleRuleStore.ReadWorkbook(stream, "kural.xlsx"));

        Assert.True(read.UpperCase);
        Assert.Equal(TitleRuleStore.ToForm(set).AttributeList, TitleRuleStore.ToForm(read).AttributeList);

        var viaForm = TitleRuleStore.FromForm(TitleRuleStore.ToForm(set));
        Assert.Equal("Mainboard", viaForm.AttributeList.Single(a => a.Column == "Tip").FillDefault);
        Assert.Equal("Chipset", viaForm.AttributeList.Single(a => a.Column == "Tip").FillWhen);
        Assert.Equal("Marka", viaForm.AttributeList.Single(a => a.Column == "Üretici").CopyFrom);
        Assert.True(viaForm.AttributeList.Single(a => a.Column == "Sil").RemoveOnly);
        Assert.Equal("ANAKART", viaForm.AttributeList.Single(a => a.Column == "Sil").AppendToTitle);
        Assert.Equal("ANAKART", read.AttributeList.Single(a => a.Column == "Sil").AppendToTitle);
        Assert.True(viaForm.UpperCase);
    }
}
