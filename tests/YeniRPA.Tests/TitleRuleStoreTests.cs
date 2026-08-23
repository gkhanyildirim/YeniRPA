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
            new TitleAttributeRule("Ekran Kartı", Remove: false),
            new TitleAttributeRule("RAM", TitleAttributeKind.Measure, FillFromTitle: true, Units: [Gb]),
            new TitleAttributeRule("İşletim Sistemi", TitleAttributeKind.Alias,
                Correct: false,
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

    [Fact]
    public void TheEditorShapeFlattensUnitsAndAliasesIntoOneCellEach()
    {
        var form = TitleRuleStore.ToForm(Laptop());

        var storage = form.AttributeList.First(a => a.Column == "Sabit Disk Kapasitesi");
        Assert.Equal("GB=gb|gigabayt@1 ; TB=tb|terabayt@1024", storage.Units);

        var os = form.AttributeList.First(a => a.Column == "İşletim Sistemi");
        Assert.Equal("W11P|Windows 11 Pro ; W11H|Windows 11 Home", os.Aliases);
        Assert.Equal("Alias", os.Kind);
        Assert.False(os.Correct);
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
