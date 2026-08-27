using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// How an address cell is read, everywhere in the app. Both warning modules build their To lines
/// through these three functions, so a change here reaches every mail either of them sends.
/// </summary>
public class SellerMailStoreTests
{
    [Fact]
    public void SemicolonsAndCommasBothSeparateRecipients()
    {
        Assert.Equal(
            ["a@x.com", "b@x.com", "c@x.com"],
            SellerMailStore.SplitAddresses(" a@x.com ; b@x.com , c@x.com "));
    }

    /// <summary>A repeat inside one cell would put the same person in the To line twice.</summary>
    [Fact]
    public void ARepeatWithinOneCellIsDropped()
    {
        Assert.Equal(["a@x.com", "b@x.com"], SellerMailStore.SplitAddresses("a@x.com; b@x.com; A@X.COM"));
    }

    /// <summary>Order is the back office's own, which is the only ordering both sides can agree on —
    /// and the send guard compares the two lists in sequence.</summary>
    [Fact]
    public void OrderIsPreserved()
    {
        Assert.Equal(["z@x.com", "a@x.com"], SellerMailStore.SplitAddresses("z@x.com;a@x.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;")]
    [InlineData(null)]
    public void AnEmptyCellHasNoRecipients(string? raw)
    {
        Assert.Empty(SellerMailStore.SplitAddresses(raw));
    }

    [Fact]
    public void SplittingAndJoiningRoundTrips()
    {
        const string cell = "a@x.com; b@x.com";
        Assert.Equal(cell, SellerMailStore.JoinAddresses(SellerMailStore.SplitAddresses(cell)));
    }

    /// <summary>The comparison key, not a validity check — case and padding must not make two
    /// spellings of one address look like two people.</summary>
    [Fact]
    public void NormalisingAnAddressTrimsAndLowercasesIt()
    {
        Assert.Equal("a@x.com", SellerMailStore.NormalizeEmail("  A@X.CoM "));
    }

    [Theory]
    [InlineData("topcuu@mms-marketplace.com", true)]
    [InlineData("a.b-c@sub.example.co.uk", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("no-at-sign.com", false)]
    [InlineData("two@@example.com", false)]
    [InlineData("trailing@", false)]
    [InlineData("@leading.com", false)]
    [InlineData("spaced address@example.com", false)]
    [InlineData("nodot@localhost", false)]
    public void AddressesThatCannotBeRealAreCaughtBeforeOutlookSeesThem(string raw, bool expected)
    {
        Assert.Equal(expected, SellerMailStore.LooksLikeEmail(raw));
    }
}
