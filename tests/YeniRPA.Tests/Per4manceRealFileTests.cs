using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// The "Laptop Per4mance" rule set run end to end over the seller's own 617-row export
/// (<c>per4mance2610.xlsx</c>, 24 model families, one title template).
///
/// <para>Every title in that file reads <c>[model] [cpu] [gpu] [ram] [disk] [screen] [panel] FDOS [code]
/// Notebook + PER4 Çanta</c>, so the rule set is small and the interesting part is what it must leave
/// alone: the model, the model code, the graphics card (a protected column — its memory is what
/// keeps "8GB" from being read as RAM) and the panel words no column claims.</para>
///
/// <para>The 70 rows this file sends to review are <b>the seller's data errors</b>, not engine
/// failures: 43 Predator18 rows carry a different laptop's CPU/GPU/screen cells, 19 Nitro V rows say
/// RTX 4050 against a title's RTX 3050, 7 Raider rows say RTX 5070 Ti against RTX 5080, one Cyborg
/// says 9 GB of RAM against 8GB. A cleaner that resolved those would be guessing which side is true,
/// so they are pinned here as review rows — and a second test shows the same rule set cleaning them
/// completely once the cells are right.</para>
/// </summary>
public class Per4manceRealFileTests
{
    const string FileName = "per4mance2610.xlsx";

    static readonly MeasureUnit[] Disk =
    [
        new("GB", ["gb"], 1),
        new("TB", ["tb"], 1024),
    ];

    // "40 GB RAM" style spellings first: the longer spelling has to be tried before the bare "GB".
    static readonly MeasureUnit[] Ram =
    [
        new("GB", ["gb ram", "gb"], 1),
        new("TB", ["tb ram", "tb"], 1024),
    ];

    static readonly MeasureUnit[] Screen = [new("inç", ["inc", "\"", "''", "inch", "inches"])];

    /// <summary>
    /// The set as the operator imports it (<c>Laptop-Per4mance-kural-seti.xlsx</c>). A separate set
    /// from the saved "Laptop" one on purpose: the seller's CPU shorthand (U9, R7, i5), its graphics
    /// card spellings and the "PER4 Çanta" bundle note are this seller's habits, and folding them into
    /// a set other sellers' files run through would widen what those files may delete.
    ///
    /// <para><b>Düzelt is off on every column.</b> The file's cells are already in the marketplace's
    /// own spelling ("15,6 inç", "İşletim Sistemi Bulunmuyor"); the title is what needs cleaning, and a
    /// rewrite of 839 cells (what the saved set does to this file) is a change nobody asked for to
    /// values the marketplace validates against its own lists.</para>
    /// </summary>
    static TitleRuleSet Rules() => new(
        "Laptop Per4mance",
        "Başlık",
        [
            // Protected, not removed: the card stays in the title. It is listed so its memory
            // ("RTX 5050 8GB") is claimed together with the name — the same size sits beside it as
            // RAM on many rows ("… RTX 5050 8GB 8GB 512GB SSD"), and only one of the two may go.
            new TitleAttributeRule("Grafik Kartı", TitleAttributeKind.Alias, Remove: false, Correct: false, TitleWins: true,
                Aliases:
                [
                    ["GeForce RTX™ 5080", "RTX5080 16GB", "RTX 5080 16GB", "RTX5080", "RTX 5080"],
                    ["GeForce RTX™ 5070 Ti", "RTX5070Ti 12GB", "RTX 5070 Ti 12GB", "RTX5070Ti", "RTX 5070 Ti"],
                    ["GeForce RTX™ 5070", "RTX5070 8GB", "RTX 5070 8GB", "RTX5070 12GB", "RTX 5070 12GB", "RTX5070", "RTX 5070"],
                    ["GeForce RTX™ 5060", "RTX5060 8GB", "RTX 5060 8GB", "RTX5060/8GB", "RTX5060", "RTX 5060"],
                    ["GeForce RTX™ 5050", "RTX5050 8GB", "RTX 5050 8GB", "RTX5050", "RTX 5050"],
                    ["GeForce RTX™ 4050", "RTX4050 6GB", "RTX 4050 6GB", "RTX4050", "RTX 4050"],
                    ["GeForce RTX™ 3050", "RTX3050 6GB", "RTX 3050 6GB", "RTX3050", "RTX 3050"],
                    ["Radeon™ Onboard Graphics"],
                    ["Arc™ Onboard Graphics"],
                    ["Radeon™ 890M"],
                    ["Onboard Graphics"],
                ]),
            // The seller's "+ PER4 Çanta" bundle note is spelled as part of the product type, so it
            // leaves with the word it is attached to ("Notebook + PER4 Çanta") rather than being
            // deleted on the strength of no column at all.
            new TitleAttributeRule("Ürün Tipi (tr_TR)", TitleAttributeKind.Alias, Correct: false,
                Aliases:
                [
                    ["Notebook", "Notebook + PER4 Çanta", "Laptop", "Dizüstü Bilgisayar", "Taşınabilir Bilgisayar", "Dizüstü"],
                    ["Gaming Laptop", "Oyun Bilgisayarı"],
                    ["İş İstasyonu", "Taşınabilir İş İstasyonu"],
                ]),
            // "İşletim Sistemi Bulunmuyor" heads the group so the cell's own spelling is the canonical
            // one; with Düzelt off it is never rewritten either way.
            new TitleAttributeRule("İşletim Sistemi", TitleAttributeKind.Alias, Correct: false,
                Aliases:
                [
                    ["İşletim Sistemi Bulunmuyor", "FreeDOS", "FDOS", "Free DOS"],
                    ["Windows 11 Pro", "W11P", "Win11Pro", "W11Pro", "Win11 Pro", "W11 Pro"],
                    ["Windows 11 Home", "W11H", "Win11Home", "W11Home"],
                    ["Ubuntu"],
                ]),
            // The cell is the model code. Four of them are written in the title with the family
            // letter glued on and the suffix dropped ("R7 170" for 170H) — phrases, never a bare
            // number, so "170" on its own is still never a candidate.
            new TitleAttributeRule("İşlemci (tr_TR)", TitleAttributeKind.Alias, Correct: false, TitleWins: true,
                Aliases:
                [
                    ["170H", "R7 170"],
                    ["260H", "R7 260"],
                    ["325H", "U5 325"],
                    ["365H", "R AI 9 365"],
                    ["275HX"], ["255H"], ["255HX"], ["8940HX"], ["14500HX"], ["13645HX"], ["7445H"],
                    ["210H"], ["12450H"], ["12650HX"], ["14900HX"], ["240H"],
                ]),
            new TitleAttributeRule("İşlemci Modeli", TitleAttributeKind.Alias, Correct: false, TitleWins: true,
                Aliases:
                [
                    ["Core™ Ultra 9", "U9", "Ultra 9", "Ultra9"],
                    ["Core™ Ultra 7", "U7", "Ultra 7", "Ultra7"],
                    ["Core™ Ultra 5", "U5", "Ultra 5", "Ultra5"],
                    ["Core™ Ultra 3", "U3", "Ultra 3", "Ultra3"],
                    ["Core™ 7", "C7"],
                    ["Core™ 3", "C3"],
                    ["Core™ i3", "i3"],
                    ["Ryzen™ AI 7", "R AI 7", "Ryzen AI 7"],
                    ["Ryzen™ AI 5", "R AI 5", "Ryzen AI 5"],
                    ["Ryzen™ 5", "R5", "Ryzen 5"],
                    ["Ryzen™ 3", "R3", "Ryzen 3"],
                    ["Core™ 5", "C5"],
                    ["Core™ i9", "i9"],
                    ["Core™ i7", "i7"],
                    ["Core™ i5", "i5"],
                    ["Ryzen™ AI 9", "R AI 9", "Ryzen AI 9"],
                    ["Ryzen™ 9", "R9", "Ryzen 9"],
                    ["Ryzen™ 7", "R7", "Ryzen 7"],
                    ["Snapdragon X2 Elite", "Snapdragon X2 X2E"],
                    ["Snapdragon X", "Snapdragon X X1"],
                ]),
            new TitleAttributeRule("Sabit disk kapasitesi", TitleAttributeKind.Measure, Correct: false, TitleWins: true, Units: Disk),
            new TitleAttributeRule("RAM Bellek Boyutu", TitleAttributeKind.Measure, Correct: false, TitleWins: true, Units: Ram),
            new TitleAttributeRule("Ekran Boyutu (inç)", TitleAttributeKind.Measure, Correct: false, TitleWins: true, Units: Screen),
            new TitleAttributeRule("Sabit disk tipi", TitleAttributeKind.Alias, Correct: false,
                Aliases:
                [
                    ["SSD"],
                ]),
            new TitleAttributeRule("Marka", TitleAttributeKind.Text, Correct: false),
        ],
        // The marketplace's own list writes "15,6 inç"; with Düzelt off nothing is written back, but
        // a corrected value would have to match it.
        ",");

    static List<List<string>> Table()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", FileName);
        Assert.True(File.Exists(path), $"The sample workbook is missing from the test output: {path}");

        using var stream = File.OpenRead(path);
        return TabularFile.Read(stream, FileName);
    }

    static IReadOnlyList<TitleCleanRow> Clean(List<List<string>>? table = null) =>
        TitleCleanBuilder.Clean(CompiledRuleSet.Compile(Rules()), table ?? Table());

    static string[] Tokens(string title) =>
        title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // -----------------------------------------------------------------
    // What each family comes out as
    // -----------------------------------------------------------------

    /// <summary>One row per way this file writes a title.</summary>
    [Theory]
    // The CPU written as family letter + number, the suffix dropped: "R7 170" for the cell's 170H
    [InlineData(10, "V15 G6 FHD IPS 83V40026TXP325")]
    [InlineData(15, "ROG G16 RTX5070 8GB FHD+ G614PPRV218P323")]
    [InlineData(16, "Predator 16S RTX5070 8GB QHD+ NHQZFEY001P307")]
    [InlineData(17, "Predator16 RTX5070Ti 12GB QHD+ NHQX4EY003P358")]
    // "R AI 9 365" is one processor name, so it goes whole — "AI" alone is the model's own word on EB 6
    [InlineData(25, "Nitro 18 RTX 5070 8GB QHD+ HNJEY002P359")]
    [InlineData(130, "EB 6 G2i AI FHD+ IPS C77J9AVP303")]
    [InlineData(32, "Aspire Lite FHD NXJ7JEY001P311")]
    [InlineData(55, "LOQ RTX 5060 8GB FHD 83JE012LTRP359")]
    // Nothing claims "4G SIM", a panel word or a resolution, so they stay
    [InlineData(66, "EB B3 4G SIM FHD+ B3605CCA-MB2224P357")]
    [InlineData(85, "TB 16 FHD+ 21SK007UTXPN349")]
    // The graphics card's 8GB beside the RAM's 8GB: one goes, one is the card's own
    [InlineData(86, "TUF A16 RTX 5050 8GB FHD+ FA608UHI-TU126P302")]
    [InlineData(147, "Victus 15 RTX 4050 6GB FHD BX9U9EAPN313")]
    [InlineData(207, "Predator 14 RTX5060 8GB QHD+ NHQV0EY001P307")]
    [InlineData(228, "Omen 16 RTX 5060 8GB 2K C39J7EAPN345")]
    // Snapdragon written three ways
    [InlineData(118, "ZB A16 3K UX3607OA-SQ042WP335")]
    [InlineData(229, "VB S16 FHD+ S3607QA-SH037WP309")]
    [InlineData(231, "ZB A14 FHD+ UX3407NA-QD217WP310")]
    [InlineData(235, "VB 16 FHD+ X1607QA-MB052WP311")]
    [InlineData(244, "VB S16 FHD+ S3607NA-SH003WP311")]
    // MSI: the card written "RTX5060/8GB", the model code at the end
    [InlineData(399, "Cyborg 15 RTX5060/8GB FHD B2RWFKG207XTRP305")]
    [InlineData(588, "Cyborg 15 RTX5060/8GB FHD B2RWFKG208XTRP323")]
    [InlineData(402, "Raider 16 HX RTX5070 12GB QHD+ B2WGX018TRP352")]
    public void EachFamilyCleansToItsModelTheCardAndWhatNoColumnClaims(int rowNumber, string expected)
    {
        var row = Clean().Single(r => r.RowNumber == rowNumber);

        Assert.Equal(expected, row.CleanTitle);
        Assert.False(row.HasConflict, $"row {rowNumber} should not be a review row");
    }

    // -----------------------------------------------------------------
    // The invariants, over every one of the 617 rows
    // -----------------------------------------------------------------

    /// <summary>
    /// A cleaned title is the original with whole words taken out and nothing else: no word it did not
    /// start with, none moved, none cut in half. The model code at the end is what a wrong cut would
    /// damage first, so it is checked on its own as well.
    /// </summary>
    [Fact]
    public void EveryCleanedTitleIsTheOriginalMinusWholeWordsInTheSameOrder()
    {
        var rows = Clean();
        Assert.Equal(617, rows.Count);

        foreach (var row in rows)
        {
            var original = Tokens(row.OriginalTitle);
            var cleaned = Tokens(row.CleanTitle);

            var next = 0;
            foreach (var word in cleaned)
            {
                while (next < original.Length && original[next] != word)
                    next++;

                Assert.True(
                    next < original.Length,
                    $"row {row.RowNumber}: '{word}' is not a word of the original, or moved: {row.CleanTitle}");
                next++;
            }

            // The model code is the last word once the bundle note is set aside — it sits either
            // before "Notebook + PER4 Çanta" or after it — and it never goes.
            var code = original.Where(w => w is not ("Notebook" or "+" or "PER4" or "Çanta")).Last();
            Assert.Contains(code, cleaned);
        }
    }

    [Fact]
    public void TheBundleNoteAndTheProductTypeLeaveEveryTitle()
    {
        foreach (var row in Clean())
        {
            Assert.DoesNotContain("PER4", row.CleanTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("Çanta", row.CleanTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("Notebook", row.CleanTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("FDOS", row.CleanTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("SSD", row.CleanTitle, StringComparison.Ordinal);
            Assert.DoesNotContain(" + ", row.CleanTitle, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The file goes back to the marketplace, so a cell is only ever rewritten where the title names a
    /// different value for it (Başlık Doğru) — never for spelling, and never on a column without the
    /// switch. Exactly 199 cells, in exactly the columns of the rows whose cells copy another laptop.
    /// </summary>
    [Fact]
    public void OnlyCellsTheTitleContradictsAreRewritten()
    {
        var rewritten = Clean()
            .SelectMany(r => r.Attributes.Select(a => (Row: r, Attribute: a)))
            .Where(x => x.Attribute.Status == TitleAttributeStatus.Corrected)
            .ToList();

        Assert.Equal(199, rewritten.Count);

        Assert.All(rewritten, x => Assert.NotEqual(x.Attribute.OriginalValue, x.Attribute.Value));

        Assert.Equal(
            new Dictionary<string, int>
            {
                ["Ekran Boyutu (inç)"] = 43,
                ["Grafik Kartı"] = 69,
                ["RAM Bellek Boyutu"] = 1,
                ["İşlemci (tr_TR)"] = 43,
                ["İşlemci Modeli"] = 43,
            },
            rewritten.GroupBy(x => x.Attribute.Column).ToDictionary(g => g.Key, g => g.Count()));

        // Every other cell comes back exactly as it went in.
        foreach (var row in Clean())
        {
            Assert.All(
                row.Attributes.Where(a => a.Status != TitleAttributeStatus.Corrected),
                a => Assert.Equal(a.OriginalValue, a.Value));
        }
    }

    [Fact]
    public void ASecondPassChangesNothing()
    {
        var table = Table();
        var first = Clean(table);

        var titleIndex = TabularFile.BuildHeaderIndex(table[0])["Başlık"];
        // The output file as the operator uploads it again: the cleaned title and the corrected cells.
        var header = TabularFile.BuildHeaderIndex(table[0]);
        foreach (var row in first)
        {
            table[row.RowNumber - 1][titleIndex] = row.CleanTitle;
            foreach (var a in row.Attributes.Where(a => a.Status == TitleAttributeStatus.Corrected))
                table[row.RowNumber - 1][header[a.Column]] = a.Value;
        }

        Assert.Equal(first.Select(r => r.CleanTitle), Clean(table).Select(r => r.CleanTitle));
    }

    // -----------------------------------------------------------------
    // Rows whose cells contradict their title: the title is taken as right
    // -----------------------------------------------------------------

    /// <summary>
    /// 70 rows carry cells that contradict their own title: 43 Predator18 rows hold another laptop's
    /// CPU, graphics card and screen, 19 Nitro V rows say RTX 4050 against a title's RTX 3050, 7 Raider
    /// rows say RTX 5070 Ti against RTX 5080, one Cyborg says 9 GB against 8GB. The operator's ruling is
    /// that the title is right, so the cell follows it and nothing is left for review.
    /// </summary>
    [Theory]
    [InlineData(3, "Ekran Boyutu (inç)", "15,6 inç", "18 inç")]
    [InlineData(3, "Grafik Kartı", "GeForce RTX™ 4050", "GeForce RTX™ 5070 Ti")]
    [InlineData(3, "İşlemci Modeli", "Core™ i5", "Core™ Ultra 9")]
    [InlineData(3, "İşlemci (tr_TR)", "12500H", "275HX")]
    [InlineData(58, "Grafik Kartı", "GeForce RTX™ 4050", "GeForce RTX™ 3050")]
    [InlineData(501, "Grafik Kartı", "GeForce RTX™ 5070 Ti", "GeForce RTX™ 5080")]
    [InlineData(412, "RAM Bellek Boyutu", "9 GB", "8 GB")]
    public void ACellThatContradictsTheTitleIsRewrittenToTheTitlesValue(
        int rowNumber, string column, string was, string becomes)
    {
        var result = Clean().Single(r => r.RowNumber == rowNumber).Attributes.Single(a => a.Column == column);

        Assert.Equal(TitleAttributeStatus.Corrected, result.Status);
        Assert.Equal(was, result.OriginalValue);
        Assert.Equal(becomes, result.Value);
    }

    [Theory]
    [InlineData(3, "Predator18 RTX5070Ti 12GB QHD+ NHQVLEY002P307")]
    [InlineData(58, "Nitro V 15 RTX 3050 6GB FHD NH.QUBEY.001P311")]
    // The 8GB left over is the card's own; the RAM's (now 8 GB in the cell) is the one that goes.
    [InlineData(412, "Cyborg 15 RTX5060/8GB FHD B2RWFKG207XTRP302")]
    [InlineData(501, "RAIDER A16 HX RTX5080 16GB QHD+ B8WIPN349")]
    public void TheContradictingRowsAreNowCleanedToo(int rowNumber, string expected)
    {
        var row = Clean().Single(r => r.RowNumber == rowNumber);

        Assert.False(row.HasConflict);
        Assert.Equal(expected, row.CleanTitle);
    }

    [Fact]
    public void NoRowIsLeftForReview()
    {
        Assert.DoesNotContain(Clean(), r => r.HasConflict);
    }

    /// <summary>The switch needs a clear answer: a title naming two different values is still reported.</summary>
    [Fact]
    public void ATitleNamingTwoDifferentValuesIsStillAConflict()
    {
        var rules = new TitleRuleSet(
            "x", "Başlık",
            [
                new TitleAttributeRule("Grafik Kartı", TitleAttributeKind.Alias, Remove: false, TitleWins: true,
                    Aliases: [["RTX 3050"], ["RTX 4050"], ["RTX 5050"]]),
            ]);

        List<List<string>> table = [["Başlık", "Grafik Kartı"], ["Model RTX 3050 ve RTX 4050", "RTX 5050"]];
        var row = Assert.Single(TitleCleanBuilder.Clean(CompiledRuleSet.Compile(rules), table));

        Assert.Equal(TitleAttributeStatus.Conflict, row.Attributes[0].Status);
        Assert.Equal("RTX 5050", row.Attributes[0].Value);
    }

    // -----------------------------------------------------------------
    // The hand-off
    // -----------------------------------------------------------------

    /// <summary>
    /// The operator brings this set in through the rule editor's Excel import. What comes back out of
    /// that round trip is the set that was exported, so what was tested here is what runs there.
    /// </summary>
    [Fact]
    public void TheRuleSetSurvivesTheExcelImportUnchanged()
    {
        var bytes = TitleRuleStore.BuildWorkbook([Rules()]);

        using var stream = new MemoryStream(bytes);
        var imported = Assert.Single(TitleRuleStore.ReadWorkbook(stream, "kural-seti.xlsx"));

        Assert.Equal(Rules().Name, imported.Name);
        Assert.Equal(Rules().DecimalSeparator, imported.DecimalSeparator);

        var expected = TitleRuleStore.ToForm(Rules()).AttributeList;
        var actual = TitleRuleStore.ToForm(imported).AttributeList;
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i], actual[i]);

        Assert.Equal(
            Clean().Select(r => r.CleanTitle),
            TitleCleanBuilder.Clean(CompiledRuleSet.Compile(imported), Table()).Select(r => r.CleanTitle));
    }
}



