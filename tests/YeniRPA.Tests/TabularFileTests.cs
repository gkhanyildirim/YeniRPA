using System.Text;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The shared reading rules three modules join on: the order-number key, the day-first template
/// dates, and what counts as a tracking code.
/// </summary>
public class TabularFileTests
{
    /// <summary>
    /// A quote only opens a quoted field at the start of one. An inch mark mid-field is a literal
    /// character — it used to switch quoting on and swallow the rest of the line into that cell,
    /// which emptied every column after it without any error. A screen size is written exactly that
    /// way, and Title Cleaner reads screen sizes out of exactly that kind of column.
    /// </summary>
    [Fact]
    public void AnInchMarkMidFieldDoesNotSwallowTheRestOfTheLine()
    {
        var csv = "Başlık;Ekran Boyutu;Marka\nAcme Book 15.6\" Notebook;15.6\";Acme\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var table = TabularFile.Read(stream, "urunler.csv");

        Assert.Equal(["Acme Book 15.6\" Notebook", "15.6\"", "Acme"], table[1]);
    }

    /// <summary>A properly quoted field still works, including a doubled quote inside it.</summary>
    [Fact]
    public void AQuotedFieldStillCarriesItsDelimiterAndItsEscapedQuotes()
    {
        var csv = "A;B\n\"one;two\";\"say \"\"hi\"\"\"\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var table = TabularFile.Read(stream, "x.csv");

        Assert.Equal(["one;two", "say \"hi\""], table[1]);
    }

    [Theory]
    [InlineData("01259_321097726-A", "321097726")]
    [InlineData("01259_321097726-B", "321097726")]
    [InlineData("321097726", "321097726")]
    [InlineData(" 01259_321097726-A ", "321097726")]
    [InlineData("", "")]
    public void OrderCoreStripsTheMarketplacePrefixAndTheSellerSuffix(string full, string expected)
    {
        Assert.Equal(expected, TabularFile.OrderCore(full));
    }

    /// <summary>
    /// Both halves of the join have to reduce to the same key, or the report cannot read a status at
    /// all — which is exactly what went wrong before.
    /// </summary>
    [Fact]
    public void TheTemplateNumberAndTheOrdersNumberReduceToTheSameKey()
    {
        Assert.Equal(TabularFile.OrderCore("321097726"), TabularFile.OrderCore("01259_321097726-A"));
    }

    [Theory]
    [InlineData("12.08.2026", 2026, 8, 12)]   // month-first would read 8 December
    [InlineData("07.08.2026", 2026, 8, 7)]
    [InlineData("13.08.2026", 2026, 8, 13)]   // unambiguous either way
    [InlineData("12.08.2026 13:21", 2026, 8, 12)]
    [InlineData("2026-08-12", 2026, 8, 12)]
    public void TemplateDatesAreReadDayFirst(string text, int year, int month, int day)
    {
        var parsed = TabularFile.ParseDayFirstDate(text);
        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(year, month, day), parsed!.Value.Date);
    }

    [Fact]
    public void AnEmptyDateIsNoDate()
    {
        Assert.Null(TabularFile.ParseDayFirstDate(""));
        Assert.Null(TabularFile.ParseDayFirstDate("   "));
    }

    // The state is named rather than passed as the enum itself: TabularFile is internal, and a
    // public test method cannot take a less accessible parameter type.
    [Theory]
    [InlineData("", "Missing")]
    [InlineData("   ", "Missing")]
    [InlineData("NULL", "Missing")]
    [InlineData("null", "Missing")]
    [InlineData("106060305663", "Ok")]
    [InlineData("YK-12345", "Malformed")]
    public void OnlyDigitsCountAsATrackingCode(string raw, string expected)
    {
        Assert.Equal(expected, TabularFile.ReadTracking(raw).State.ToString());
    }

    [Theory]
    [InlineData("11616.0", "11616")]
    [InlineData("11616", "11616")]
    [InlineData(" 11616.0 ", "11616")]
    public void SellerIdsLoseTheFloatTailTheExportGivesThem(string raw, string expected)
    {
        Assert.Equal(expected, TabularFile.NormalizeSellerId(raw));
    }
}
