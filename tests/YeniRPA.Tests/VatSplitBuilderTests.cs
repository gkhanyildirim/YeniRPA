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
    /// <summary>Exactly what the Mirakl export writes, lower-case <c>gtin</c> included — the column
    /// name this module silently failed to find for as long as the tests spelled it "EAN".</summary>
    const string Headers = "Seller id;Seller;Offer id;Product Title;gtin;Product Brand;State Reasons";

    /// <summary>Semicolons, so a product title full of commas stays one cell.</summary>
    static VatSplitBuilder.SplitResult Read(string body)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"{Headers}\n{body}"));
        return VatSplitBuilder.Read(stream, "kdvsizler.csv");
    }

    static VatSellerGroup Group(string id, string name, int offers = 1) => new(
        id, name, VatSplitBuilder.SellerKey(id, name),
        [.. Enumerable.Range(0, offers).Select(i => new VatOfferRow($"{i:D13}", "t", ""))]);

    [Fact]
    public void OffersAreGroupedByTheSellerTheExportNames()
    {
        var result = Read(
            "11835;Prodesk;1805444610;LEGO Icons;5702017829159;LEGO;VAT_RATE_MISSING\n" +
            "11476;BL Müzik;1812641455;Strum Buddy;858445004684;FLUID;VAT_RATE_MISSING\n" +
            "11835;Prodesk;1805444082;LEGO Optimus;673419355704;LEGO;VAT_RATE_MISSING\n");

        Assert.Equal(3, result.OffersInFile);
        Assert.Equal(2, result.Sellers.Count);

        var prodesk = result.Sellers.Single(s => s.SellerId == "11835");
        Assert.Equal(["5702017829159", "0673419355704"], prodesk.Offers.Select(o => o.Gtin));

        // Sellers come back in the order the export introduced them, which is the order the operator
        // scrolled past in Excel.
        Assert.Equal(["11835", "11476"], result.Sellers.Select(s => s.SellerId));
    }

    /// <summary>
    /// The export writes this column as <c>gtin</c>. Read under any other name it is not found at all,
    /// and because it is the only column a seller can look a product up by, the attachment goes out as
    /// a list of titles with no barcodes — which is exactly what used to happen.
    /// </summary>
    [Fact]
    public void TheGtinColumnIsFoundUnderItsMiraklName()
    {
        var result = Read("11835;Prodesk;o1;LEGO Icons;5702017829159;LEGO;VAT_RATE_MISSING\n");

        Assert.Equal("5702017829159", result.Sellers.Single().Offers.Single().Gtin);
    }

    /// <summary>
    /// A barcode is not a number, but the export stores it as one, so <c>0858445004684</c> arrives as
    /// <c>858445004684</c> and identifies nothing. The leading zero is put back.
    /// </summary>
    [Theory]
    [InlineData("858445004684", "0858445004684")]
    [InlineData("8683052680295.0", "8683052680295")]
    [InlineData("5702017829159", "5702017829159")]
    // Nothing is ever truncated: a GTIN-14 is a real barcode, and a cell that is not a barcode at all
    // is shown to the seller exactly as their export holds it rather than padded into a guess.
    [InlineData("05702017829159", "05702017829159")]
    [InlineData("N/A", "N/A")]
    [InlineData("", "")]
    public void AGtinIsPaddedToThirteenDigits(string cell, string expected)
    {
        Assert.Equal(expected, VatSplitBuilder.NormalizeGtin(cell));
        Assert.Equal(
            expected,
            Read($"11835;Prodesk;o1;t;{cell};LEGO;VAT_RATE_MISSING\n").Sellers.Single().Offers.Single().Gtin);
    }

    /// <summary>
    /// A seller can hold two offers on one product. The attachment no longer carries the offer number
    /// that would tell those two lines apart, so a second identical line reads as a mistake in our
    /// file rather than as two offers.
    /// </summary>
    [Fact]
    public void TheSameProductIsListedOnce()
    {
        var result = Read(
            "11835;Prodesk;1805444610;LEGO Icons;5702017829159;LEGO;VAT_RATE_MISSING\n" +
            "11835;Prodesk;1805444082;LEGO Icons;5702017829159;LEGO;VAT_RATE_MISSING\n");

        var seller = Assert.Single(result.Sellers);
        Assert.Equal("5702017829159", Assert.Single(seller.Offers).Gtin);

        // The row count the operator uploaded is still reported as it was.
        Assert.Equal(2, result.OffersInFile);
    }

    /// <summary>The same product padded on one row and not the other is still one product — the fold
    /// happens after the GTIN is normalised, not before.</summary>
    [Fact]
    public void APaddedGtinAndAnUnpaddedOneAreTheSameProduct()
    {
        var result = Read(
            "11476;BL Müzik;o1;Strum Buddy;858445004684;FLUID;VAT_RATE_MISSING\n" +
            "11476;BL Müzik;o2;Strum Buddy;0858445004684;FLUID;VAT_RATE_MISSING\n");

        Assert.Equal("0858445004684", Assert.Single(Assert.Single(result.Sellers).Offers).Gtin);
    }

    /// <summary>Folding every barcode-less row onto one key would delete real products from the
    /// seller's list, so those fall back to the title and brand.</summary>
    [Fact]
    public void ProductsWithNoGtinAreNotCollapsedTogether()
    {
        var result = Read(
            "11835;Prodesk;o1;LEGO Icons;;LEGO;VAT_RATE_MISSING\n" +
            "11835;Prodesk;o2;LEGO Optimus;;LEGO;VAT_RATE_MISSING\n" +
            "11835;Prodesk;o3;LEGO Icons;;LEGO;VAT_RATE_MISSING\n");

        var seller = Assert.Single(result.Sellers);
        Assert.Equal(["LEGO Icons", "LEGO Optimus"], seller.Offers.Select(o => o.ProductTitle));
    }

    /// <summary>The seller id identifies the row, so a mid-month storefront rename is one seller, not
    /// two — but the rename is worth saying out loud, because the name is what appears in the mail.</summary>
    [Fact]
    public void OneSellerIdUnderTwoNamesIsStillOneSeller()
    {
        var result = Read(
            "12953;VintageOnline;o1;t1;5702017829159;;VAT_RATE_MISSING\n" +
            "12953;Vintage Online;o2;t2;673419355704;;VAT_RATE_MISSING\n");

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
            "11835;Prodesk;o1;t;5702017829159;LEGO;VAT_RATE_MISSING\n" +
            ";;o2;orphan;673419355704;LEGO;VAT_RATE_MISSING\n");

        Assert.Equal(2, result.OffersInFile);
        Assert.Single(result.Sellers);
        Assert.Contains(result.Warnings, w => w.Contains("no seller"));
    }

    /// <summary>The used range of a sheet routinely runs past the data; a blank line is not a row.</summary>
    [Fact]
    public void TrailingBlankLinesAreNotOffers()
    {
        var result = Read("11835;Prodesk;o1;t;5702017829159;LEGO;VAT_RATE_MISSING\n;;;;;;\n;;;;;;\n");

        Assert.Equal(1, result.OffersInFile);

        // Blank lines are not rows at all, so they are not "filtered out" either.
        Assert.Equal(0, result.OffersFilteredOut);
        Assert.Empty(result.Warnings);
    }

    // ---------------------------------------------------------------------
    // The state-reason filter
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>The rule this filter exists for.</b> An offer that is also switched off, out of stock or
    /// priced at zero has a bigger problem than its VAT rate, and asking its seller to fix the VAT
    /// rate is the wrong message. Only offers whose sole complaint is the missing VAT rate are warned
    /// about — the export writes the others alongside it in the same cell.
    /// </summary>
    [Fact]
    public void OnlyOffersWhoseSoleStateReasonIsTheMissingVatRateAreWarnedAbout()
    {
        var result = Read(
            "11835;Prodesk;o1;LEGO Icons;5702017829159;LEGO;VAT_RATE_MISSING\n" +
            "11835;Prodesk;o2;LEGO Optimus;673419355704;LEGO;INACTIVE_IN_MIRAKL,MIRAKL_ZERO_QUANTITY,VAT_RATE_MISSING\n" +
            "11476;BL Müzik;o3;Strum Buddy;858445004684;FLUID;SELLER_NOT_AUTHORIZED_TO_SELL_REFURBISHED,VAT_RATE_MISSING\n");

        // One row survived, and it is the one that names nothing else.
        Assert.Equal(1, result.OffersInFile);
        Assert.Equal(2, result.OffersFilteredOut);

        var seller = Assert.Single(result.Sellers);
        Assert.Equal("11835", seller.SellerId);
        Assert.Equal("LEGO Icons", Assert.Single(seller.Offers).ProductTitle);
    }

    /// <summary>The filter runs before the grouping, so a seller is only in the run for the offers
    /// that survived it — not for every offer the export lists against their name.</summary>
    [Fact]
    public void ASellerCarriesOnlyTheOffersThatSurvivedTheFilter()
    {
        var result = Read(
            "11835;Prodesk;o1;LEGO Icons;5702017829159;LEGO;INACTIVE_IN_MIRAKL,VAT_RATE_MISSING\n" +
            "11835;Prodesk;o2;LEGO Optimus;673419355704;LEGO;VAT_RATE_MISSING\n" +
            "11835;Prodesk;o3;LEGO Creator;5702017829160;LEGO;PRICE_IS_ZERO,VAT_RATE_MISSING\n");

        var seller = Assert.Single(result.Sellers);
        Assert.Equal("LEGO Optimus", Assert.Single(seller.Offers).ProductTitle);
    }

    /// <summary>A file in which nothing carries the reason on its own leaves nobody to mail, rather
    /// than falling back to mailing everybody.</summary>
    [Fact]
    public void AnExportWhereEveryRowNamesAnotherReasonLeavesNoSellers()
    {
        var result = Read(
            "11835;Prodesk;o1;LEGO Icons;5702017829159;LEGO;INACTIVE_IN_MIRAKL,VAT_RATE_MISSING\n" +
            "11476;BL Müzik;o2;Strum Buddy;858445004684;FLUID;PRICE_IS_ZERO\n");

        Assert.Empty(result.Sellers);
        Assert.Equal(0, result.OffersInFile);
        Assert.Equal(2, result.OffersFilteredOut);
    }

    /// <summary>
    /// The cell is a comma-joined list, so the rule is "reduces to exactly this one reason", not
    /// "contains it". Spacing and casing are the export's business, not the seller's.
    /// </summary>
    [Theory]
    [InlineData("VAT_RATE_MISSING", true)]
    [InlineData("  VAT_RATE_MISSING  ", true)]
    [InlineData("vat_rate_missing", true)]
    // A trailing separator writes an empty reason, not a second one.
    [InlineData("VAT_RATE_MISSING,", true)]
    [InlineData("INACTIVE_IN_MIRAKL,VAT_RATE_MISSING", false)]
    [InlineData("VAT_RATE_MISSING,MIRAKL_ZERO_QUANTITY", false)]
    [InlineData("VAT_RATE_MISSING, VAT_RATE_MISSING", false)]
    [InlineData("PRICE_IS_ZERO", false)]
    // A row that states no reason at all is not a row that states this one.
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void AStateReasonCellCountsOnlyWhenItReducesToTheOneReason(string cell, bool expected)
    {
        Assert.Equal(expected, VatSplitBuilder.IsVatRateMissingOnly(cell));
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

    /// <summary>
    /// The attachment is GTIN, title and brand. Without the barcode a seller cannot look the products
    /// up in their own panel, so a two-column list is not worth sending — and the export quietly
    /// renaming this column is precisely how it went out empty before.
    /// </summary>
    [Fact]
    public void AnExportWithNoGtinIsRefusedByName()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Seller;Offer id;Product Title\nProdesk;o1;t\n"));

        var error = Assert.Throws<InvalidOperationException>(() => VatSplitBuilder.Read(stream, "x.csv"));
        Assert.Contains("gtin", error.Message);
    }

    /// <summary>
    /// The column this module selects on. An export that renamed it would match nothing, and rather
    /// than warning nobody the module would fall back to warning everybody — every offer in the file
    /// mailed to its seller as a VAT problem. So the file is refused by name instead.
    /// </summary>
    [Fact]
    public void AnExportWithNoStateReasonsColumnIsRefusedByName()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "Seller;Offer id;Product Title;gtin\nProdesk;o1;t;5702017829159\n"));

        var error = Assert.Throws<InvalidOperationException>(() => VatSplitBuilder.Read(stream, "x.csv"));
        Assert.Contains("State Reasons", error.Message);
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
