using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// The engine run end to end over the marketplace exports this module was actually built against.
///
/// <para>The unit tests next door pin one rule each against a title written to exercise it. These pin
/// the whole thing against files nobody wrote for a test — 300 columns, a technical field-code row,
/// titles typed by five different sellers — which is where every rule in this module came from in the
/// first place. A change that satisfies every unit test and still ruins a real file is the failure
/// mode worth spending a slow test on.</para>
///
/// <para>The workbooks are the ones served from <c>wwwroot</c>, copied into the test output by the web
/// project's content glob. They are read, never written.</para>
/// </summary>
public class TitleCleanerRealFileTests
{
    static readonly MeasureUnit Gb = new("GB", ["gb", "gbyte", "gigabayt"], 1);
    static readonly MeasureUnit Tb = new("TB", ["tb", "tbyte", "terabayt"], 1024);
    static readonly MeasureUnit Inch = new("\"", ["\"", "''", "inç", "inc", "inch"]);

    /// <summary>
    /// The naming standard for the laptop file, as a category operator would build it.
    ///
    /// <para>Two absences are deliberate. <c>Kutu İçeriği (tr_TR)</c> holds "Bilgisayar" on every row
    /// and a rule for it would cut that word out of "Dizüstü Bilgisayar", leaving the product type
    /// stranded — the column describes what is in the box, not what the product is. And
    /// <c>Ekran boyutu(cm)</c> is never written in a title, so a rule for it would only ever report
    /// itself missing.</para>
    /// </summary>
    static TitleRuleSet LaptopRules() => new(
        "Dizüstü",
        "Başlık",
        [
            // Longest and most specific first: attribute order decides who wins a contested span.
            new TitleAttributeRule("Ürün Tipi (tr_TR)", TitleAttributeKind.Alias,
                Aliases:
                [
                    // The marketplace's own RuleSet group for NOTEBOOKS, plus the one spelling this
                    // seller uses that the marketplace has never heard of.
                    [
                        "Notebook", "Laptop", "Workstations Laptop", "Dizüstü Bilgisayar",
                        "Dönüştürebilir Dizüstü Bilgisayar", "Taşınabilir Bilgisayar", "Dizüstü",
                    ],
                ]),
            new TitleAttributeRule("İşletim Sistemi", TitleAttributeKind.Alias,
                Aliases:
                [
                    ["Windows 11 Pro", "W11P"],
                    // "W11" alone is the seller's own shorthand and does not say which edition. It
                    // sits with Home because that is what the row carrying it holds; a Pro row that
                    // wrote "W11" would be reported as a disagreement, which is the right answer.
                    ["Windows 11 Home", "W11H", "W11"],
                    ["FreeDOS", "FDOS", "Free DOS"],
                ]),
            new TitleAttributeRule("İşlemci (tr_TR)", AllowPartial: true, ReferenceList: "İşlemciler"),
            new TitleAttributeRule("İşlemci Modeli"),
            new TitleAttributeRule("Marka"),
            new TitleAttributeRule("Sabit disk kapasitesi", TitleAttributeKind.Measure, Units: [Gb, Tb]),
            new TitleAttributeRule("RAM Bellek Boyutu", TitleAttributeKind.Measure, Units: [Gb, Tb]),
            new TitleAttributeRule("Ekran Boyutu (inç)", TitleAttributeKind.Measure, Units: [Inch]),
            new TitleAttributeRule("Sabit disk tipi", TitleAttributeKind.Alias,
                Aliases: [["SSD"], ["HDD"], ["eMMC"]]),
            new TitleAttributeRule("Renk (temel)"),
            // The card's own brand is dropped in a title — "GeForce RTX™ 4050" is written "RTX4050" —
            // so this column is one of the few where part of the value answers for the whole.
            new TitleAttributeRule("Grafik Kartı", AllowPartial: true),
        ]);

    // -----------------------------------------------------------------

    static List<List<string>> Table(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", fileName);
        Assert.True(File.Exists(path), $"The sample workbook is missing from the test output: {path}");

        using var stream = File.OpenRead(path);
        return TabularFile.Read(stream, fileName);
    }

    /// <summary>The published Intel/AMD catalogue, read the way the upload endpoint reads it — every
    /// sheet, one named column. Intel and AMD sit on two sheets behind a page of provenance notes.</summary>
    static TitleReferenceList Processors()
    {
        const string file = "intel_amd_tum_islemci_modelleri_2026-08-17 1.xlsx";
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", file);
        Assert.True(File.Exists(path), $"The processor catalogue is missing from the test output: {path}");

        using var stream = File.OpenRead(path);
        return new TitleReferenceList(
            "İşlemciler", file, TitleReferenceStore.ReadWorkbook(stream, "Tam İşlemci Adı"));
    }

    static IReadOnlyList<TitleCleanRow> Clean(TitleRuleSet rules, string fileName) =>
        TitleCleanBuilder.Clean(CompiledRuleSet.Compile(rules, [Processors()]), Table(fileName));

    static string Title(IReadOnlyList<TitleCleanRow> rows, int rowNumber) =>
        rows.First(r => r.RowNumber == rowNumber).CleanTitle;

    // -----------------------------------------------------------------
    // What the laptop export comes out as
    // -----------------------------------------------------------------

    /// <summary>
    /// One row per way this file writes a title. Between them they cover every rule the module
    /// gained for it: the glued processor ("Ultra5", "Ryzen5", "Core5"), the capacity written with no
    /// unit at all ("512SSD"), the model code that is in no cell and comes from the reference
    /// catalogue ("125H", "220", "120U"), and the hyphenated Intel spelling ("i5-13420H").
    /// </summary>
    [Theory]
    // Ultra5 125H · 1TBSSD · the screen the cell disagrees about
    [InlineData(3, "Aspire Lite AL16-51P-580H NX.DCLEY.001A003 15.6\" FullHD")]
    // Ryzen5 220 · 512SSD, both halves of the glued token
    [InlineData(4, "ThinkPad E16 21ST0058TX003 WUXGA")]
    // Core5 120U · nothing at all survives but the model
    [InlineData(5, "Vivobook 15 X1504VA-BQ5383W001")]
    // Ultra7 255U
    [InlineData(9, "ThinkPad T16 21QFS2BHTX WUXGA")]
    // Ryzen5 7520U, and a title carrying the double space a real export writes
    [InlineData(22, "Omnibook 3 DY0G7EA003 FHD")]
    // i5-13420H — the catalogue joins family and model with a hyphen, the cell says only "Intel Core i5"
    [InlineData(50, "Nitro V15 ANV15-51 NH.QNBEY.006 FHD")]
    public void TheLaptopExportCleansToTheModelAndWhatNoColumnClaims(int rowNumber, string expected)
    {
        Assert.Equal(expected, Title(Clean(LaptopRules(), "teknoraks0109.xlsx"), rowNumber));
    }

    /// <summary>
    /// The rows this file gets wrong, and the tool is right to leave alone. All three are cell/title
    /// disagreements a cleaner cannot resolve — which side is true is not something it can know — so
    /// the title keeps what it has and the row goes out for review.
    /// </summary>
    [Theory]
    // The cell says "AMD Ryzen 3"; the title says Ryzen7 3700U, and it is the cell that is wrong.
    [InlineData(20, "IdeaPad 3 81W1005QTX Ryzen7 3700U FullHD")]
    // "Ryzen3-30" is not a processor anybody makes. Ryzen3 goes, the typo stays and is reported.
    [InlineData(11, "Ideapad Ideapad Slim3 82XQ0129TX002 -30 FHD")]
    public void RowsWhoseCellAndTitleDisagreeKeepTheirTitle(int rowNumber, string expected)
    {
        Assert.Equal(expected, Title(Clean(LaptopRules(), "teknoraks0109.xlsx"), rowNumber));
    }

    /// <summary>The screen size is the one conflict this file reports, and it reports it on exactly
    /// the rows where the cell reads 16 inç against a title reading 15.6".</summary>
    [Fact]
    public void TheScreenSizeDisagreementIsReportedRatherThanActedOn()
    {
        var rows = Clean(LaptopRules(), "teknoraks0109.xlsx");
        var conflicted = rows.Where(r => r.Attributes.Any(a =>
            a.Column == "Ekran Boyutu (inç)" && a.Status == TitleAttributeStatus.Conflict)).ToList();

        Assert.Equal(8, conflicted.Count);
        Assert.All(conflicted, r => Assert.Contains("15.6\"", r.CleanTitle, StringComparison.Ordinal));
        Assert.All(conflicted, r => Assert.Equal("16 inç", r.Attributes
            .First(a => a.Column == "Ekran Boyutu (inç)").Value));
    }

    // -----------------------------------------------------------------
    // The invariant, over every real file
    // -----------------------------------------------------------------

    /// <summary>
    /// A value the engine reported as found and removed is not still sitting in the cleaned title.
    ///
    /// <para>Checked over every row of every sample file rather than against a list of words, so it
    /// keeps holding as the files change. Single-word values only: a measurement is written a dozen
    /// ways and a substring test on "16 GB" would be looking for "16" inside a model code.</para>
    /// </summary>
    [Theory]
    [InlineData("teknoraks0109.xlsx")]
    [InlineData("dizüstü-per4mance.xlsx")]
    [InlineData("ocaklar.xlsx")]
    public void NothingReportedAsRemovedIsStillInTheTitle(string fileName)
    {
        var rows = fileName == "teknoraks0109.xlsx"
            ? Clean(LaptopRules(), fileName)
            : Suggested(fileName);

        var rules = fileName == "teknoraks0109.xlsx"
            ? CompiledRuleSet.Compile(LaptopRules(), [Processors()])
            : CompiledRuleSet.Compile(TitleRuleSuggester.Suggest(Table(fileName), fileName).RuleSet);

        var removing = rules.Attributes
            .Where(a => a.Rule.Remove)
            .Select(a => a.Rule.Column)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var words = row.CleanTitle
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(FoldedTitle.Fold)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var attribute in row.Attributes)
            {
                if (!removing.Contains(attribute.Column) ||
                    attribute.Status is not (TitleAttributeStatus.Ok or TitleAttributeStatus.Corrected))
                {
                    continue;
                }

                var said = (attribute.TitleSaid ?? "").Trim();
                if (said.Length == 0 || said.Any(char.IsWhiteSpace))
                    continue;

                Assert.DoesNotContain(FoldedTitle.Fold(said), words, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Cleaning the result again takes nothing further out. A second pass that kept eating characters
    /// would corrupt a catalogue one re-run at a time, and every individual run would look like it had
    /// worked — so it is checked on real files, not only on the constructed one.
    /// </summary>
    [Fact]
    public void ASecondPassOverTheLaptopExportChangesNothing()
    {
        var rules = CompiledRuleSet.Compile(LaptopRules(), [Processors()]);
        var table = Table("teknoraks0109.xlsx");
        var first = TitleCleanBuilder.Clean(rules, table);

        var titleIndex = TabularFile.BuildHeaderIndex(table[0])["Başlık"];
        foreach (var row in first)
            table[row.RowNumber - 1][titleIndex] = row.CleanTitle;

        var second = TitleCleanBuilder.Clean(rules, table);

        Assert.Equal(
            first.Select(r => r.CleanTitle),
            second.Select(r => r.CleanTitle));
    }

    // -----------------------------------------------------------------
    // The other categories, on their own suggested rules
    // -----------------------------------------------------------------

    static IReadOnlyList<TitleCleanRow> Suggested(string fileName)
    {
        var table = Table(fileName);
        var suggestion = TitleRuleSuggester.Suggest(table, fileName);
        return TitleCleanBuilder.Clean(CompiledRuleSet.Compile(suggestion.RuleSet), table);
    }

    /// <summary>
    /// The two files the module was built against before this one, cleaned by the rules their own
    /// suggester proposes. They are here as the check that a change made for laptops did not quietly
    /// change what a white-goods file comes out as — the model codes are the part that must survive,
    /// and they are what a loosened match would take first.
    /// </summary>
    [Theory]
    [InlineData("dizüstü-per4mance.xlsx", 3, "TUF A16 R7 RTX 5070 8GB 8GB FHD+ FDOS FA608PPRV15P302 Gaming Laptop")]
    [InlineData("dizüstü-per4mance.xlsx", 5, "TUF F16 C5 RTX 3050 6GB 65W FHD+ FDOS FX607VJBRL037P324 Gaming Laptop")]
    [InlineData("ocaklar.xlsx", 5, "GL General GLO 022SARS Emaye")]
    [InlineData("ocaklar.xlsx", 6, "IZC 93301 97630 MST BK")]
    public void TheEarlierExportsCleanAsTheyDid(string fileName, int rowNumber, string expected)
    {
        Assert.Equal(expected, Title(Suggested(fileName), rowNumber));
    }

    /// <summary>
    /// The graphics card's own 8 GB beside the system RAM: still reported, still not removed. This is
    /// the case the bare-number work had the most room to break — "8GB 8GB" is two spans of one value
    /// and a rule that took both would delete the card's memory out of the title.
    /// </summary>
    [Fact]
    public void ARepeatedMemorySizeIsStillReportedRatherThanRemovedTwice()
    {
        var row = Suggested("dizüstü-per4mance.xlsx").First(r => r.RowNumber == 3);

        Assert.Contains(row.Attributes, a =>
            a.Column == "RAM Bellek Boyutu" && a.Status == TitleAttributeStatus.Ambiguous);

        Assert.Contains("8GB 8GB", row.CleanTitle, StringComparison.Ordinal);
    }
}
