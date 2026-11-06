using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// The "Anakart" rule set run end to end over the seller's own 220-row motherboard export
/// (<c>anakart.xlsx</c>).
///
/// <para>Unlike the laptop files, <b>every attribute cell in this file is empty</b> — the marketplace
/// rejected it for eleven missing mandatory attributes on 140 rows — so the rule set has two jobs: cut
/// the title down to the model name, and fill the cells out of what the title (and the research the
/// rule set carries) says.</para>
///
/// <para>The rule set is the JSON file the operator's copy was built from
/// (<c>anakart-kural-seti.json</c>, in this project's <c>samples/</c>), read the way the editor's own
/// import reads it. Pinning that file, not a hand-copy of it in C#, is what makes these tests a
/// statement about the set that is actually saved.</para>
///
/// <para>Four rows are not motherboards at all — three memory modules and a mining board — and the
/// operator asked that they are left exactly as they are.</para>
/// </summary>
public class AnakartRealFileTests
{
    const string FileName = "anakart.xlsx";
    static readonly int[] NotMotherboards = [19, 20, 87, 221];

    static readonly string[] Mandatory =
    [
        "Marka", "Desteklenen hafıza tipi", "Renk (temel)", "Ürün Tipi (tr_TR)",
        "Renk (Üreticiye Göre) (tr_TR)", "Chipset (tr_TR)", "Desteklenen İşlemci modelleri (tr_TR)",
        "İşlemci soket tipi (tr_TR)", "Anakart Form Tipi (tr_TR)", "Anakart Üreticisi (tr_TR)",
        "Kutu İçeriği (tr_TR)",
    ];

    static TitleRuleSet Rules()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "anakart-kural-seti.json");
        Assert.True(File.Exists(path), $"The rule set file is missing from the test output: {path}");

        var file = TitleRuleStore.FromForm(TitleRuleStore.ParseFileForm(File.ReadAllText(path)));
        return Assert.Single(file.Sets, s => s.Name == "Anakart");
    }

    static List<List<string>> Table()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", FileName);
        Assert.True(File.Exists(path), $"The sample workbook is missing from the test output: {path}");

        using var stream = File.OpenRead(path);
        return TabularFile.Read(stream, FileName);
    }

    static IReadOnlyList<TitleCleanRow> Clean(List<List<string>>? table = null) =>
        TitleCleanBuilder.Clean(CompiledRuleSet.Compile(Rules()), table ?? Table());

    /// <summary>What a column holds once the run is done, however it got there.</summary>
    static string Cell(TitleCleanRow row, string column)
    {
        var a = row.Attributes.Single(x => x.Column == column);
        return a.Status is TitleAttributeStatus.Filled or TitleAttributeStatus.Corrected ? a.Value : a.OriginalValue;
    }

    static string[] Tokens(string title) => title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // -----------------------------------------------------------------
    // The titles
    // -----------------------------------------------------------------

    /// <summary>One row per way this file writes a title. The team's rule: only the chipset, the memory type and
    /// the form factor leave the title (their cells take them); brand, socket, speeds, ports and USB stay.</summary>
    [Theory]
    // The team's own example, word for word
    [InlineData(3, "PRO B550M-B AM4 4600(OC) HDMI DP 2X M.2 USB 5GBPS")]
    [InlineData(57, "PRO B550M-B 4600MHZ 1XHDMI 2XM.2 AM4")]
    [InlineData(4, "A620M S2H 6400MHZ 1XVGA 1XHDMI 1XDP 1XM.2 USB 3.2 AM5")]
    [InlineData(45, "PRO B850M-P WIFI AM5 8200MHZOC HDMI DP 3X M.2 USB 10GBS 5G LAN WI-FI 7")]
    // Every title is in capitals, whichever way the seller typed it; AMD/Ryzen, ANAKART, SOKET and the compatibility sentence go
    [InlineData(9, "PRIME H810M-E CSM 1851P HDMI DP")]
    [InlineData(49, "ROG STRIX X870E-E GAMING WIFI AM5 TYPE-C")]
    // The maker's own SKU codes that look like a memory type stay
    [InlineData(89, "PRIME H610M-R D4-SI 3200(OC) HDMI VGA 1700P")]
    [InlineData(124, "PRIME H610M-E D4-CSM 3200MHZ DP HDMI M.2 1700P")]
    // The chipset written out beside its maker goes; the one inside the model code stays
    [InlineData(16, "TUF GAMING Z890-PRO WIFI")]
    [InlineData(6, "B550 GAMING WIFI6 AM4")]
    [InlineData(112, "B550-A PRO 4400OCMHZ HDMI / DP AM4")]
    [InlineData(61, "PRO B760M-P 4800(OC) HDMI DP M.2 1700P")]
    [InlineData(118, "MPG B650I EDGE WIFI HDMI M.2 USB 3.2 LAN WİFİ AM5")]
    public void EachTitleShapeKeepsEverythingButChipsetMemoryAndForm(int rowNumber, string expected)
    {
        var row = Clean().Single(r => r.RowNumber == rowNumber);

        Assert.Equal(expected, row.CleanTitle);
        Assert.False(row.HasConflict);
    }

    [Fact]
    public void EveryCleanedTitleIsTheOriginalMinusWholeWordsInTheSameOrder()
    {
        foreach (var row in Clean().Where(r => !NotMotherboards.Contains(r.RowNumber)))
        {
            var original = Tokens(row.OriginalTitle).Select(w => w.ToUpperInvariant()).ToArray();
            var next = 0;

            foreach (var word in Tokens(row.CleanTitle))
            {
                while (next < original.Length && !string.Equals(original[next], word, StringComparison.OrdinalIgnoreCase))
                    next++;

                Assert.True(next < original.Length, $"row {row.RowNumber}: '{word}' is not a word of the original, or moved: {row.CleanTitle}");
                next++;
            }

            Assert.True(row.CleanTitle.Length <= row.OriginalTitle.Trim().Length, $"row {row.RowNumber} grew");
        }
    }

    /// <summary>The three words the team names are the only ones that leave, whichever way they are written.</summary>
    [Fact]
    public void NoMemoryTypeOrFormWordIsLeftInAnyTitle()
    {
        var banned = new[] { "DDR4", "DDR5", "MATX", "M-ATX", "MICRO-ATX", "MINI-ITX", "ANAKART-ATX" };

        foreach (var row in Clean().Where(r => !NotMotherboards.Contains(r.RowNumber)))
        {
            var words = Tokens(row.CleanTitle);
            Assert.DoesNotContain(words, w => banned.Contains(w, StringComparer.OrdinalIgnoreCase));
        }
    }
    /// <summary>Words the cells already hold, or that say nothing about the board, never stay.</summary>
    [Fact]
    public void NoProcessorMakerSocketLabelProductTypeOrCompatibilitySentenceIsLeft()
    {
        var banned = new[] { "AMD", "INTEL", "RYZEN", "SOKET", "ANAKART", "UYUMLU)", "UYUMLU", "SERİLERİ", "SERİSİ", "NESİL", "BEYAZ" };

        foreach (var row in Clean().Where(r => !NotMotherboards.Contains(r.RowNumber)))
        {
            Assert.DoesNotContain(Tokens(row.CleanTitle), w => banned.Contains(w, StringComparer.OrdinalIgnoreCase));
            Assert.Equal(row.CleanTitle, row.CleanTitle.ToUpper(System.Globalization.CultureInfo.GetCultureInfo("en-US")));
        }
    }
    // -----------------------------------------------------------------
    // The cells
    // -----------------------------------------------------------------

    [Fact]
    public void EveryMotherboardGetsAllElevenMandatoryAttributes()
    {
        var rows = Clean().Where(r => !NotMotherboards.Contains(r.RowNumber)).ToList();
        Assert.Equal(216, rows.Count);

        foreach (var row in rows)
        {
            foreach (var column in Mandatory)
                Assert.True(Cell(row, column).Trim().Length > 0, $"row {row.RowNumber}: '{column}' is still empty");
        }
    }

    /// <summary>The marketplace refuses a value it does not list: memory, colour and brand come from
    /// its ReferenceData sheet, so a cell can never be a made-up spelling.</summary>
    [Fact]
    public void EveryListedAttributeUsesAValueTheMarketplaceAccepts()
    {
        foreach (var row in Clean().Where(r => !NotMotherboards.Contains(r.RowNumber)))
        {
            Assert.Contains(Cell(row, "Desteklenen hafıza tipi"), new[] { "DDR4", "DDR5" });
            Assert.Contains(Cell(row, "Renk (temel)"), new[] { "Siyah", "Beyaz" });
            Assert.Contains(Cell(row, "Marka"), new[] { "ASUS", "MSI", "GIGABYTE", "COLORFUL" });
            Assert.Contains(Cell(row, "İşlemci Markası"), new[] { "AMD", "Intel®" });
        }
    }

    [Theory]
    [InlineData(3, "MSI", "B550", "AM4", "DDR4", "Micro-ATX", "AMD Ryzen 5000/4000/3000 Series", "AMD")]
    [InlineData(4, "GIGABYTE", "A620", "AM5", "DDR5", "Micro-ATX", "AMD Ryzen 9000/8000/7000 Series", "AMD")]
    [InlineData(5, "GIGABYTE", "B760", "LGA1700", "DDR5", "Micro-ATX", "Intel Core 12th, 13th and 14th Gen", "Intel®")]
    [InlineData(9, "ASUS", "H810", "LGA1851", "DDR5", "Micro-ATX", "Intel Core Ultra 200S Series", "Intel®")]
    [InlineData(16, "ASUS", "Z890", "LGA1851", "DDR5", "ATX", "Intel Core Ultra 200S Series", "Intel®")]
    [InlineData(134, "COLORFUL", "B650", "AM5", "DDR5", "Micro-ATX", "AMD Ryzen 9000/8000/7000 Series", "AMD")]
    [InlineData(118, "MSI", "B650", "AM5", "DDR5", "Mini-ITX", "AMD Ryzen 9000/8000/7000 Series", "AMD")]
    // No DDR word at all in the title: AM5 only ever takes DDR5
    [InlineData(99, "ASUS", "A620", "AM5", "DDR5", "Micro-ATX", "AMD Ryzen 9000/8000/7000 Series", "AMD")]
    public void TheCellsAreResearchedFromTheTitleAndTheModelCode(
        int rowNumber, string brand, string chipset, string socket, string memory, string form,
        string processors, string processorBrand)
    {
        var row = Clean().Single(r => r.RowNumber == rowNumber);

        Assert.Equal(brand, Cell(row, "Marka"));
        Assert.Equal(brand, Cell(row, "Anakart Üreticisi (tr_TR)"));
        Assert.Equal(chipset, Cell(row, "Chipset (tr_TR)"));
        Assert.Equal(socket, Cell(row, "İşlemci soket tipi (tr_TR)"));
        Assert.Equal(memory, Cell(row, "Desteklenen hafıza tipi"));
        Assert.Equal(form, Cell(row, "Anakart Form Tipi (tr_TR)"));
        Assert.Equal(processors, Cell(row, "Desteklenen İşlemci modelleri (tr_TR)"));
        Assert.Equal(processorBrand, Cell(row, "İşlemci Markası"));
        Assert.Equal("Mainboard", Cell(row, "Ürün Tipi (tr_TR)"));
        Assert.Equal("Mainboard", Cell(row, "Kutu İçeriği (tr_TR)"));
    }

    /// <summary>The team's reference files decide how a value is written: chipset, memory type and
    /// form factor can only ever be a normalised value out of them.</summary>
    [Fact]
    public void ChipsetMemoryAndFormAreAlwaysTheReferenceFilesNormalisedValue()
    {
        static HashSet<string> Normalised(string file)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "samples", file);
            Assert.True(File.Exists(path), $"The reference workbook is missing from the test output: {path}");

            using var stream = File.OpenRead(path);
            return TabularFile.Read(stream, file).Skip(1).Select(r => r[0].Trim()).Where(v => v.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
        }

        var chipsets = Normalised("anakart-chipset-referans.xlsx");
        var memory = Normalised("anakart-hafiza-referans.xlsx");
        var forms = Normalised("anakart-form-referans.xlsx");

        foreach (var row in Clean().Where(r => !NotMotherboards.Contains(r.RowNumber)))
        {
            Assert.Contains(Cell(row, "Chipset (tr_TR)"), chipsets);
            Assert.Contains(Cell(row, "Desteklenen hafıza tipi"), memory);
            Assert.Contains(Cell(row, "Anakart Form Tipi (tr_TR)"), forms);
        }
    }

    /// <summary>The socket decides the memory: no AM5 or LGA1851 board takes DDR4, no AM4 board takes DDR5.</summary>
    [Fact]
    public void TheMemoryTypeAgreesWithTheSocketOnEveryRow()
    {
        foreach (var row in Clean().Where(r => !NotMotherboards.Contains(r.RowNumber)))
        {
            var memory = Cell(row, "Desteklenen hafıza tipi");

            switch (Cell(row, "İşlemci soket tipi (tr_TR)"))
            {
                case "AM5" or "LGA1851":
                    Assert.Equal("DDR5", memory);
                    break;
                case "AM4":
                    Assert.Equal("DDR4", memory);
                    break;
            }
        }
    }

    /// <summary>"B760M" is a micro-ATX board whatever the seller wrote after it.</summary>
    [Theory]
    [InlineData(23)]
    [InlineData(61)]
    [InlineData(164)]
    public void TheModelCodeOutranksATitleThatSaysATX(int rowNumber)
    {
        var row = Clean().Single(r => r.RowNumber == rowNumber);

        Assert.Equal("Micro-ATX", Cell(row, "Anakart Form Tipi (tr_TR)"));
        Assert.DoesNotContain("ATX", Tokens(row.CleanTitle));
    }

    /// <summary>Colour is researched by series, and only these are not black. They are the boards the
    /// operator is told to check.</summary>
    [Fact]
    public void OnlyTheWhiteBoardsAreWhite()
    {
        var white = Clean().Where(r => Cell(r, "Renk (temel)") == "Beyaz").Select(r => r.RowNumber).Order().ToList();

        Assert.Equal([71, 84, 165, 168, 213], white);
        Assert.All(Clean().Where(r => white.Contains(r.RowNumber)), r =>
            Assert.Equal("Beyaz", Cell(r, "Renk (Üreticiye Göre) (tr_TR)")));
    }

    // -----------------------------------------------------------------
    // What is left alone
    // -----------------------------------------------------------------

    [Fact]
    public void TheFourRowsThatAreNotMotherboardsAreLeftExactlyAsTheyAre()
    {
        foreach (var row in Clean().Where(r => NotMotherboards.Contains(r.RowNumber)))
        {
            Assert.Equal(row.OriginalTitle.Trim(), row.CleanTitle);
            Assert.All(row.Attributes, a =>
            {
                Assert.Equal(TitleAttributeStatus.Empty, a.Status);
                Assert.Equal("", a.Value);
            });
        }
    }

    /// <summary>The file goes back to the marketplace and is cleaned again after an edit: the second
    /// pass over the output must find nothing more to do.</summary>
    [Fact]
    public void ASecondPassOverTheOutputChangesNothing()
    {
        var table = Table();
        var first = Clean(table);

        var header = TabularFile.BuildHeaderIndex(table[0]);
        foreach (var row in first)
        {
            table[row.RowNumber - 1][header["Başlık"]] = row.CleanTitle;

            foreach (var a in row.Attributes.Where(a => a.Status == TitleAttributeStatus.Filled))
                table[row.RowNumber - 1][header[a.Column]] = a.Value;
        }

        var second = Clean(table);

        Assert.Equal(first.Select(r => r.CleanTitle), second.Select(r => r.CleanTitle));
        Assert.All(second, r => Assert.DoesNotContain(r.Attributes, a => a.Status == TitleAttributeStatus.Filled));
    }

    // -----------------------------------------------------------------
    // The hand-off
    // -----------------------------------------------------------------

    [Fact]
    public void TheRuleSetSurvivesTheExcelImportUnchanged()
    {
        using var stream = new MemoryStream(TitleRuleStore.BuildWorkbook([Rules()]));
        var imported = Assert.Single(TitleRuleStore.ReadWorkbook(stream, "kural-seti.xlsx"));

        var expected = TitleRuleStore.ToForm(Rules());
        var actual = TitleRuleStore.ToForm(imported);

        Assert.Equal(expected.UpperCase, actual.UpperCase);
        Assert.Equal(expected.AttributeList, actual.AttributeList);

        Assert.Equal(
            Clean().Select(r => r.CleanTitle),
            TitleCleanBuilder.Clean(CompiledRuleSet.Compile(imported), Table()).Select(r => r.CleanTitle));
    }
}
