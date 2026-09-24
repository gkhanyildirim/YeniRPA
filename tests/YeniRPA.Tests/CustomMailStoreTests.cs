using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// How a hand-entered address is matched to a seller and reported back — the same rules
/// <see cref="OfferMailStoreTests"/> covers for <see cref="OfferMailStore"/>, since
/// <see cref="CustomMailStore.FindOverride"/>/<see cref="CustomMailStore.FindOverrideProblems"/>
/// mirror it exactly, keyed on <see cref="CustomMailSellerListReader.SellerKey"/> instead.
/// </summary>
public class CustomMailStoreTests
{
    static CustomMailOverrideEntry Entry(string id, string name, string email) => new(id, name, email);

    [Fact]
    public void AnAddressEnteredByHandIsFoundByTheSellersId()
    {
        var overrides = new[] { Entry("13193", "NEXA E-TİCARET", "info@nexaeticaret.com") };

        Assert.Equal("info@nexaeticaret.com", CustomMailStore.FindOverride(overrides, "13193", "Nexa"));
    }

    [Theory]
    [InlineData("VintageOnline")]
    [InlineData("VINTAGEONLINE")]
    [InlineData("vıntageonlıne")]
    public void ARowWithNoIdIsFoundByItsFoldedName(string spelling)
    {
        var overrides = new[] { Entry("", "VintageOnline", "leyla@example.com") };

        Assert.Equal("leyla@example.com", CustomMailStore.FindOverride(overrides, "", spelling));
    }

    [Fact]
    public void ARowCarryingAnIdIsNotReachableByNameAlone()
    {
        var overrides = new[] { Entry("13193", "Nexa", "info@nexa.com") };

        Assert.Null(CustomMailStore.FindOverride(overrides, "", "Nexa"));
    }

    [Fact]
    public void WhenTwoRowsDescribeOneSellerTheLastOneIsLive()
    {
        var overrides = new[]
        {
            Entry("12552", "TonerCenter", "eski@example.com"),
            Entry("12552", "TonerCenter", "yeni@example.com")
        };

        Assert.Equal("yeni@example.com", CustomMailStore.FindOverride(overrides, "12552", "TonerCenter"));
        Assert.Contains(CustomMailStore.FindOverrideProblems(overrides), w => w.Contains("last is used"));
    }

    [Fact]
    public void ARowWithNoAddressYetIsNotAnAnswer()
    {
        Assert.Null(CustomMailStore.FindOverride([Entry("11806", "Proteldepo", "  ")], "11806", "Proteldepo"));
    }

    [Fact]
    public void SeveralAddressesInOneCellComeBackAsOneToLine()
    {
        var overrides = new[] { Entry("10700", "Nasa İletişim", "a@x.com, b@x.com; a@x.com") };

        Assert.Equal("a@x.com; b@x.com", CustomMailStore.FindOverride(overrides, "10700", "Nasa İletişim"));
    }

    [Fact]
    public void AMangledAddressIsNamedRatherThanTheWholeRow()
    {
        var problems = CustomMailStore.FindOverrideProblems([Entry("1", "Karataş Online", "info@example.com; bilgi@")]);

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

        Assert.Empty(CustomMailStore.FindOverrideProblems(overrides));
        Assert.Null(CustomMailStore.FindOverride(overrides, "99999", "Someone Else"));
    }
}
