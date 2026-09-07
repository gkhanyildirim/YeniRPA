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
    static readonly OfferSellerGroup Seller = new(
        SellerId: "12421",
        SellerName: "Tedarik Türkiye",
        SellerKey: "id:12421",
        Offers: [.. Enumerable.Range(0, 1200).Select(i => new OfferLeadRow($"SKU{i}", i < 500 ? 0 : 1))],
        LeadTimeCounts: [new OfferLeadTimeCount(0, 500), new OfferLeadTimeCount(1, 700)]);

    static OfferSellerMail Render(string? subject, string? body) => OfferMailBuilder.Render(
        Seller, ["topcuu@mms-marketplace.com"], "12421 - Tedarik Türkiye.xlsx", 0,
        "2026-08-20", [0, 1], subject, body, "directory", null);

    // -----------------------------------------------------------------
    // Rendering
    // -----------------------------------------------------------------

    [Fact]
    public void EveryPlaceholderIsFilledIn()
    {
        var mail = Render(
            "{seller} ({sellerId}) {leadTimes}",
            "{email} · {recipientCount} · {fileName} · {offerCount} · {date}");

        Assert.Equal("Tedarik Türkiye (12421) 0-1", mail.Subject);
        Assert.Equal(
            "topcuu@mms-marketplace.com · 1 · 12421 - Tedarik Türkiye.xlsx · 1.200 · 2026-08-20",
            mail.Body);
    }

    /// <summary>Turkish groups thousands with a dot. "1200" in a Turkish sentence reads as a typo.</summary>
    [Fact]
    public void CountsAreGroupedTheTurkishWay()
    {
        Assert.Contains("1.200", Render("s", "{offerCount}").Body);
    }

    /// <summary>The split is what the mail is about; it has to come off the group rather than being
    /// recounted anywhere else, or the mail and the attachment can disagree.</summary>
    [Fact]
    public void TheLeadTimeCountsComeFromTheGroup()
    {
        var mail = Render("s", "b");

        Assert.Equal([(0, 500), (1, 700)], mail.LeadTimeCounts.Select(c => (c.LeadTime, c.Offers)));
        Assert.Equal(1200, mail.OfferCount);
    }

    /// <summary>One line per day, ascending, with the count grouped the Turkish way.</summary>
    [Fact]
    public void TheBreakdownIsOneLinePerDay()
    {
        var body = Render("s", "{leadTimeBreakdown}").Body;

        Assert.Equal(
            "Termini 0 gün olan teklif sayısı: 500\nTermini 1 gün olan teklif sayısı: 700",
            body);
    }

    /// <summary>
    /// <c>{leadTimes}</c> is a prefix of <c>{leadTimeBreakdown}</c>. Substituting the short one first
    /// would leave "0-1Breakdown}" in the middle of the mail.
    /// </summary>
    [Fact]
    public void TheShortTokenDoesNotEatTheBreakdownToken()
    {
        var body = Render("s", "{leadTimes} · {leadTimeBreakdown}").Body;

        Assert.StartsWith("0-1 · Termini 0 gün", body);
        Assert.DoesNotContain("Breakdown}", body);
    }

    /// <summary>A day the seller has no offers on is not a line — "0 offers at 0 days" is a line the
    /// seller has to read and then discard.</summary>
    [Fact]
    public void ADayWithNoOffersIsNotInTheBreakdown()
    {
        Assert.Equal(
            "Termini 1 gün olan teklif sayısı: 7",
            OfferMailBuilder.DescribeBreakdown([new OfferLeadTimeCount(0, 0), new OfferLeadTimeCount(1, 7)]));
    }

    /// <summary>Consecutive days read as a range in a sentence; anything else is listed.</summary>
    [Theory]
    [InlineData(new[] { 0, 1 }, "0-1")]
    [InlineData(new[] { 1, 2, 3 }, "1-3")]
    [InlineData(new[] { 0, 2 }, "0, 2")]
    [InlineData(new[] { 1 }, "1")]
    [InlineData(new[] { 2, 0, 1 }, "0-2")]
    public void TheWarnedDaysReadAsARangeWhenTheyAreOne(int[] days, string expected)
    {
        Assert.Equal(expected, OfferMailBuilder.DescribeLeadTimes(days));
    }

    [Fact]
    public void AnEmptyTemplateFallsBackToTheDefault()
    {
        var mail = Render("", "   ");

        Assert.Contains("Tedarik Türkiye", mail.Subject);
        Assert.Contains("Termini 0 gün olan teklif sayısı: 500", mail.Body);
        Assert.Contains("Termini 1 gün olan teklif sayısı: 700", mail.Body);
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

    /// <summary>A subject pasted out of Word carries CRLF, and folding the two characters separately
    /// would leave two spaces where the operator saw one.</summary>
    [Fact]
    public void ACrLfInTheSubjectBecomesOneSpace()
    {
        Assert.Equal("first second", Render("first\r\nsecond", "b").Subject);
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
        var hostile = new OfferSellerGroup("1", "{email}", "id:1", [], []);
        var mail = OfferMailBuilder.Render(
            hostile, ["a@b.com"], "f.xlsx", 0, "2026-08-20", [0, 1], "s", "{seller}", "", null);

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
    /// The file name is derived from a seller name that came out of an uploaded spreadsheet. A path in
    /// it must not be able to reach anything outside the output folder — <c>auth.dat</c> is two
    /// directories up from a plausible folder choice.
    /// </summary>
    [Theory]
    [InlineData(@"..\..\auth.dat")]
    [InlineData("../../auth.dat")]
    [InlineData(@"sub\Alpha.xlsx")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\Alpha.xlsx")]
    public void APathInTheFileNameIsRefused(string fileName)
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
    /// "helpfully" adds fuzzy matching: an 85 %-similar match attaches one seller's complete offer
    /// list to another seller's mail.
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
