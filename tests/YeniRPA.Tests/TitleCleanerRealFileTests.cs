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
        ],
        // This seller types the series twice on two rows ("Ideapad Ideapad Slim3").
        CollapseRepeats: true);

    /// <summary>
    /// A second seller's laptop export. Same template, different habits: two disks in one cell, the
    /// family and the model code joined with a hyphen, a hundred-character cut landing wherever it
    /// lands, and one row whose title column holds a warranty advert instead of a product name.
    ///
    /// <para>The product-type column is <b>one group with Düzelt off</b>, which is the shape a laptop
    /// category actually needs. Every word a title uses for its own kind belongs together so the
    /// title loses it whichever one it wrote; and the cell keeps what it holds, because rewriting a
    /// "Gaming" row's cell to "Notebook" would throw away what that row says it is.</para>
    /// </summary>
    static TitleRuleSet SecondSellerRules() => new(
        "Dizüstü — ikinci satıcı",
        "Başlık",
        [
            new TitleAttributeRule("Ürün Tipi (tr_TR)", TitleAttributeKind.Alias, Correct: false,
                Aliases:
                [
                    [
                        "Notebook", "Laptop", "Gaming", "İş İstasyonu",
                        "Dizüstü Bilgisayar", "Taşınabilir Bilgisayar", "Taşınabilir İş İstasyonu",
                        "Dizüstü",
                    ],
                ]),
            new TitleAttributeRule("İşletim Sistemi", TitleAttributeKind.Alias,
                Aliases:
                [
                    ["Windows 11 Pro", "W11P"],
                    ["Windows 11 Home", "W11H", "W11"],
                    ["FreeDOS", "FDOS"],
                ]),
            new TitleAttributeRule("İşlemci (tr_TR)", AllowPartial: true, ReferenceList: "İşlemciler"),
            new TitleAttributeRule("İşlemci Modeli"),
            new TitleAttributeRule("Marka"),
            new TitleAttributeRule("Sabit disk kapasitesi", TitleAttributeKind.Measure, Units: [Gb, Tb]),
            new TitleAttributeRule("RAM Bellek Boyutu", TitleAttributeKind.Measure, Units: [Gb, Tb]),
            new TitleAttributeRule("Ekran Boyutu (inç)", TitleAttributeKind.Measure, Units: [Inch]),
            new TitleAttributeRule("Sabit disk tipi", TitleAttributeKind.Alias, Aliases: [["SSD"], ["HDD"]]),
            new TitleAttributeRule("Renk (temel)"),
            new TitleAttributeRule("Grafik Kartı", AllowPartial: true),
        ],
        ".",
        CollapseRepeats: true);

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
    // The marketplace cut this title at 100 characters, mid-word: "… W11P Dizüstü Bi"
    [InlineData(7, "Aspire Lite AL16-51P-580H NX.DCLEY.001A006 15.6\" FullHD")]
    [InlineData(39, "Aspire Lite AL16-51P-580H NX.DCLEY.001 15.6\" FullHD")]
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
    // "Ryzen3-30" is not a processor anybody makes. Ryzen3 goes, the typo stays and is reported —
    // and the series the seller typed twice goes, because this rule set asks for that.
    [InlineData(11, "Ideapad Slim3 82XQ0129TX002 -30 FHD")]
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
    [InlineData("laptop-test.xlsx")]
    [InlineData("dizüstü-per4mance.xlsx")]
    [InlineData("ocaklar.xlsx")]
    public void NothingReportedAsRemovedIsStillInTheTitle(string fileName)
    {
        var rules = fileName switch
        {
            "teknoraks0109.xlsx" => CompiledRuleSet.Compile(LaptopRules(), [Processors()]),
            "laptop-test.xlsx" => CompiledRuleSet.Compile(SecondSellerRules(), [Processors()]),
            _ => CompiledRuleSet.Compile(TitleRuleSuggester.Suggest(Table(fileName), fileName).RuleSet),
        };

        var rows = TitleCleanBuilder.Clean(rules, Table(fileName));

        var removing = rules.Attributes
            .Where(a => a.Rule.Remove)
            .Select(a => a.Rule.Column)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            // A row whose title is not a product name keeps everything it had, on purpose. Its
            // attributes still report what they found — the match was real — and the one thing that
            // did match is exactly what stays put.
            if (row.TitleSuspect)
                continue;

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

    // -----------------------------------------------------------------
    // The second seller's export
    // -----------------------------------------------------------------

    static IReadOnlyList<TitleCleanRow> SecondSeller() =>
        TitleCleanBuilder.Clean(
            CompiledRuleSet.Compile(SecondSellerRules(), [Processors()]),
            Table("laptop-test.xlsx"));

    /// <summary>One row per way this file breaks what came before it.</summary>
    [Theory]
    // Two disks in one cell, written "1TBSSD+1TBSSD" — and a title cut at 100 characters mid-word
    [InlineData(3, "ThinkPad P16 Gen3 21RQ000JTX001 WUXGA")]
    // Two *different* disks, and the cut landing on a space instead
    [InlineData(8, "ThinkPad P16 Gen3 21RQ000JTX008 WUXGA")]
    // "128SSD+1TBSSD": one disk written with no unit at all, beside one that has it
    [InlineData(9, "ThinkPad P16 Gen3 21RQ000JTX004 WUXGA")]
    // The family and the model code joined with a hyphen where the catalogue uses a space
    [InlineData(14, "LOQ 83S0002YTR FullHD")]
    // …and the mirror: a space where the catalogue uses a hyphen ("i5 14450HX")
    [InlineData(74, "OMEN 15 D2QD1EA003 15.3\" WUXGA")]
    // "Intel Core Ultra 5 225" sits before "…225U" in the catalogue and used to block it
    [InlineData(93, "ProBook 4 G1i D21PFET WUXGA")]
    public void TheSecondSellersExportCleansToItsModel(int rowNumber, string expected)
    {
        Assert.Equal(expected, Title(SecondSeller(), rowNumber));
    }

    /// <summary>
    /// The card the operator needs when the product-type column is empty, on the file that could not
    /// produce one before.
    ///
    /// <para>Eleven rows carry "İş İstasyonu" and the marketplace cut every one of their titles
    /// before "İstasyonu" finished, so no row spells the seller's phrase out. Nothing anchored, no
    /// card, and the leftover report could only say "nothing claims this" about "Taşınabilir" —
    /// while "İş", at two letters, never reached the report at all.</para>
    /// </summary>
    [Fact]
    public void TheTruncatedProductTypeIsOfferedAsASpelling()
    {
        // The same rules with that column's catalogue emptied — where an operator starts.
        var bare = SecondSellerRules() with
        {
            Attributes = SecondSellerRules().AttributeList
                .Select(a => a.Column == "Ürün Tipi (tr_TR)" ? a with { Aliases = null } : a)
                .ToList(),
        };

        var rules = CompiledRuleSet.Compile(bare, [Processors()]);
        var rows = TitleCleanBuilder.Clean(rules, Table("laptop-test.xlsx"));

        var fix = Assert.Single(
            TitleFixSuggester.Suggest(rules, rows),
            f => f.Column == "Ürün Tipi (tr_TR)" && f.Value == "Taşınabilir İş İstasyonu");

        Assert.Equal(TitleFixKind.AdoptPhrase, fix.Kind);
        Assert.DoesNotContain("Taşınabilir", fix.SampleAfter, StringComparison.Ordinal);
    }

    /// <summary>
    /// No protector is offered for the double-disk token, and it takes two things to get there.
    ///
    /// <para>One row of this file has a disk-capacity cell reading "2 TB" while its title carries two
    /// disks, so the second "SSD" has nothing to anchor it and the repeat is real as far as the row
    /// is concerned. But both occurrences sit inside one token — there is no phrase around the other
    /// one to hand to anybody — so the honest answer is no card, and the row stays in review.</para>
    ///
    /// <para>It used to offer "350 32GB 1TBSSD+2TBSSD": the phrase search joined two words to find a
    /// value that sat whole inside the second, then reached left over the RAM. That is how one rule
    /// set acquired "1TBSSD+2TBSSD" as a disk type.</para>
    /// </summary>
    [Fact]
    public void NoProtectorIsOfferedForTheDoubleDiskToken()
    {
        var rules = CompiledRuleSet.Compile(SecondSellerRules(), [Processors()]);
        var rows = TitleCleanBuilder.Clean(rules, Table("laptop-test.xlsx"));

        Assert.DoesNotContain(
            TitleFixSuggester.Suggest(rules, rows),
            f => f.Kind == TitleFixKind.ProtectPhrase);
    }

    /// <summary>
    /// And no card offers to teach the catalogue that "Ryzen7" is a spelling of "Ryzen™ 3". Those two
    /// rows are a genuine data error — the cell says one processor and the title another — and taking
    /// the card would clean every Ryzen 7 title as a Ryzen 3 from then on.
    /// </summary>
    [Fact]
    public void NoCardAdoptsAProcessorSpellingThatDiffersByItsDigit()
    {
        var rules = CompiledRuleSet.Compile(SecondSellerRules(), [Processors()]);
        var rows = TitleCleanBuilder.Clean(rules, Table("laptop-test.xlsx"));

        Assert.DoesNotContain(
            TitleFixSuggester.Suggest(rules, rows),
            f => f.Column == "İşlemci Modeli");
    }

    /// <summary>
    /// The row whose title column holds a warranty advert. Its brand cell says LENOVO and the line
    /// does contain LENOVO, so the cleaner would faithfully cut it out and write the rest back — a
    /// mangled sentence, to the marketplace. One match out of twelve filled cells is not a product
    /// title, and the row is handed over untouched instead.
    /// </summary>
    [Fact]
    public void TheRowWhoseTitleIsAnAdvertIsHandedOverUntouched()
    {
        var row = SecondSeller().Single(r => r.RowNumber == 13);

        Assert.True(row.TitleSuspect);
        Assert.Equal(row.OriginalTitle, row.CleanTitle);
        Assert.Contains("LENOVO", row.CleanTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cleaning the result again takes nothing further out. A second pass that kept eating characters
    /// would corrupt a catalogue one re-run at a time, and every individual run would look like it had
    /// worked — so it is checked on real files, not only on the constructed one.
    /// </summary>
    [Theory]
    [InlineData("teknoraks0109.xlsx")]
    [InlineData("laptop-test.xlsx")]
    public void ASecondPassOverALaptopExportChangesNothing(string fileName)
    {
        var rules = fileName == "teknoraks0109.xlsx"
            ? CompiledRuleSet.Compile(LaptopRules(), [Processors()])
            : CompiledRuleSet.Compile(SecondSellerRules(), [Processors()]);

        var table = Table(fileName);
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

    // -----------------------------------------------------------------
    // 7. The operator's own rule set
    // -----------------------------------------------------------------

    /// <summary>
    /// The saved "Laptop" set as it stands in the operator's own <c>title-rules.json</c>, copied here
    /// verbatim.
    ///
    /// <para>Every other fixture in this file is a set <em>I</em> wrote, and they are all tidy: one
    /// product-type group, a processor column that leans on the catalogue, a disk-type column holding
    /// nothing but "SSD". A set that has been in daily use does not look like that. This one has two
    /// product-type groups whose members overlap ("Gaming Laptop" against a group holding both
    /// "Gaming" and "Laptop"), a bare "Ultra9" standing alone under the processor column, and a
    /// "1TBSSD+2TBSSD" under disk type — every one of them added by accepting a suggestion card, and
    /// every one of them a shape no fixture of mine ever produced. The
    /// <c>"Notebook" başlıkta 2 kez geçiyor</c> report on a title containing no such word got through
    /// because of that gap, so the messy set is now a fixture and stays one.</para>
    /// </summary>
    static TitleRuleSet MessyLaptopRules() => new(
        "Laptop",
        "Başlık",
        [
            new TitleAttributeRule("Grafik Kartı", TitleAttributeKind.Alias, Remove: false, Correct: false,
                Aliases:
                [
                    ["GeForce RTX™ 5070", "RTX 5070 8GB"],
                    ["GeForce RTX™ 3050", "RTX 3050 6GB"],
                    ["Radeon™ Onboard Graphics"],
                    ["Arc™ Onboard Graphics"],
                    ["Radeon™ 890M"],
                    ["Onboard Graphics"],
                ]),
            // Two groups, and the second holds both words of the first. This is the pair that
            // produced the phantom repeat.
            new TitleAttributeRule("Ürün Tipi (tr_TR)", TitleAttributeKind.Alias,
                Aliases:
                [
                    ["Gaming Laptop", "Oyun Bilgisayarı"],
                    [
                        "Notebook", "Laptop", "Gaming", "İş İstasyonu", "Dizüstü Bilgisayar",
                        "Taşınabilir Bilgisayar", "Taşınabilir İş İstasyonu", "Dizüstü",
                    ],
                ]),
            new TitleAttributeRule("İşletim Sistemi", TitleAttributeKind.Alias,
                Aliases:
                [
                    ["FreeDOS", "FDOS", "İşletim Sistemi Bulunmuyor"],
                    ["Windows 11 Pro", "W11P"],
                    ["Windows 11 Home", "W11H"],
                ]),
            new TitleAttributeRule("İşlemci (tr_TR)", TitleAttributeKind.Alias, ReferenceList: "İşlemciler",
                Aliases:
                [
                    ["AMD Ryzen 7 8745HX", "8745HX", "R7 8745HX"],
                    ["Intel Core 5 210H", "210H", "C5 210H"],
                    ["Intel Core Ultra 7 258V", "258V", "U7 258V"],
                    ["Intel Core Ultra 5 225H", "225H", "U5 225H"],
                    ["AMD Ryzen AI 7 445", "445", "R AI 7 445", "AI 7 445"],
                    ["AMD Ryzen AI 9 465", "465", "R AI 9 465", "AI 9 465"],
                    ["AMD Ryzen AI 9 HX 370", "370", "R AI 9 HX370", "HX370"],
                    ["AMD Ryzen 7", "Ryzen7 170"],
                    // Standing alone under the processor column, so no cell ever resolves to it and
                    // every "Ultra9" title conflicts. Kept because the operator's file keeps it.
                    ["Ultra9"],
                ]),
            new TitleAttributeRule("Sabit disk kapasitesi", TitleAttributeKind.Measure, Units: [Gb, Tb]),
            new TitleAttributeRule("RAM Bellek Boyutu", TitleAttributeKind.Measure, Units: [Gb, Tb]),
            new TitleAttributeRule("Ekran Boyutu (inç)", TitleAttributeKind.Measure,
                Units: [new MeasureUnit("inç", ["inc", "\"", "''", "inch", "inches"])]),
            new TitleAttributeRule("Sabit disk tipi", TitleAttributeKind.Alias, Correct: false,
                Aliases: [["SSD"], ["1TBSSD+2TBSSD"]]),
            new TitleAttributeRule("Marka", Aliases: [["TUF A16"]]),
        ]);

    /// <summary>The catalogue under the name the operator's set refers to it by.</summary>
    static TitleReferenceList MessyReference() =>
        Processors() with { Name = "İşlemciler" };

    static IReadOnlyList<TitleCleanRow> Messy(string fileName) =>
        TitleCleanBuilder.Clean(
            CompiledRuleSet.Compile(MessyLaptopRules(), [MessyReference()]), Table(fileName));

    /// <summary>
    /// "Gaming Laptop" is one product type spelled out, not the word "Gaming" plus the word "Laptop".
    /// The operator's group holds both, and counting them separately reported the value twice — under
    /// the group's canonical name, which the title never used.
    /// </summary>
    [Theory]
    [InlineData("Asus TUF Gaming Laptop A16 FA608WI", "Dizüstü", "Asus TUF A16 FA608WI")]
    [InlineData("MSI Katana 15 Gaming Notebook", "Laptop", "MSI Katana 15")]
    [InlineData("Lenovo V15 Dizüstü Bilgisayar Notebook", "Notebook", "Lenovo V15")]
    public void TwoSpellingsOfOneGroupSideBySideAreOneProductType(
        string title, string cell, string expected)
    {
        var rules = CompiledRuleSet.Compile(
            new TitleRuleSet("Laptop", "Başlık",
                MessyLaptopRules().AttributeList.Where(a => a.Column == "Ürün Tipi (tr_TR)").ToList()),
            [MessyReference()]);

        var row = TitleCleanBuilder.Clean(rules, [["Başlık", "Ürün Tipi (tr_TR)"], [title, cell]]).Single();

        Assert.Empty(row.Errors);
        Assert.Equal(expected, row.CleanTitle);
    }

    /// <summary>
    /// The same word written twice is still a repeat. Folding the phrase must not fold this, or it
    /// would hide the case the guard exists for.
    /// </summary>
    [Fact]
    public void TheSameSpellingTwiceInARowIsStillAmbiguous()
    {
        var rules = CompiledRuleSet.Compile(
            new TitleRuleSet("Laptop", "Başlık",
                MessyLaptopRules().AttributeList.Where(a => a.Column == "Ürün Tipi (tr_TR)").ToList()),
            [MessyReference()]);

        var row = TitleCleanBuilder
            .Clean(rules, [["Başlık", "Ürün Tipi (tr_TR)"], ["Lenovo V15 Notebook Notebook", "Notebook"]])
            .Single();

        Assert.Contains(row.Attributes, a =>
            a.Column == "Ürün Tipi (tr_TR)" && a.Status == TitleAttributeStatus.Ambiguous);
    }

    /// <summary>
    /// Every phrase a message attributes to the title is in the title. The rule's canonical spelling
    /// is what the group is called, not what the seller typed, and quoting it as "what the title says"
    /// sends the operator looking for a word that is not on the page.
    /// </summary>
    [Theory]
    [InlineData("laptop-test.xlsx")]
    [InlineData("teknoraks0109.xlsx")]
    public void AMessageOnlyQuotesTheTitleForWhatTheTitleActuallySays(string fileName)
    {
        foreach (var row in Messy(fileName))
        {
            var quoted = row.Attributes
                .Where(a => a.Reason is TitleAttributeReason.ValueRepeated
                                     or TitleAttributeReason.Disagreement)
                .SelectMany(a => (a.TitleSaid ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries));

            foreach (var phrase in quoted)
            {
                Assert.Contains(phrase, row.OriginalTitle, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The screen-size rows: one card per distinct pair of readings, each asking which is right.
    ///
    /// <para>Two pairs in this file — 15.6" against a cell of 16 inches and 15.3" against 15 — and
    /// they are two separate questions, so they must not be collapsed into one card. Neither arrives
    /// ticked: the engine has no opinion about which side is true.</para>
    /// </summary>
    [Fact]
    public void TheScreenSizeRowsEachAskWhichReadingIsRight()
    {
        var rules = CompiledRuleSet.Compile(MessyLaptopRules(), [MessyReference()]);
        var rows = TitleCleanBuilder.Clean(rules, Table("laptop-test.xlsx"));

        var cards = TitleFixSuggester.Suggest(rules, rows)
            .Where(f => f.Kind == TitleFixKind.MatchMeasure)
            .ToList();

        // 15.6" against 16, 15.3" against 15, 17.3" against 17 — three panels, three questions.
        Assert.Equal(3, cards.Count);

        Assert.All(cards, card =>
        {
            Assert.Equal("Ekran Boyutu (inç)", card.Column);
            Assert.False(card.Preselected);
            Assert.Equal(2, card.ChoiceList.Count);

            // Both answers offered in the column's own canonical form, and each carries the whole
            // value-list line it would write — the head being the size that goes into the cell.
            Assert.All(card.ChoiceList, choice => Assert.Contains('|', choice.Value));
            Assert.NotEqual(card.ChoiceList[0].Value, card.ChoiceList[1].Value);
        });

        // The answers are offered in the column's own canonical form. The cell on some of these rows
        // is written "16 inç inç"; storing that verbatim would put a spelling in the value list that
        // the column cannot read back, and the next compile would refuse the whole set.
        Assert.Contains(cards, c =>
            c.Problem.Contains("15.6\"", StringComparison.Ordinal) &&
            c.ChoiceList[0].Value == "15.6 inç|16 inç" &&
            c.ChoiceList[1].Value == "16 inç|15.6 inç");

        Assert.Contains(cards, c => c.Problem.Contains("15.3\"", StringComparison.Ordinal));
        Assert.Contains(cards, c => c.Problem.Contains("17.3\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Taking the answer end to end: pick the title's reading, and the rows it covers come out
    /// cleaned with the cell pulled onto the size the title named.
    /// </summary>
    [Fact]
    public void AnsweringTheScreenSizeCardCleansTheRowsItCovers()
    {
        var rules = CompiledRuleSet.Compile(MessyLaptopRules(), [MessyReference()]);
        var table = Table("laptop-test.xlsx");
        var rows = TitleCleanBuilder.Clean(rules, table);

        var card = TitleFixSuggester.Suggest(rules, rows)
            .First(f => f.Kind == TitleFixKind.MatchMeasure &&
                        f.Problem.Contains("15.6\"", StringComparison.Ordinal));

        var chosen = card.ChoiceList.First(c => c.Label.StartsWith("Başlıktaki", StringComparison.Ordinal));
        var applied = TitleFixSuggester.Apply(
            rules.Source, [card with { Value = chosen.Value }], [card.Id]);

        var after = TitleCleanBuilder.Clean(
            CompiledRuleSet.Compile(applied, [MessyReference()]), table);

        var before = rows.First(r =>
            r.Attributes.Any(a => a.Column == "Ekran Boyutu (inç)" &&
                                  a.Status == TitleAttributeStatus.Conflict &&
                                  a.TitleSaid == "15.6\""));

        var fixedRow = after.First(r => r.RowNumber == before.RowNumber);
        var screen = fixedRow.Attributes.First(a => a.Column == "Ekran Boyutu (inç)");

        Assert.Equal(TitleAttributeStatus.Corrected, screen.Status);
        Assert.Equal("15.6 inç", screen.Value);
        Assert.DoesNotContain("15.6", fixedRow.CleanTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rows the operator was looking at. Their titles end in a cut-off "Taşınabilir İ" and hold
    /// two disks apiece, and they come out clean — no product-type report, and the fragment gone.
    /// </summary>
    [Theory]
    [InlineData(5, "ThinkPad P16 Gen3 21RQ000JTX010 WUXGA")]
    [InlineData(6, "ThinkPad P16 Gen3 21RQ000JTX003 WUXGA")]
    [InlineData(7, "ThinkPad P16 Gen3 21RQ000JTX009 WUXGA")]
    [InlineData(8, "ThinkPad P16 Gen3 21RQ000JTX008 WUXGA")]
    public void TheOperatorsOwnSetCleansTheWorkstationRows(int rowNumber, string expected)
    {
        var row = Messy("laptop-test.xlsx").First(r => r.RowNumber == rowNumber);

        Assert.Equal(expected, row.CleanTitle);
        Assert.DoesNotContain(row.Attributes, a => a.Column == "Ürün Tipi (tr_TR)" &&
            a.Status is TitleAttributeStatus.Ambiguous or TitleAttributeStatus.Conflict);
    }
}
