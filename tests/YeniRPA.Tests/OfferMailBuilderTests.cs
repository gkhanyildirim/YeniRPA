using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The two rules that decide what a seller receives: what the message says, and which file is
/// attached to it. The second one is the reason this module has a test file at all — an attachment
/// resolved one row over is a competitor data leak, and it would look like a working system.
/// </summary>
public class OfferMailBuilderTests
{
    static readonly SellerMailEntry Seller =
        new("12421", "Tedarik Türkiye", "topcuu@mms-marketplace.com", "Tedarik Türkiye.xlsx", 0, 19700);

    static RenderedMail Render(string? subject, string? body) =>
        OfferMailBuilder.Render(Seller, "2026-08-20", subject, body, "", 0, null);

    // -----------------------------------------------------------------
    // Rendering
    // -----------------------------------------------------------------

    [Fact]
    public void EveryPlaceholderIsFilledIn()
    {
        var mail = Render(
            "{seller} ({sellerId})",
            "{email} · {fileName} · {leadTime0} / {leadTime1} / {leadTimeTotal} · {date}");

        Assert.Equal("Tedarik Türkiye (12421)", mail.Subject);
        Assert.Equal("topcuu@mms-marketplace.com · Tedarik Türkiye.xlsx · 0 / 19.700 / 19.700 · 2026-08-20", mail.Body);
    }

    /// <summary>Turkish groups thousands with a dot, and these counts run to five digits. "19700"
    /// in a Turkish sentence reads as a typo.</summary>
    [Fact]
    public void CountsAreGroupedTheTurkishWay()
    {
        Assert.Contains("19.700", Render("s", "{leadTime1}").Body);
    }

    [Fact]
    public void AnEmptyTemplateFallsBackToTheDefault()
    {
        var mail = Render("", "   ");

        Assert.Contains("Tedarik Türkiye", mail.Subject);
        Assert.Contains("19.700", mail.Body);
    }

    /// <summary>
    /// A subject line cannot hold a newline: mail clients drop everything after it, which would make
    /// the text a seller sees differ from the text the operator approved.
    /// </summary>
    [Fact]
    public void NewlinesInTheSubjectAreFoldedToSpaces()
    {
        Assert.Equal("first second", Render("first\nsecond", "b").Subject);
    }

    [Fact]
    public void TheBodyKeepsItsLineBreaksNormalisedToNewlines()
    {
        Assert.Equal("one\ntwo", Render("s", "one\r\ntwo").Body);
    }

    /// <summary>
    /// Deleting an unrecognised placeholder would ship "Sayın ," to a seller and throwing would let
    /// one typo block the whole preview. It is left in place and reported instead.
    /// </summary>
    [Fact]
    public void AnUnknownPlaceholderSurvivesInTheTextAndIsReported()
    {
        var mail = Render("s", "Sayın {sellerName} yetkilisi");

        Assert.Contains("{sellerName}", mail.Body);
        Assert.Contains("{sellerName}", mail.UnknownPlaceholders);
    }

    [Fact]
    public void AGoodTemplateReportsNoUnknownPlaceholders()
    {
        Assert.Empty(Render(OfferMailBuilder.DefaultSubjectTemplate, OfferMailBuilder.DefaultBodyTemplate)
            .UnknownPlaceholders);
    }

    /// <summary>
    /// The classic template-injection foot-gun: a seller whose storefront name contains a literal
    /// placeholder must not have it re-substituted, or one seller's mail carries another's address.
    /// </summary>
    [Fact]
    public void ASellerNameContainingAPlaceholderIsNotResubstituted()
    {
        var hostile = new SellerMailEntry("1", "{email}", "a@b.com", "f.xlsx", 0, 0);
        var mail = OfferMailBuilder.Render(hostile, "2026-08-20", "s", "{seller}", "", 0, null);

        Assert.Equal("{email}", mail.Body);
    }

    // -----------------------------------------------------------------
    // Attachment resolution
    // -----------------------------------------------------------------

    static readonly string Folder = Path.Combine(Path.GetTempPath(), "yenirpa-offers");

    [Fact]
    public void APlainFileNameResolvesInsideTheFolder()
    {
        var match = OfferMailBuilder.ResolveAttachment(Folder, "Tedarik Türkiye.xlsx");

        Assert.Null(match.Problem);
        Assert.Equal(Path.Combine(Folder, "Tedarik Türkiye.xlsx"), match.Path);
    }

    /// <summary>A trailing separator on the configured folder must not change the answer — the
    /// containment check compares string prefixes.</summary>
    [Fact]
    public void ATrailingSeparatorOnTheFolderChangesNothing()
    {
        var match = OfferMailBuilder.ResolveAttachment(Folder + Path.DirectorySeparatorChar, "Akpa DTM.xlsx");

        Assert.Null(match.Problem);
        Assert.Equal(Path.Combine(Folder, "Akpa DTM.xlsx"), match.Path);
    }

    /// <summary>
    /// The mapping table is a spreadsheet anyone can edit. A path in the file-name column must not be
    /// able to reach anything outside the attachment folder — <c>auth.dat</c> is two directories up
    /// from a plausible folder choice.
    /// </summary>
    [Theory]
    [InlineData(@"..\..\auth.dat")]
    [InlineData("../../auth.dat")]
    [InlineData(@"sub\Alpha.xlsx")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\Alpha.xlsx")]
    public void APathInTheFileNameColumnIsRefused(string fileName)
    {
        var match = OfferMailBuilder.ResolveAttachment(Folder, fileName);

        Assert.NotNull(match.Problem);
        Assert.Equal("", match.Path);
    }

    [Fact]
    public void AnEmptyFileNameIsRefused()
    {
        Assert.NotNull(OfferMailBuilder.ResolveAttachment(Folder, "   ").Problem);
    }

    [Fact]
    public void AnEmptyFolderIsRefused()
    {
        Assert.NotNull(OfferMailBuilder.ResolveAttachment("", "Alpha.xlsx").Problem);
    }

    /// <summary>
    /// The rule is exact-name only. This test exists to be the thing that fails if anyone ever
    /// "helpfully" adds fuzzy matching: an 85 %-similar match attaches one seller's complete price
    /// and stock list to another seller's mail.
    /// </summary>
    [Fact]
    public void ResolutionIsByNameAloneAndNeverGuessesANeighbour()
    {
        var a = OfferMailBuilder.ResolveAttachment(Folder, "Alpha.xlsx");
        var b = OfferMailBuilder.ResolveAttachment(Folder, "Alpha (1).xlsx");

        Assert.NotEqual(a.Path, b.Path);
        Assert.Equal(Path.Combine(Folder, "Alpha (1).xlsx"), b.Path);
    }
}
