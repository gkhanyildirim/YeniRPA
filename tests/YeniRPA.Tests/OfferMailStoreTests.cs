using System.Text.Json;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The hand-entered addresses: the operator's answer to a seller the uploaded list does not name.
/// Getting the lookup wrong here means a seller who was answered stays unanswered, or worse, is
/// answered with a stale address.
///
/// <para>The twin of <see cref="VatMailStoreTests"/>. Both exist rather than one shared fixture for
/// the same reason the two stores do: the rules happen to agree today, and a change to one must fail
/// its own test rather than quietly pass on the other's.</para>
/// </summary>
public class OfferMailStoreTests
{
    static OfferOverrideEntry Entry(string id, string name, string email) => new(id, name, email);

    [Fact]
    public void AnAddressEnteredByHandIsFoundByTheSellersId()
    {
        var overrides = new[] { Entry("13193", "NEXA E-TİCARET", "info@nexaeticaret.com") };

        Assert.Equal("info@nexaeticaret.com", OfferMailStore.FindOverride(overrides, "13193", "Nexa"));
    }

    /// <summary>A row typed in without an id still has to reach its seller, and every human spelling
    /// of a Turkish name has to reach the same row.</summary>
    [Theory]
    [InlineData("VintageOnline")]
    [InlineData("VINTAGEONLINE")]
    [InlineData("vıntageonlıne")]
    public void ARowWithNoIdIsFoundByItsFoldedName(string spelling)
    {
        var overrides = new[] { Entry("", "VintageOnline", "leyla@example.com") };

        Assert.Equal("leyla@example.com", OfferMailStore.FindOverride(overrides, "", spelling));
    }

    /// <summary>Ids are what identify a row when there is one, so an address entered against an id is
    /// not reachable by a name that belongs to somebody else.</summary>
    [Fact]
    public void ARowCarryingAnIdIsNotReachableByNameAlone()
    {
        var overrides = new[] { Entry("13193", "Nexa", "info@nexa.com") };

        Assert.Null(OfferMailStore.FindOverride(overrides, "", "Nexa"));
    }

    /// <summary>
    /// Two rows for one seller can only come from a hand-edited file — saving collapses them. The
    /// later one is live, and <see cref="OfferMailStore.FindOverrideProblems"/> has to say the same
    /// thing, or the operator removes the wrong row.
    /// </summary>
    [Fact]
    public void WhenTwoRowsDescribeOneSellerTheLastOneIsLive()
    {
        var overrides = new[]
        {
            Entry("12552", "TonerCenter", "eski@example.com"),
            Entry("12552", "TonerCenter", "yeni@example.com")
        };

        Assert.Equal("yeni@example.com", OfferMailStore.FindOverride(overrides, "12552", "TonerCenter"));
        Assert.Contains(OfferMailStore.FindOverrideProblems(overrides), w => w.Contains("last is used"));
    }

    /// <summary>A row saved with the seller but no address yet is "seen but not finished". It must not
    /// shadow the uploaded list — the seller should still be matched there if they are in it.</summary>
    [Fact]
    public void ARowWithNoAddressYetIsNotAnAnswer()
    {
        Assert.Null(OfferMailStore.FindOverride([Entry("11806", "Proteldepo", "  ")], "11806", "Proteldepo"));
    }

    /// <summary>A seller with several people on the mail: one cell, one To line, no repeats.</summary>
    [Fact]
    public void SeveralAddressesInOneCellComeBackAsOneToLine()
    {
        var overrides = new[] { Entry("10700", "Nasa İletişim", "a@x.com, b@x.com; a@x.com") };

        Assert.Equal("a@x.com; b@x.com", OfferMailStore.FindOverride(overrides, "10700", "Nasa İletişim"));
    }

    /// <summary>A typo in a hand-entered address is caught before Outlook is asked to send to it, and
    /// the offending address is named rather than the whole cell.</summary>
    [Fact]
    public void AMangledAddressIsNamedRatherThanTheWholeRow()
    {
        var problems = OfferMailStore.FindOverrideProblems([Entry("1", "Karataş Online", "info@example.com; bilgi@")]);

        Assert.Contains(problems, w => w.Contains("bilgi@") && w.Contains("Karataş Online"));
    }

    [Fact]
    public void AHealthyListReportsNothing()
    {
        var overrides = new[]
        {
            Entry("13193", "Nexa", "info@nexa.com"),
            Entry("12552", "TonerCenter", "info@tonercenter.com")
        };

        Assert.Empty(OfferMailStore.FindOverrideProblems(overrides));
        Assert.Null(OfferMailStore.FindOverride(overrides, "99999", "Someone Else"));
    }

    // ---------------------------------------------------------------------
    // The minimum offer count
    // ---------------------------------------------------------------------

    /// <summary>
    /// Zero, a negative number and a blank box all mean "mail everybody". Collapsed to one value so no
    /// caller has to test for three — the one that forgot would refuse to mail anybody.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    public void AMinimumOfZeroOrLessIsNoMinimumAtAll(int? saved, int? expected)
    {
        Assert.Equal(expected, OfferMailStore.NormalizeMinimum(saved));
    }

    /// <summary>The threshold is the lever that brings a 287-seller run under the 250-mail limit, so a
    /// saved one has to survive the round trip it is written and read back through.</summary>
    [Fact]
    public void ASavedThresholdComesBackAsItWasWritten()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var written = new OfferMailFile(1, null, null, null, null, 40, null, null, []);

        var read = JsonSerializer.Deserialize<OfferMailFile>(JsonSerializer.Serialize(written, options), options);

        Assert.Equal(40, read?.MinOfferCount);
    }

    // ---------------------------------------------------------------------
    // The CC line
    // ---------------------------------------------------------------------

    /// <summary>Nobody typed anything, so nobody is copied. Not an error — the CC is optional.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ; , ")]
    public void AnEmptyCcCopiesNobodyAndIsNotAProblem(string? raw)
    {
        var (cc, problem) = OfferMailStore.NormalizeCc(raw);

        Assert.Null(cc);
        Assert.Null(problem);
    }

    /// <summary>Several people can be copied, written the way any other address cell in this app is
    /// written — and joined into the one line Outlook's CC field expects.</summary>
    [Theory]
    [InlineData("bilgi@sirket.com", "bilgi@sirket.com")]
    [InlineData("  bilgi@sirket.com  ", "bilgi@sirket.com")]
    [InlineData("a@x.com; b@x.com", "a@x.com; b@x.com")]
    [InlineData("a@x.com, b@x.com", "a@x.com; b@x.com")]
    [InlineData("a@x.com; A@X.com; b@x.com", "a@x.com; b@x.com")]
    public void TheCcIsSplitDeduplicatedAndJoinedBack(string raw, string expected)
    {
        var (cc, problem) = OfferMailStore.NormalizeCc(raw);

        Assert.Equal(expected, cc);
        Assert.Null(problem);
    }

    /// <summary>
    /// A typo is named rather than dropped. Dropping it would mail every seller with the copy going
    /// nowhere, and nothing on the screen would say so.
    /// </summary>
    [Fact]
    public void AMangledCcIsRefusedAndNamed()
    {
        var (cc, problem) = OfferMailStore.NormalizeCc("bilgi@sirket.com; kayit@");

        Assert.Null(cc);
        Assert.NotNull(problem);
        Assert.Contains("kayit@", problem);
    }
}
