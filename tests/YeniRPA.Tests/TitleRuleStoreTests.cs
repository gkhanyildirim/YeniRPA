using System.Text;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Tests;

/// <summary>
/// Storing and re-reading a naming standard. Rule sets are the only data in this module that cannot
/// be regenerated from an export — they are what the category team decided — so a round trip that
/// quietly drops a flag is worse than one that fails.
/// </summary>
public class TitleRuleStoreTests
{
    static readonly MeasureUnit Gb = new("GB", ["gb", "gigabayt"], 1);
    static readonly MeasureUnit Tb = new("TB", ["tb", "terabayt"], 1024);

    static TitleRuleSet Laptop() => new(
        "Laptop",
        "Başlık",
        [
            new TitleAttributeRule("Ürün Tipi"),
            new TitleAttributeRule("Sabit Disk Kapasitesi", TitleAttributeKind.Measure, Units: [Gb, Tb]),
            new TitleAttributeRule("Ekran Kartı", Remove: false, AllowPartial: true),
            new TitleAttributeRule("RAM", TitleAttributeKind.Measure, FillFromTitle: true, Units: [Gb]),
            new TitleAttributeRule("İşletim Sistemi", TitleAttributeKind.Alias,
                Correct: false,
                AllowSuffix: true,
                Aliases: [["W11P", "Windows 11 Pro"], ["W11H", "Windows 11 Home"]]),
        ],
        DecimalSeparator: ",");

    // -----------------------------------------------------------------
    // JSON
    // -----------------------------------------------------------------

    /// <summary>
    /// The dangerous default. <c>Remove</c> and <c>Correct</c> are <c>true</c> on the record, and a
    /// rule that leaves them out has to come back that way — if the serializer handed back
    /// <c>default(bool)</c> instead, every run would report success while cleaning nothing.
    /// </summary>
    [Fact]
    public void ARuleThatOmitsItsFlagsKeepsTheDeclaredDefaults()
    {
        var file = TitleRuleStore.Parse(
            """
            {
              "version": 1,
              "sets": [
                { "name": "Laptop", "titleColumn": "Başlık", "attributes": [ { "column": "RAM" } ] }
              ]
            }
            """);

        var rule = file.Sets.Single().AttributeList.Single();

        Assert.Equal("RAM", rule.Column);
        Assert.True(rule.Remove);
        Assert.True(rule.Correct);
        Assert.False(rule.FillFromTitle);
        Assert.Equal(TitleAttributeKind.Text, rule.Kind);
        Assert.Equal(".", file.Sets.Single().DecimalSeparator);
    }

    /// <summary>The kind is written as a word so the file stays readable and hand-editable.</summary>
    [Fact]
    public void TheKindIsStoredAsAWordNotANumber()
    {
        var json = TitleRuleStore.Serialize(new TitleRuleFile(1, null, [Laptop()]));

        Assert.Contains("\"Measure\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Alias\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AFullRuleSetSurvivesAJsonRoundTrip()
    {
        var round = TitleRuleStore.Parse(TitleRuleStore.Serialize(new TitleRuleFile(1, null, [Laptop()])));

        AssertSameAsLaptop(round.Sets.Single());
    }

    // -----------------------------------------------------------------
    // Excel
    // -----------------------------------------------------------------

    [Fact]
    public void AFullRuleSetSurvivesAnExcelRoundTrip()
    {
        var bytes = TitleRuleStore.BuildWorkbook([Laptop()]);

        using var stream = new MemoryStream(bytes);
        var read = TitleRuleStore.ReadWorkbook(stream, "kural-setleri.xlsx");

        AssertSameAsLaptop(Assert.Single(read));
    }

    /// <summary>Attribute order decides which of two attributes claims a stretch of title that both
    /// could match, so the sheet has to preserve it.</summary>
    [Fact]
    public void TheExcelRoundTripKeepsAttributeOrder()
    {
        var bytes = TitleRuleStore.BuildWorkbook([Laptop()]);

        using var stream = new MemoryStream(bytes);
        var read = TitleRuleStore.ReadWorkbook(stream, "kural-setleri.xlsx").Single();

        Assert.Equal(
            Laptop().AttributeList.Select(a => a.Column),
            read.AttributeList.Select(a => a.Column));
    }

    [Fact]
    public void SeveralRuleSetsShareOneSheetAndComeBackApart()
    {
        var phone = new TitleRuleSet("Telefon", "Ürün Adı",
            [new TitleAttributeRule("Marka"), new TitleAttributeRule("Batarya", TitleAttributeKind.Measure,
                Units: [new MeasureUnit("mAh", ["mah"])])]);

        var bytes = TitleRuleStore.BuildWorkbook([Laptop(), phone]);

        using var stream = new MemoryStream(bytes);
        var read = TitleRuleStore.ReadWorkbook(stream, "kural-setleri.xlsx");

        Assert.Equal(2, read.Count);
        Assert.Equal("Laptop", read[0].Name);
        Assert.Equal("Telefon", read[1].Name);
        Assert.Equal("Ürün Adı", read[1].TitleColumn);
        Assert.Equal("mAh", read[1].AttributeList[1].UnitList.Single().Canonical);
    }

    /// <summary>A hand-typed sheet carrying only the three columns that identify a rule still reads,
    /// with the record's own defaults filling the rest.</summary>
    [Fact]
    public void AMinimalHandTypedSheetStillReads()
    {
        var csv = new StringBuilder()
            .AppendLine("Kural Seti;Başlık Kolonu;Kolon")
            .AppendLine("Laptop;Başlık;Marka")
            .AppendLine("Laptop;Başlık;İşlemci")
            .ToString();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var set = TitleRuleStore.ReadWorkbook(stream, "kurallar.csv").Single();

        Assert.Equal("Başlık", set.TitleColumn);
        Assert.Equal(2, set.AttributeList.Count);
        Assert.True(set.AttributeList[0].Remove);
        Assert.True(set.AttributeList[0].Correct);
    }

    /// <summary>
    /// A sheet exported before the Alias type was relabelled — "Eşanlamlı" in the Tip cell, its
    /// groups under an "Eşanlamlılar" header. Both are optional columns, so a rename with no fallback
    /// would read this file without complaining and hand back a Text rule with an empty catalogue:
    /// every rule still there, every rule silently unable to see a conflict.
    /// </summary>
    [Fact]
    public void ASheetWrittenUnderTheOldAliasNamesStillReads()
    {
        var csv = new StringBuilder()
            .AppendLine("Kural Seti;Başlık Kolonu;Kolon;Tip;Eşanlamlılar")
            .AppendLine("Laptop;Başlık;İşletim Sistemi;Eşanlamlı;W11P|Windows 11 Pro")
            .ToString();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var rule = TitleRuleStore.ReadWorkbook(stream, "kurallar.csv").Single().AttributeList.Single();

        Assert.Equal(TitleAttributeKind.Alias, rule.Kind);
        Assert.Equal(["W11P", "Windows 11 Pro"], rule.AliasGroups.Single());
    }

    /// <summary>A sheet exported before the "Ek" column existed reads with the permission off — which
    /// is the safe default, and the behaviour that sheet was written under.</summary>
    [Fact]
    public void ASheetWithoutTheSuffixColumnReadsWithItOff()
    {
        var csv = new StringBuilder()
            .AppendLine("Kural Seti;Başlık Kolonu;Kolon;Tip")
            .AppendLine("Laptop;Başlık;Ürün Tipi;Değer Listesi")
            .ToString();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var rule = TitleRuleStore.ReadWorkbook(stream, "kurallar.csv").Single().AttributeList.Single();

        Assert.False(rule.AllowSuffix);
        Assert.False(rule.AllowPartial);
        Assert.Null(rule.ReferenceList);
    }

    /// <summary>
    /// A reference list is a name written in a cell, so it has to survive the trip out to a workbook
    /// and back — otherwise exporting a rule set and re-importing it silently drops the one thing that
    /// lets a column remove more than its own cell says.
    /// </summary>
    [Fact]
    public void AReferenceListNameSurvivesTheExcelRoundTrip()
    {
        var set = new TitleRuleSet("Laptop", "Başlık",
            [new TitleAttributeRule("İşlemci", ReferenceList: "İşlemciler")]);

        var bytes = TitleRuleStore.BuildWorkbook([set]);

        using var stream = new MemoryStream(bytes);
        var rule = TitleRuleStore.ReadWorkbook(stream, "kural-setleri.xlsx").Single().AttributeList.Single();

        Assert.Equal("İşlemciler", rule.ReferenceList);
    }

    /// <summary>
    /// "Tekrarı Sil" belongs to the set rather than to a rule, so it is written on every row of that
    /// set and read back off the first — the same shape the decimal separator has always had.
    /// </summary>
    [Fact]
    public void TheRepeatSettingSurvivesTheExcelRoundTrip()
    {
        var set = new TitleRuleSet("Laptop", "Başlık",
            [new TitleAttributeRule("Marka"), new TitleAttributeRule("İşlemci")],
            ".", CollapseRepeats: true);

        var bytes = TitleRuleStore.BuildWorkbook([set]);

        using var stream = new MemoryStream(bytes);
        Assert.True(TitleRuleStore.ReadWorkbook(stream, "kural-setleri.xlsx").Single().CollapseRepeats);
    }

    /// <summary>
    /// The setting rides on the rule set the browser posts with the upload, so it has to survive
    /// that JSON. It is a whole-set setting rather than a per-rule one, which is the shape most
    /// likely to be dropped somewhere along the wire.
    /// </summary>
    [Fact]
    public void TheRepeatSettingSurvivesThePostedRuleSet()
    {
        const string json = """
            {
              "name": "Laptop",
              "titleColumn": "Başlık",
              "decimalSeparator": ".",
              "collapseRepeats": true,
              "attributes": [{ "column": "Marka", "kind": "Text" }]
            }
            """;

        Assert.True(TitleRuleStore.ParseRuleSetForm(json).CollapseRepeats);
    }

    /// <summary>A sheet exported before the column existed reads with it off — the safe default, and
    /// the behaviour that sheet was written under.</summary>
    [Fact]
    public void ASheetWithoutTheRepeatColumnReadsWithItOff()
    {
        var csv = new StringBuilder()
            .AppendLine("Kural Seti;Başlık Kolonu;Kolon;Tip")
            .AppendLine("Laptop;Başlık;Marka;Metin")
            .ToString();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        Assert.False(TitleRuleStore.ReadWorkbook(stream, "kurallar.csv").Single().CollapseRepeats);
    }

    /// <summary>And an empty cell comes back as no list rather than as a list named nothing.</summary>
    [Fact]
    public void ARuleWithNoReferenceListComesBackWithNone()
    {
        var bytes = TitleRuleStore.BuildWorkbook([Laptop()]);

        using var stream = new MemoryStream(bytes);
        var read = TitleRuleStore.ReadWorkbook(stream, "kural-setleri.xlsx").Single();

        Assert.All(read.AttributeList, rule => Assert.Null(rule.ReferenceList));
    }

    /// <summary>The Tip cell is written in English and read back through the Turkish label the editor
    /// shows, so a sheet a category owner typed by hand reads the same as one the app exported.</summary>
    [Fact]
    public void TheTipCellAcceptsTheLabelTheEditorShows()
    {
        var csv = new StringBuilder()
            .AppendLine("Kural Seti;Başlık Kolonu;Kolon;Tip;Değerler")
            .AppendLine("Laptop;Başlık;İşletim Sistemi;Değer Listesi;W11P|Windows 11 Pro")
            .ToString();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var rule = TitleRuleStore.ReadWorkbook(stream, "kurallar.csv").Single().AttributeList.Single();

        Assert.Equal(TitleAttributeKind.Alias, rule.Kind);
        Assert.Equal(["W11P", "Windows 11 Pro"], rule.AliasGroups.Single());
    }

    [Fact]
    public void AFileWithoutTheColumnColumnSaysWhichColumnIsMissing()
    {
        var csv = "Kural Seti;Başlık Kolonu\nLaptop;Başlık\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var error = Assert.Throws<InvalidOperationException>(
            () => TitleRuleStore.ReadWorkbook(stream, "kurallar.csv"));

        Assert.Contains("Kolon", error.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // Ready-made unit sets
    // -----------------------------------------------------------------

    public static TheoryData<string> UnitFamilies()
    {
        var data = new TheoryData<string>();
        foreach (var family in TitleRuleSuggester.Families)
            data.Add(family.Label);

        return data;
    }

    /// <summary>
    /// Every family the editor offers has to survive being encoded into a cell and read back out.
    /// Encoding and parsing are two separate functions, and the picker hands the operator a string
    /// it did not write itself — if one drifts from the other, choosing a set from the list produces
    /// a rule that cannot be saved, or worse, one that saves with the wrong factors.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnitFamilies))]
    public void EveryReadyMadeUnitSetSurvivesTheCellFormat(string label)
    {
        var family = TitleRuleSuggester.Families.Single(f => f.Label == label);

        var set = TitleRuleStore.FromForm(new TitleRuleSetForm(
            "Test", "Başlık",
            [new TitleAttributeForm("Ölçü", "Measure", Units: TitleRuleStore.EncodeUnits(family.Units))]));

        // Compiling is what the editor does before it lets the set be saved, and it is what throws
        // "names no unit" on a measured rule whose units did not survive the round trip.
        CompiledRuleSet.Compile(set);

        var read = set.AttributeList.Single().UnitList;

        Assert.Equal(family.Units.Select(u => u.Canonical), read.Select(u => u.Canonical));
        Assert.Equal(family.Units.Select(u => u.Factor), read.Select(u => u.Factor));
        Assert.Equal(family.Units.Select(u => u.Spellings), read.Select(u => u.Spellings));
    }

    // -----------------------------------------------------------------
    // The editor's flattened shape
    // -----------------------------------------------------------------

    /// <summary>
    /// The browser never encodes units or alias groups itself — a second implementation of the
    /// format, in a second language, free to drift from this one. It edits the flattened strings and
    /// the server folds them back, so this round trip has to be lossless.
    /// </summary>
    [Fact]
    public void AFullRuleSetSurvivesTheEditorRoundTrip()
    {
        AssertSameAsLaptop(TitleRuleStore.FromForm(TitleRuleStore.ToForm(Laptop())));
    }

    /// <summary>
    /// One entry per line, because the editor's box is where forty alias groups have to be read and
    /// on one line they cannot be. The workbook keeps ";" — a cell is one line — and the parser takes
    /// either, so there is still one implementation of the format rather than one per destination.
    /// </summary>
    [Fact]
    public void TheEditorShapeFlattensUnitsAndAliasesOnePerLine()
    {
        var form = TitleRuleStore.ToForm(Laptop());

        var storage = form.AttributeList.First(a => a.Column == "Sabit Disk Kapasitesi");
        Assert.Equal("GB=gb|gigabayt@1\nTB=tb|terabayt@1024", storage.Units);

        var os = form.AttributeList.First(a => a.Column == "İşletim Sistemi");
        Assert.Equal("W11P|Windows 11 Pro\nW11H|Windows 11 Home", os.Aliases);
        Assert.Equal("Alias", os.Kind);
        Assert.False(os.Correct);
    }

    /// <summary>The workbook stays on ";" — one cell, one line — however the editor shows it.</summary>
    [Fact]
    public void TheWorkbookStillWritesOneCellPerRule()
    {
        var bytes = TitleRuleStore.BuildWorkbook([Laptop()]);

        using var stream = new MemoryStream(bytes);
        var read = TitleRuleStore.ReadWorkbook(stream, "kural-setleri.xlsx").Single();

        AssertSameAsLaptop(read);
    }

    /// <summary>
    /// Both separators reach the same rule set. A workbook written before the editor moved to lines
    /// still reads, and a value typed with ";" by hand keeps working.
    /// </summary>
    [Fact]
    public void LinesAndSemicolonsParseToTheSameThing()
    {
        TitleRuleSet Read(string units, string aliases) => TitleRuleStore.FromForm(
            new TitleRuleSetForm("Test", "Başlık",
            [
                new TitleAttributeForm("Ölçü", "Measure", Units: units),
                new TitleAttributeForm("Liste", "Alias", Aliases: aliases),
            ]));

        var lines = Read("GB=gb@1\nTB=tb@1024", "W11P|Windows 11 Pro\nW11H|Windows 11 Home");
        var semis = Read("GB=gb@1 ; TB=tb@1024", "W11P|Windows 11 Pro ; W11H|Windows 11 Home");

        Assert.Equal(
            TitleRuleStore.EncodeUnits(semis.AttributeList[0].UnitList),
            TitleRuleStore.EncodeUnits(lines.AttributeList[0].UnitList));

        Assert.Equal(
            TitleRuleStore.EncodeAliases(semis.AttributeList[1].AliasGroups),
            TitleRuleStore.EncodeAliases(lines.AttributeList[1].AliasGroups));

        Assert.Equal(2, lines.AttributeList[0].UnitList.Count);
        Assert.Equal(2, lines.AttributeList[1].AliasGroups.Count);
    }

    /// <summary>A row typed into the editor with the optional cells left blank is a plain text
    /// attribute that is removed and corrected — the same defaults as everywhere else.</summary>
    [Fact]
    public void AnEditorRowWithBlankCellsBecomesAPlainTextAttribute()
    {
        var set = TitleRuleStore.FromForm(
            new TitleRuleSetForm("Test", "Başlık", [new TitleAttributeForm("Marka")]));

        var rule = set.AttributeList.Single();

        Assert.Equal(TitleAttributeKind.Text, rule.Kind);
        Assert.True(rule.Remove);
        Assert.True(rule.Correct);
        Assert.Empty(rule.UnitList);
        Assert.Empty(rule.AliasGroups);
    }

    /// <summary>A row with no column name is dropped rather than saved as an unrunnable rule — the
    /// editor's "add row" leaves one behind whenever someone changes their mind.</summary>
    [Fact]
    public void AnEditorRowWithNoColumnIsDropped()
    {
        var set = TitleRuleStore.FromForm(new TitleRuleSetForm(
            "Test", "Başlık", [new TitleAttributeForm("Marka"), new TitleAttributeForm("  ")]));

        Assert.Single(set.AttributeList);
    }

    // -----------------------------------------------------------------

    /// <summary>A rule set that has been through a round trip still cleans the reference title the
    /// same way — the check that matters more than any field-by-field comparison.</summary>
    static void AssertSameAsLaptop(TitleRuleSet read)
    {
        var original = Laptop();

        Assert.Equal(original.Name, read.Name);
        Assert.Equal(original.TitleColumn, read.TitleColumn);
        Assert.Equal(",", read.DecimalSeparator);
        Assert.Equal(original.AttributeList.Count, read.AttributeList.Count);

        for (var i = 0; i < original.AttributeList.Count; i++)
        {
            var want = original.AttributeList[i];
            var got = read.AttributeList[i];

            Assert.Equal(want.Column, got.Column);
            Assert.Equal(want.Kind, got.Kind);
            Assert.Equal(want.Remove, got.Remove);
            Assert.Equal(want.Correct, got.Correct);
            Assert.Equal(want.FillFromTitle, got.FillFromTitle);
            Assert.Equal(want.AllowSuffix, got.AllowSuffix);
            Assert.Equal(want.AllowPartial, got.AllowPartial);

            Assert.Equal(
                want.UnitList.Select(u => (u.Canonical, u.Factor)),
                got.UnitList.Select(u => (u.Canonical, u.Factor)));

            Assert.Equal(
                want.UnitList.SelectMany(u => u.Spellings ?? []),
                got.UnitList.SelectMany(u => u.Spellings ?? []));

            Assert.Equal(
                want.AliasGroups.Select(g => string.Join("|", g)),
                got.AliasGroups.Select(g => string.Join("|", g)));
        }
    }
}
