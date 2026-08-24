using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The hand-entered addresses: the operator's answer to a seller the uploaded list does not name.
/// Getting the lookup wrong here means a seller who was answered stays unanswered, or worse, is
/// answered with a stale address.
/// </summary>
public class VatMailStoreTests
{
    static VatOverrideEntry Entry(string id, string name, string email) => new(id, name, email);

    [Fact]
    public void AnAddressEnteredByHandIsFoundByTheSellersId()
    {
        var overrides = new[] { Entry("13193", "NEXA E-TİCARET", "info@nexaeticaret.com") };

        Assert.Equal("info@nexaeticaret.com", VatMailStore.FindOverride(overrides, "13193", "Nexa"));
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

        Assert.Equal("leyla@example.com", VatMailStore.FindOverride(overrides, "", spelling));
    }

    /// <summary>Ids are what identify a row when there is one, so an address entered against an id is
    /// not reachable by a name that belongs to somebody else.</summary>
    [Fact]
    public void ARowCarryingAnIdIsNotReachableByNameAlone()
    {
        var overrides = new[] { Entry("13193", "Nexa", "info@nexa.com") };

        Assert.Null(VatMailStore.FindOverride(overrides, "", "Nexa"));
    }

    /// <summary>
    /// Two rows for one seller can only come from a hand-edited file — saving collapses them. The
    /// later one is live, and <see cref="VatMailStore.FindOverrideProblems"/> has to say the same
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

        Assert.Equal("yeni@example.com", VatMailStore.FindOverride(overrides, "12552", "TonerCenter"));
        Assert.Contains(VatMailStore.FindOverrideProblems(overrides), w => w.Contains("last is used"));
    }

    /// <summary>A row saved with the seller but no address yet is "seen but not finished". It must not
    /// shadow the uploaded list — the seller should still be matched there if they are in it.</summary>
    [Fact]
    public void ARowWithNoAddressYetIsNotAnAnswer()
    {
        Assert.Null(VatMailStore.FindOverride([Entry("11806", "Proteldepo", "  ")], "11806", "Proteldepo"));
    }

    /// <summary>A seller with several people on the mail: one cell, one To line, no repeats.</summary>
    [Fact]
    public void SeveralAddressesInOneCellComeBackAsOneToLine()
    {
        var overrides = new[] { Entry("10700", "Nasa İletişim", "a@x.com, b@x.com; a@x.com") };

        Assert.Equal("a@x.com; b@x.com", VatMailStore.FindOverride(overrides, "10700", "Nasa İletişim"));
    }

    /// <summary>A typo in a hand-entered address is caught before Outlook is asked to send to it, and
    /// the offending address is named rather than the whole cell.</summary>
    [Fact]
    public void AMangledAddressIsNamedRatherThanTheWholeRow()
    {
        var problems = VatMailStore.FindOverrideProblems([Entry("1", "Karataş Online", "info@example.com; bilgi@")]);

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

        Assert.Empty(VatMailStore.FindOverrideProblems(overrides));
        Assert.Null(VatMailStore.FindOverride(overrides, "99999", "Someone Else"));
    }
}
