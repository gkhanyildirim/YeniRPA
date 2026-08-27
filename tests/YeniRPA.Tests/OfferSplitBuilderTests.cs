using System.Text;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The rule that decides which offer ends up in which seller's file, and what that file is called.
/// Every one of these decides whose offer list lands in whose inbox.
/// </summary>
public class OfferSplitBuilderTests
{
    /// <summary>Exactly what the Mirakl export writes, in the order it writes it.</summary>
    const string Headers = "Seller;Seller ID;Product SKU;Lead time to ship";

    /// <summary>Semicolons, so a product name full of commas stays one cell.</summary>
    static OfferSplitBuilder.SplitResult Read(string body)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"{Headers}\n{body}"));
        return OfferSplitBuilder.Read(stream, "offers.csv");
    }

    static OfferSellerGroup Group(string id, string name, int offers = 1) => new(
        id, name, OfferSplitBuilder.SellerKey(id, name),
        [.. Enumerable.Range(0, offers).Select(i => new OfferLeadRow($"SKU{i}", 1))],
        offers, 0);

    // ---------------------------------------------------------------------
    // The lead-time filter
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>The test this module exists for.</b> The export holds every lead time the marketplace has;
    /// only one and two days are worth a warning. Zero is excluded on purpose — it is what the export
    /// writes for offers the seller does not ship at all — and a blank cell, which is a third of the
    /// real file, is not a promise anyone made.
    /// </summary>
    [Fact]
    public void OnlyOneAndTwoDayLeadTimesAreKept()
    {
        var result = Read(
            "Prodesk;11835;SKU-A;0\n" +
            "Prodesk;11835;SKU-B;1\n" +
            "Prodesk;11835;SKU-C;2\n" +
            "Prodesk;11835;SKU-D;3\n" +
            "Prodesk;11835;SKU-E;\n");

        var seller = Assert.Single(result.Sellers);
        Assert.Equal(["SKU-B", "SKU-C"], seller.Offers.Select(o => o.ProductSku));
        Assert.Equal(2, result.OffersInFile);
        Assert.Equal(3, result.OffersFilteredOut);
    }

    /// <summary>The mail quotes both counts, so they are computed once beside the list they describe
    /// rather than recounted wherever they are needed.</summary>
    [Fact]
    public void TheTwoLeadTimesAreCountedSeparately()
    {
        var result = Read(
            "Prodesk;11835;SKU-A;1\n" +
            "Prodesk;11835;SKU-B;1\n" +
            "Prodesk;11835;SKU-C;2\n");

        var seller = Assert.Single(result.Sellers);
        Assert.Equal(2, seller.LeadTime1);
        Assert.Equal(1, seller.LeadTime2);
        Assert.Equal(3, seller.Offers.Count);
    }

    /// <summary>A numeric cell read back through a general format can carry a decimal tail; "1.0" is
    /// one day. A cell that is not a number at all is not a lead time and must not become 0.</summary>
    [Theory]
    [InlineData("1", 1)]
    [InlineData("2.0", 2)]
    [InlineData("0", 0)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("N/A", null)]
    [InlineData("1.5", null)]
    public void ALeadTimeCellIsReadAsWholeDaysOrNotAtAll(string cell, int? expected)
    {
        Assert.Equal(expected, OfferSplitBuilder.ReadLeadTime(cell));
    }

    /// <summary>A seller whose every offer is filtered out is not in the result at all — there is
    /// nothing to warn them about.</summary>
    [Fact]
    public void ASellerWithNoShortLeadTimesIsNotInTheResult()
    {
        var result = Read(
            "Prodesk;11835;SKU-A;1\n" +
            "BL Müzik;11476;SKU-B;5\n");

        Assert.Equal("11835", Assert.Single(result.Sellers).SellerId);
    }

    // ---------------------------------------------------------------------
    // Grouping
    // ---------------------------------------------------------------------

    [Fact]
    public void OffersAreGroupedByTheSellerTheExportNames()
    {
        var result = Read(
            "Prodesk;11835;SKU-A;1\n" +
            "BL Müzik;11476;SKU-B;2\n" +
            "Prodesk;11835;SKU-C;2\n");

        Assert.Equal(3, result.OffersInFile);
        Assert.Equal(2, result.Sellers.Count);

        var prodesk = result.Sellers.Single(s => s.SellerId == "11835");
        Assert.Equal(["SKU-A", "SKU-C"], prodesk.Offers.Select(o => o.ProductSku));

        // Sellers come back in the order the export introduced them, which is the order the operator
        // scrolled past in Excel.
        Assert.Equal(["11835", "11476"], result.Sellers.Select(s => s.SellerId));
    }

    /// <summary>A seller can hold two offers on one product at one lead time. The attachment has no
    /// column that would tell those two lines apart, so a second identical line reads as a mistake in
    /// our file rather than as two offers.</summary>
    [Fact]
    public void TheSameSkuAtTheSameLeadTimeIsListedOnce()
    {
        var result = Read(
            "Prodesk;11835;SKU-A;1\n" +
            "Prodesk;11835;SKU-A;1\n");

        Assert.Single(Assert.Single(result.Sellers).Offers);

        // The row count the operator uploaded is still reported as it was.
        Assert.Equal(2, result.OffersInFile);
    }

    /// <summary>
    /// One SKU offered at one day and at two is two different things to fix, and both belong in the
    /// list. Folding them on the SKU alone would hide half the problem from the seller.
    /// </summary>
    [Fact]
    public void TheSameSkuAtTwoLeadTimesIsListedTwice()
    {
        var result = Read(
            "Prodesk;11835;SKU-A;1\n" +
            "Prodesk;11835;SKU-A;2\n");

        var seller = Assert.Single(result.Sellers);
        Assert.Equal(2, seller.Offers.Count);
        Assert.Equal([1, 2], seller.Offers.Select(o => o.LeadTime).Order());
    }

    /// <summary>Collapsing every SKU-less row onto one blank key would delete real offers from the
    /// seller's list.</summary>
    [Fact]
    public void OffersWithNoSkuAreNotCollapsedTogether()
    {
        var result = Read(
            "Prodesk;11835;;1\n" +
            "Prodesk;11835;;1\n");

        Assert.Equal(2, Assert.Single(result.Sellers).Offers.Count);
    }

    /// <summary>The seller id identifies the row, so a mid-month storefront rename is one seller, not
    /// two — but the rename is worth saying out loud, because the name is what appears in the mail.</summary>
    [Fact]
    public void OneSellerIdUnderTwoNamesIsStillOneSeller()
    {
        var result = Read(
            "VintageOnline;12953;SKU-A;1\n" +
            "Vintage Online;12953;SKU-B;1\n");

        var seller = Assert.Single(result.Sellers);
        Assert.Equal(2, seller.Offers.Count);
        Assert.Equal("VintageOnline", seller.SellerName);
        Assert.Contains(result.Warnings, w => w.Contains("Vintage Online") && w.Contains("12953"));
    }

    /// <summary>A row nobody can be billed for cannot be mailed to anybody. Counted and reported
    /// rather than dropped in silence.</summary>
    [Fact]
    public void RowsThatNameNoSellerAreCountedAndReported()
    {
        var result = Read(
            "Prodesk;11835;SKU-A;1\n" +
            ";;SKU-B;1\n");

        Assert.Equal(2, result.OffersInFile);
        Assert.Single(result.Sellers);
        Assert.Contains(result.Warnings, w => w.Contains("no seller"));
    }

    /// <summary>The used range of a sheet routinely runs past the data; a blank line is not a row and
    /// must not land in the filtered-out count either.</summary>
    [Fact]
    public void TrailingBlankLinesAreNotOffers()
    {
        var result = Read("Prodesk;11835;SKU-A;1\n;;;\n;;;\n");

        Assert.Equal(1, result.OffersInFile);
        Assert.Equal(0, result.OffersFilteredOut);
        Assert.Empty(result.Warnings);
    }

    // ---------------------------------------------------------------------
    // File names
    // ---------------------------------------------------------------------

    /// <summary>The id leads because it is what makes the name unique and stable; the name follows so
    /// the operator can recognise the file in the folder without looking anything up.</summary>
    [Fact]
    public void TheFileNameLeadsWithTheSellerId()
    {
        Assert.Equal("11835 - Prodesk.xlsx", OfferSplitBuilder.FileNameFor(Group("11835", "Prodesk")));
    }

    /// <summary>
    /// Turkish letters are kept. Stripping them to ASCII would fold "Bizbiz-E" and "Bızbız-E" onto one
    /// file name, which is exactly the collision this name must never create.
    /// </summary>
    [Fact]
    public void TurkishLettersSurviveTheFileName()
    {
        Assert.Equal("12482 - Yazıcı Bende.xlsx", OfferSplitBuilder.FileNameFor(Group("12482", "Yazıcı Bende")));
    }

    /// <summary>
    /// The seller name comes out of an uploaded spreadsheet, so it is untrusted input on the path that
    /// decides what gets written and later attached. Nothing it contains may produce a separator.
    /// </summary>
    [Theory]
    [InlineData(@"..\..\auth")]
    [InlineData("../../auth")]
    [InlineData(@"C:\Windows\system32")]
    [InlineData("a/b")]
    public void AFileNameNeverCarriesAPathOutOfTheFolder(string hostileName)
    {
        var name = OfferSplitBuilder.FileNameFor(Group("10001", hostileName));

        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.Equal(name, Path.GetFileName(name));
    }

    /// <summary>A name that sanitises to nothing still has to produce a usable file name.</summary>
    [Fact]
    public void ASellerWhoseNameSanitisesAwayFallsBackToTheirId()
    {
        Assert.Equal("10001.xlsx", OfferSplitBuilder.FileNameFor(Group("10001", "..")));
    }

    /// <summary>
    /// Two sellers whose names reduce to one file name: the second write overwrites the first, so
    /// mailing either one hands a seller the other's complete offer list. Both are refused — not the
    /// second one, both.
    /// </summary>
    [Fact]
    public void TwoSellersWhoseFileNamesCollideAreBothRefused()
    {
        // Same id, different case in the name — one file on Windows.
        var a = Group("10001", "Prodesk");
        var b = new OfferSellerGroup("10001", "PRODESK", "name:prodesk-2", a.Offers, 1, 0);

        var clashes = OfferSplitBuilder.FindFileNameClashes([a, b]);

        Assert.Equal(2, clashes.Count);
        Assert.Contains(a.SellerKey, clashes);
        Assert.Contains(b.SellerKey, clashes);
    }

    [Fact]
    public void DistinctFileNamesClashWithNothing()
    {
        Assert.Empty(OfferSplitBuilder.FindFileNameClashes([Group("10001", "Prodesk"), Group("10002", "Nethouse")]));
    }

    // ---------------------------------------------------------------------
    // Refusals
    // ---------------------------------------------------------------------

    /// <summary>Without a seller column there is no way to tell whose offers these are, and guessing
    /// is the one thing this module may not do.</summary>
    [Fact]
    public void AnExportWithNoSellerColumnIsRefusedByName()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Product SKU;Lead time to ship\nSKU-A;1\n"));

        var error = Assert.Throws<InvalidOperationException>(() => OfferSplitBuilder.Read(stream, "x.csv"));
        Assert.Contains("Seller", error.Message);
    }

    /// <summary>A list of bare lead times identifies nothing — the SKU is how the seller finds the
    /// offer in their own panel.</summary>
    [Fact]
    public void AnExportWithNoProductSkuIsRefusedByName()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Seller;Lead time to ship\nProdesk;1\n"));

        var error = Assert.Throws<InvalidOperationException>(() => OfferSplitBuilder.Read(stream, "x.csv"));
        Assert.Contains("Product SKU", error.Message);
    }

    /// <summary>
    /// The column this module selects on. An export that renamed it would match nothing and report
    /// every seller as having no short lead times — a clean-looking result that is entirely wrong, so
    /// it is refused rather than run.
    /// </summary>
    [Fact]
    public void AnExportWithNoLeadTimeColumnIsRefusedByName()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Seller;Product SKU\nProdesk;SKU-A\n"));

        var error = Assert.Throws<InvalidOperationException>(() => OfferSplitBuilder.Read(stream, "x.csv"));
        Assert.Contains("Lead time to ship", error.Message);
    }

    /// <summary>The seller key follows <c>SellerGroupMap.Resolve</c>'s precedence: id when there is
    /// one, folded name otherwise — so a row typed in without an id still lands on its seller.</summary>
    [Fact]
    public void TheSellerKeyPrefersTheIdAndFoldsTheNameOtherwise()
    {
        Assert.Equal("id:11835", OfferSplitBuilder.SellerKey("11835", "Prodesk"));
        Assert.Equal("id:11835", OfferSplitBuilder.SellerKey("11835.0", "Anything Else"));
        Assert.Equal(OfferSplitBuilder.SellerKey("", "FIRSATKURDU"), OfferSplitBuilder.SellerKey("", "FırsatKurdu"));
    }
}
