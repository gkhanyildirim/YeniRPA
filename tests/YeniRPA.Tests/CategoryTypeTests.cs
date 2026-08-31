using ClosedXML.Excel;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// The marketplace's RuleSet, read as a product-type catalogue.
///
/// <para>The workbook is published by somebody else and changes without notice, so what this class
/// pins down is the shape the reader depends on — a "Ürün Tipi = A OR B" line among conditions that
/// say nothing about titles, and two category columns the sheet gives the same heading.</para>
/// </summary>
public class CategoryTypeTests
{
    // -----------------------------------------------------------------
    // Reading the workbook
    // -----------------------------------------------------------------

    /// <summary>Builds a sheet shaped like the published one, down to the duplicated header.</summary>
    static MemoryStream Workbook(params (string Conditions, string Code, string Label)[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Link Rules");

        sheet.Cell(1, 1).Value = "ID";
        sheet.Cell(1, 2).Value = "Path";
        sheet.Cell(1, 3).Value = "Rule ID";
        sheet.Cell(1, 4).Value = "Type";
        sheet.Cell(1, 5).Value = "Conditions";
        sheet.Cell(1, 6).Value = "Mirakl Category";
        sheet.Cell(1, 7).Value = "Mirakl Category";

        for (var i = 0; i < rows.Length; i++)
        {
            sheet.Cell(i + 2, 5).Value = rows[i].Conditions;
            sheet.Cell(i + 2, 6).Value = rows[i].Code;
            sheet.Cell(i + 2, 7).Value = rows[i].Label;
        }

        var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>
    /// The two category columns share a heading — code on the left, Turkish label on the right — so
    /// they are read by position. A header-name lookup finds one and silently loses the other.
    /// </summary>
    [Fact]
    public void TheTwoCategoryColumnsAreToldApartByPosition()
    {
        using var stream = Workbook(("Ürün Tipi = Ankastre Ocak", "HOBS", "OCAKLAR"));

        var rule = Assert.Single(CategoryRuleStore.ReadWorkbook(stream, "RuleSet.xlsx"));

        Assert.Equal("HOBS", rule.Category);
        Assert.Equal("OCAKLAR", rule.CategoryTr);
        Assert.Equal(["Ankastre Ocak"], rule.Types);
    }

    /// <summary>The spellings are the whole point: one type, every way the marketplace accepts it
    /// written, with its own spelling first.</summary>
    [Fact]
    public void TheOrListBecomesTheGroupWithTheFirstSpellingCanonical()
    {
        using var stream = Workbook(
            ("Ürün Tipi = Elektrikli Ocak OR elektrikli ocak OR elektrikli ankastre ocak", "HOBS", "OCAKLAR"));

        var rule = Assert.Single(CategoryRuleStore.ReadWorkbook(stream, "RuleSet.xlsx"));

        Assert.Equal(["Elektrikli Ocak", "elektrikli ocak", "elektrikli ankastre ocak"], rule.Types);
    }

    /// <summary>A condition cell carries several conditions. Only the product-type line says anything
    /// about what a title contains; the rest constrain the rule in other ways entirely.</summary>
    [Fact]
    public void ConditionsOtherThanTheProductTypeAreIgnored()
    {
        using var stream = Workbook((
            "Required Feature Frame (ID) = FET_FRA_1090\nMarketplace Brand Name = TEKA OR BOSCH\n" +
            "Ürün Tipi = Ankastre Ocak",
            "HOBS", "OCAKLAR"));

        var rule = Assert.Single(CategoryRuleStore.ReadWorkbook(stream, "RuleSet.xlsx"));
        Assert.Equal(["Ankastre Ocak"], rule.Types);
    }

    /// <summary>A rule naming no product type is not a rule this module can act on.</summary>
    [Fact]
    public void ARuleWithNoProductTypeIsSkipped()
    {
        using var stream = Workbook(
            ("Marketplace Brand Name = TEKA", "HOBS", "OCAKLAR"),
            ("Ürün Tipi = Ankastre Ocak", "HOBS", "OCAKLAR"));

        Assert.Single(CategoryRuleStore.ReadWorkbook(stream, "RuleSet.xlsx"));
    }

    /// <summary>"#N/A" is what the sheet leaves where a category has no Turkish label. Shown as-is it
    /// would reach the operator on a card.</summary>
    [Fact]
    public void AMissingTurkishLabelFallsBackToTheCode()
    {
        using var stream = Workbook(("Ürün Tipi = Ankastre Ocak", "HOBS", "#N/A"));

        Assert.Equal("HOBS", CategoryRuleStore.ReadWorkbook(stream, "RuleSet.xlsx").Single().CategoryTr);
    }

    /// <summary>A workbook that is not a RuleSet says so, rather than loading as an empty catalogue
    /// that would quietly explain why no card ever appears.</summary>
    [Fact]
    public void AWorkbookWithNoProductTypeRulesIsRefused()
    {
        using var stream = Workbook(("Marketplace Brand Name = TEKA", "HOBS", "OCAKLAR"));

        var error = Assert.Throws<InvalidOperationException>(
            () => CategoryRuleStore.ReadWorkbook(stream, "yanlis.xlsx"));

        Assert.Contains("yanlis.xlsx", error.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // Matching a file to a category
    // -----------------------------------------------------------------

    /// <summary>A product file writes its category as a path; the RuleSet names the leaf alone.</summary>
    [Fact]
    public void TheCategoryIsTakenFromTheLeafOfThePath()
    {
        var rule = new CategoryTypeRule("HOBS", "OCAKLAR", ["Ankastre Ocak"]);

        Assert.True(CategoryRuleStore.Covers(rule, "EV ALETLERİ/BÜYÜK EV ALETLERİ/OCAKLAR"));
        Assert.False(CategoryRuleStore.Covers(rule, "EV ALETLERİ/BÜYÜK EV ALETLERİ/FIRINLAR"));
        Assert.False(CategoryRuleStore.Covers(rule, null));
    }

    /// <summary>The header rows and the odd blank cell are not what a file is about, so the category
    /// is the most common value rather than the first one found.</summary>
    [Fact]
    public void TheFileCategoryIsTheMostCommonValue()
    {
        List<List<string>> table =
        [
            ["Kategori", "Başlık"],
            ["CATEGORY", "TITLE__TR_TR"],
            ["EV ALETLERİ/OCAKLAR", "Bir"],
            ["", "İki"],
            ["EV ALETLERİ/OCAKLAR", "Üç"],
        ];

        Assert.Equal("EV ALETLERİ/OCAKLAR", CategoryRuleStore.FileCategory(table));
    }

    // -----------------------------------------------------------------
    // The cards
    // -----------------------------------------------------------------

    static readonly CategoryTypeRule Hobs =
        new("HOBS", "OCAKLAR", ["Ankastre Ocak", "ankastre ocak"]);

    static readonly CategoryTypeRule OtherBuiltIn =
        new("OTHER BUILD IN", "DİĞER ANKASTRE ÜRÜNLER",
            ["İndüksiyonlu Ocak", "indüksiyonlu ocak", "İndüksiyon Ocak"]);

    static TitleRuleSet TypeOnly(TitleAttributeKind kind = TitleAttributeKind.Alias) =>
        new("Ocak", "Başlık", [new TitleAttributeRule("Ürün Tipi (tr_TR)", kind)]);

    static IReadOnlyList<TitleFix> Cards(
        TitleRuleSet set, List<List<string>> table, IReadOnlyList<CategoryTypeRule> rules, string? category)
    {
        var compiled = CompiledRuleSet.Compile(set);
        return TitleFixSuggester.SuggestCategoryTypes(
            compiled, TitleCleanBuilder.Clean(compiled, table), rules, category);
    }

    static List<List<string>> HobTable() =>
    [
        ["Başlık", "Ürün Tipi (tr_TR)"],
        ["GL General GLO 205CS Bask 60 cm Siyah Cam Ankastre Ocak", "Ankastre ocak"],
    ];

    /// <summary>
    /// The case the team asked for: the title names a type, the RuleSet defines it under the file's
    /// own category, and one card carries the whole group.
    /// </summary>
    [Fact]
    public void ATypeTheRuleSetDefinesForThisCategoryIsOfferedTicked()
    {
        var fix = Assert.Single(Cards(TypeOnly(), HobTable(), [Hobs], "EV ALETLERİ/OCAKLAR"));

        Assert.Equal(TitleFixKind.AdoptCategoryType, fix.Kind);
        Assert.Equal("Ankastre Ocak", fix.CellValue);
        Assert.True(fix.Preselected);
        Assert.Null(fix.Warning);

        // "ankastre ocak" folds onto "Ankastre Ocak", so the matcher would never tell them apart.
        // Offering both would show half a proposal that does nothing.
        Assert.Equal("Ankastre Ocak", fix.Value);
    }

    /// <summary>Spellings the fold cannot tell apart are collapsed; ones it can are all kept.</summary>
    [Fact]
    public void OnlySpellingsThatDifferAfterFoldingAreOffered()
    {
        var rule = new CategoryTypeRule("HOBS", "OCAKLAR",
            ["Elektrikli Ocak", "elektrikli ocak", "Elektrikli Ankastre Ocak"]);

        List<List<string>> table =
        [
            ["Başlık", "Ürün Tipi (tr_TR)"],
            ["Çetintaş CSA VE 222 Seramik Siyah 2 Gözlü Elektrikli Ankastre Ocak", "Elektrikli ocak"],
        ];

        var fix = Assert.Single(Cards(TypeOnly(), table, [rule], "EV ALETLERİ/OCAKLAR"));

        Assert.Equal("Elektrikli Ocak|Elektrikli Ankastre Ocak", fix.Value);
    }

    /// <summary>Applying it does both halves: the type leaves the title and the cell is pulled onto
    /// the marketplace's own spelling.</summary>
    [Fact]
    public void ApplyingItCleansTheTitleAndCorrectsTheCell()
    {
        var set = TypeOnly();
        var table = HobTable();

        var cards = Cards(set, table, [Hobs], "EV ALETLERİ/OCAKLAR");
        var updated = TitleFixSuggester.Apply(set, cards, [cards[0].Id]);

        Assert.Equal(["Ankastre Ocak"], updated.AttributeList.Single().AliasGroups.Single());

        var row = TitleCleanBuilder.Clean(CompiledRuleSet.Compile(updated), table).Single();

        Assert.Equal("GL General GLO 205CS Bask 60 cm Siyah Cam", row.CleanTitle);
        Assert.Equal(TitleAttributeStatus.Corrected, row.Attributes.Single().Status);
        Assert.Equal("Ankastre Ocak", row.Attributes.Single().Value);
    }

    /// <summary>
    /// The validation the team asked for. The file says OCAKLAR; the RuleSet files this type under
    /// DİĞER ANKASTRE ÜRÜNLER. The card still appears — that is how the operator finds out — but it
    /// arrives unticked and names the other category.
    /// </summary>
    [Fact]
    public void ATypeTheRuleSetFilesElsewhereIsOfferedUntickedAndSaysSo()
    {
        List<List<string>> table =
        [
            ["Başlık", "Ürün Tipi (tr_TR)"],
            ["Teka IR 8430 5 Zone 80 cm Siyah İndüksiyon Ocak", "İndüksiyonlu ocak"],
        ];

        var fix = Assert.Single(Cards(TypeOnly(), table, [Hobs, OtherBuiltIn], "EV ALETLERİ/OCAKLAR"));

        Assert.False(fix.Preselected);
        Assert.NotNull(fix.Warning);

        // The warning is what carries the disagreement: where the RuleSet files this type, against
        // what the file itself declares.
        Assert.Contains("DİĞER ANKASTRE ÜRÜNLER", fix.Warning, StringComparison.Ordinal);
        Assert.Contains("EV ALETLERİ/OCAKLAR", fix.Warning, StringComparison.Ordinal);
    }

    /// <summary>A group the column already carries needs no card.</summary>
    [Fact]
    public void AGroupAlreadyInTheCatalogueIsNotOffered()
    {
        var set = new TitleRuleSet("Ocak", "Başlık",
            [new TitleAttributeRule("Ürün Tipi (tr_TR)", TitleAttributeKind.Alias,
                Aliases: [["Ankastre Ocak", "ankastre ocak"]])]);

        Assert.Empty(Cards(set, HobTable(), [Hobs], "EV ALETLERİ/OCAKLAR"));
    }

    /// <summary>Without a RuleSet the module behaves exactly as it did before this feature.</summary>
    [Fact]
    public void NoRuleSetMeansNoCards()
    {
        Assert.Empty(Cards(TypeOnly(), HobTable(), [], "EV ALETLERİ/OCAKLAR"));
    }

    /// <summary>A rule set with no product-type column has nowhere to put the vocabulary.</summary>
    [Fact]
    public void ARuleSetWithoutAProductTypeColumnGetsNoCards()
    {
        var set = new TitleRuleSet("Ocak", "Başlık", [new TitleAttributeRule("Marka")]);

        List<List<string>> table = [["Başlık", "Marka"], ["GL General Ankastre Ocak", "GL General"]];

        Assert.Empty(Cards(set, table, [Hobs], "EV ALETLERİ/OCAKLAR"));
    }

    /// <summary>The column is named for its locale on a real product file, so the match is on the
    /// prefix rather than the whole name.</summary>
    [Fact]
    public void TheProductTypeColumnIsFoundThroughItsLocaleSuffix()
    {
        Assert.Single(Cards(TypeOnly(), HobTable(), [Hobs], "EV ALETLERİ/OCAKLAR"));
    }

    /// <summary>A plain text column has no catalogue, so adopting a group changes its type — an
    /// effect beyond the rows on the card.</summary>
    [Fact]
    public void AdoptingOnATextColumnWarnsThatTheTypeChanges()
    {
        var fix = Assert.Single(
            Cards(TypeOnly(TitleAttributeKind.Text), HobTable(), [Hobs], "EV ALETLERİ/OCAKLAR"));

        Assert.Contains("Değer Listesi", fix.Warning, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // Storage
    // -----------------------------------------------------------------

    [Fact]
    public void TheParsedRuleSetSurvivesAJsonRoundTrip()
    {
        var file = new CategoryRuleFile(1, "2026-08-31 12:00:00Z", "RuleSet 35 1.xlsx", [Hobs, OtherBuiltIn]);

        var round = CategoryRuleStore.Parse(CategoryRuleStore.Serialize(file));

        Assert.Equal("RuleSet 35 1.xlsx", round.SourceName);
        Assert.Equal(2, round.RuleList.Count);
        Assert.Equal(OtherBuiltIn.Types, round.RuleList[1].Types);
        Assert.Equal("DİĞER ANKASTRE ÜRÜNLER", round.RuleList[1].CategoryTr);
    }
}
