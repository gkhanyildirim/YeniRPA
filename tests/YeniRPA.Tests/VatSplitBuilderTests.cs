using System.Text;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The rule that decides which offer ends up in which seller's file, and what that file is called.
/// Every one of these decides whose price and stock list lands in whose inbox.
/// </summary>
public class VatSplitBuilderTests
{
    const string Headers =
        "Seller id;Seller;Offer id;Product Title;EAN;Product Brand;Category Label;Offer Condition;Offer Total Price;Stock Qty;State Reasons";

    /// <summary>Semicolons, so a State Reasons cell full of commas stays one cell.</summary>
    static VatSplitBuilder.SplitResult Read(string body)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"{Headers}\n{body}"));
        return VatSplitBuilder.Read(stream, "kdvsizler.csv");
    }

    static VatSellerGroup Group(string id, string name, int offers = 1) => new(
        id, name, VatSplitBuilder.SellerKey(id, name),
        [.. Enumerable.Range(0, offers).Select(i =>
            new VatOfferRow($"offer{i}", "", "t", "", "", "", "", null, ""))]);

    [Fact]
    public void OffersAreGroupedByTheSellerTheExportNames()
    {
        var result = Read(
            "11835;Prodesk;1805444610;LEGO Icons;5702017829159;LEGO;TOY;NEW;23,999.00 €;0;VAT_RATE_MISSING\n" +
            "11476;BL Müzik;1812641455;Strum Buddy;0858445004684;FLUID;MUSIC;NEW;4,500.00 €;1;VAT_RATE_MISSING\n" +
            "11835;Prodesk;1805444082;LEGO Optimus;0673419355704;LEGO;TOY;NEW;11,999.00 €;1;VAT_RATE_MISSING\n");

        Assert.Equal(3, result.OffersInFile);
        Assert.Equal(2, result.Sellers.Count);

        var prodesk = result.Sellers.Single(s => s.SellerId == "11835");
        Assert.Equal(2, prodesk.Offers.Count);
        Assert.Equal(["1805444610", "1805444082"], prodesk.Offers.Select(o => o.OfferId));

        // Sellers come back in the order the export introduced them, which is the order the operator
        // scrolled past in Excel.
        Assert.Equal(["11835", "11476"], result.Sellers.Select(s => s.SellerId));
    }

    /// <summary>
    /// A barcode is not a number. <c>0858445004684</c> is real in this export and loses its leading
    /// zero the moment anything reads it as one, at which point it no longer identifies the product.
    /// </summary>
    [Fact]
    public void AnEanKeepsItsLeadingZero()
    {
        var result = Read("11476;BL Müzik;1812641455;Strum Buddy;0858445004684;FLUID;MUSIC;NEW;4,500.00 €;1;VAT_RATE_MISSING\n");

        Assert.Equal("0858445004684", result.Sellers.Single().Offers.Single().Ean);
    }

    /// <summary>
    /// "We could not read the stock" and "there are none in stock" are different claims and a seller
    /// acts on them differently, so an unreadable cell is null rather than 0.
    /// </summary>
    [Fact]
    public void AnUnreadableStockCellIsNotZero()
    {
        var result = Read(
            "1;A;o1;t;;;;;;0;VAT_RATE_MISSING\n" +
            "2;B;o2;t;;;;;;n/a;VAT_RATE_MISSING\n" +
            "3;C;o3;t;;;;;;111;VAT_RATE_MISSING\n");

        Assert.Equal(0, result.Sellers[0].Offers.Single().Stock);
        Assert.Null(result.Sellers[1].Offers.Single().Stock);
        Assert.Equal(111, result.Sellers[2].Offers.Single().Stock);
    }

    /// <summary>The seller id identifies the row, so a mid-month storefront rename is one seller, not
    /// two — but the rename is worth saying out loud, because the name is what appears in the mail.</summary>
    [Fact]
    public void OneSellerIdUnderTwoNamesIsStillOneSeller()
    {
        var result = Read(
            "12953;VintageOnline;o1;t;;;;;;1;VAT_RATE_MISSING\n" +
            "12953;Vintage Online;o2;t;;;;;;1;VAT_RATE_MISSING\n");

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
            "11835;Prodesk;o1;t;;;;;;1;VAT_RATE_MISSING\n" +
            ";;o2;orphan;;;;;;1;VAT_RATE_MISSING\n");

        Assert.Equal(2, result.OffersInFile);
        Assert.Single(result.Sellers);
        Assert.Contains(result.Warnings, w => w.Contains("no seller"));
    }

    /// <summary>The used range of a sheet routinely runs past the data; a blank line is not a row.</summary>
    [Fact]
    public void TrailingBlankLinesAreNotOffers()
    {
        var result = Read("11835;Prodesk;o1;t;;;;;;1;VAT_RATE_MISSING\n;;;;;;;;;;\n;;;;;;;;;;\n");

        Assert.Equal(1, result.OffersInFile);
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
        Assert.Equal("11835 - Prodesk.xlsx", VatSplitBuilder.FileNameFor(Group("11835", "Prodesk")));
    }

    /// <summary>
    /// Turkish letters are kept. Stripping them to ASCII would fold "Bizbiz-E" and "Bızbız-E" onto one
    /// file name, which is exactly the collision this name must never create.
    /// </summary>
    [Fact]
    public void TurkishLettersSurviveTheFileName()
    {
        Assert.Equal("12482 - Yazıcı Bende.xlsx", VatSplitBuilder.FileNameFor(Group("12482", "Yazıcı Bende")));
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
        var name = VatSplitBuilder.FileNameFor(Group("10001", hostileName));

        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.Equal(name, Path.GetFileName(name));
    }

    /// <summary>A name that sanitises to nothing still has to produce a usable file name.</summary>
    [Fact]
    public void ASellerWhoseNameSanitisesAwayFallsBackToTheirId()
    {
        Assert.Equal("10001.xlsx", VatSplitBuilder.FileNameFor(Group("10001", "..")));
    }

    /// <summary>
    /// <b>The test this module exists for.</b> Two sellers whose names reduce to one file name: the
    /// second write overwrites the first, so mailing either one hands a seller the other's complete
    /// price and stock list. Both are refused — not the second one, both.
    /// </summary>
    [Fact]
    public void TwoSellersWhoseFileNamesCollideAreBothRefused()
    {
        // Same id, different case in the name — one file on Windows.
        var a = Group("10001", "Prodesk");
        var b = new VatSellerGroup("10001", "PRODESK", "name:prodesk-2", a.Offers);

        var clashes = VatSplitBuilder.FindFileNameClashes([a, b]);

        Assert.Equal(2, clashes.Count);
        Assert.Contains(a.SellerKey, clashes);
        Assert.Contains(b.SellerKey, clashes);
    }

    [Fact]
    public void DistinctFileNamesClashWithNothing()
    {
        Assert.Empty(VatSplitBuilder.FindFileNameClashes([Group("10001", "Prodesk"), Group("10002", "Nethouse")]));
    }

    // ---------------------------------------------------------------------
    // Refusals
    // ---------------------------------------------------------------------

    /// <summary>Without a seller column there is no way to tell whose offers these are, and guessing
    /// is the one thing this module may not do.</summary>
    [Fact]
    public void AnExportWithNoSellerColumnIsRefusedByName()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Offer id;Product Title\no1;t\n"));

        var error = Assert.Throws<InvalidOperationException>(() => VatSplitBuilder.Read(stream, "x.csv"));
        Assert.Contains("Seller", error.Message);
    }

    /// <summary>A list a seller cannot read the product names off is not worth sending.</summary>
    [Fact]
    public void AnExportWithNoProductTitleIsRefusedByName()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Seller;Offer id\nProdesk;o1\n"));

        var error = Assert.Throws<InvalidOperationException>(() => VatSplitBuilder.Read(stream, "x.csv"));
        Assert.Contains("Product Title", error.Message);
    }

    /// <summary>The seller key follows <c>SellerGroupMap.Resolve</c>'s precedence: id when there is
    /// one, folded name otherwise — so a row typed in without an id still lands on its seller.</summary>
    [Fact]
    public void TheSellerKeyPrefersTheIdAndFoldsTheNameOtherwise()
    {
        Assert.Equal("id:11835", VatSplitBuilder.SellerKey("11835", "Prodesk"));
        Assert.Equal("id:11835", VatSplitBuilder.SellerKey("11835.0", "Anything Else"));
        Assert.Equal(VatSplitBuilder.SellerKey("", "FIRSATKURDU"), VatSplitBuilder.SellerKey("", "FırsatKurdu"));
    }
}
