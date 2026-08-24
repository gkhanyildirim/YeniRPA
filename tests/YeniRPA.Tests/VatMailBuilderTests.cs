using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The wording that reaches a seller. The substitution order is the part worth pinning down — it is
/// what stops one seller's address appearing in another seller's mail.
/// </summary>
public class VatMailBuilderTests
{
    static VatSellerGroup Seller(string id, string name, int offers = 1) => new(
        id, name, VatSplitBuilder.SellerKey(id, name),
        [.. Enumerable.Range(0, offers).Select(i =>
            new VatOfferRow($"offer{i}", "", "t", "", "", "", "", null, ""))]);

    static VatSellerMail Render(
        VatSellerGroup seller,
        string? subject = null,
        string? body = null,
        IReadOnlyList<string>? recipients = null) =>
        VatMailBuilder.Render(
            seller,
            recipients ?? ["info@prodesk.com"],
            "11835 - Prodesk.xlsx",
            2048,
            "2026-08-24",
            subject,
            body,
            "directory",
            null);

    [Fact]
    public void EveryPlaceholderIsFilledIn()
    {
        var mail = Render(
            Seller("11835", "Prodesk", 42),
            subject: "{seller} — {offerCount} teklif",
            body: "{sellerId} · {email} · {recipientCount} · {fileName} · {offerCount} · {date}",
            recipients: ["info@prodesk.com", "satis@prodesk.com"]);

        Assert.Equal("Prodesk — 42 teklif", mail.Subject);
        Assert.Equal(
            "11835 · info@prodesk.com; satis@prodesk.com · 2 · 11835 - Prodesk.xlsx · 42 · 2026-08-24",
            mail.Body);
        Assert.Empty(mail.UnknownPlaceholders);
    }

    /// <summary>
    /// The classic template-injection foot-gun, and here it would put one seller's address in another
    /// seller's mail. <c>{seller}</c> is substituted last, so whatever a storefront name contains is
    /// never re-substituted.
    /// </summary>
    [Fact]
    public void ASellerNameContainingAPlaceholderIsNotResubstituted()
    {
        var mail = Render(
            Seller("10001", "{email} Ticaret"),
            body: "Sayın {seller} yetkilisi",
            recipients: ["victim@example.com"]);

        Assert.Equal("Sayın {email} Ticaret yetkilisi", mail.Body);
        Assert.DoesNotContain("victim@example.com", mail.Body);
    }

    /// <summary>
    /// A typo is left in the text and pointed at. Deleting it would ship "Sayın ," to a seller, and
    /// throwing would let one typo block the whole preview.
    /// </summary>
    [Fact]
    public void AnUnknownPlaceholderIsLeftAloneAndReported()
    {
        var mail = Render(Seller("10001", "Prodesk"), body: "Sayın {satici} yetkilisi");

        Assert.Contains("{satici}", mail.Body);
        Assert.Equal(["{satici}"], mail.UnknownPlaceholders);
    }

    /// <summary>Every mail client silently drops newlines in a subject line, which would make the
    /// approved text differ from the sent text. Folded here, once.</summary>
    [Fact]
    public void NewlinesInTheSubjectAreFoldedToSpaces()
    {
        var mail = Render(Seller("10001", "Prodesk"), subject: "KDV\r\nuyarısı");

        Assert.Equal("KDV uyarısı", mail.Subject);
    }

    /// <summary>The count in the text is the number of rows in the file that travels with it. A mail
    /// promising 42 offers beside a list of 3 is worse than no mail.</summary>
    [Fact]
    public void TheOfferCountIsTheSizeOfTheirOwnList()
    {
        Assert.Equal(3, Render(Seller("10001", "Prodesk", 3)).OfferCount);
        Assert.Contains("3", Render(Seller("10001", "Prodesk", 3)).Subject);
    }

    /// <summary>Turkish groups thousands with a dot, and these lists run to four digits.</summary>
    [Fact]
    public void CountsAreWrittenTheWayATurkishReaderExpects()
    {
        var mail = Render(Seller("10001", "Prodesk", 1234), subject: "{offerCount}");

        Assert.Equal("1.234", mail.Subject);
    }

    /// <summary>Blank templates fall back to what ships rather than sending an empty mail.</summary>
    [Fact]
    public void BlankTemplatesFallBackToTheDefaults()
    {
        var mail = Render(Seller("11835", "Prodesk", 5), subject: "   ", body: "");

        Assert.Contains("Prodesk", mail.Subject);
        Assert.Contains("KDV", mail.Body);
        Assert.Contains("11835 - Prodesk.xlsx", mail.Body);
    }

    /// <summary>Both defaults have to be renderable with nothing left unresolved, or the very first
    /// preview an operator sees is broken.</summary>
    [Fact]
    public void TheShippedDefaultsLeaveNoPlaceholderBehind()
    {
        var mail = Render(Seller("11835", "Prodesk", 5));

        Assert.Empty(mail.UnknownPlaceholders);
        Assert.DoesNotContain('{', mail.Subject);
        Assert.DoesNotContain('{', mail.Body);
    }

    /// <summary>The card shows where an address came from, so a hand-entered one is visibly
    /// hand-entered.</summary>
    [Fact]
    public void TheMailRemembersWhereItsAddressCameFrom()
    {
        var mail = VatMailBuilder.Render(
            Seller("11835", "Prodesk", 1), ["a@b.com"], "f.xlsx", 0, "2026-08-24", null, null, "override", null);

        Assert.Equal("override", mail.MatchedBy);
    }
}
