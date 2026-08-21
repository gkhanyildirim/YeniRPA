using System.Text;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The reading rules behind Seller Offer Warnings: which row means what, and which table-level
/// problems have to stop a run. Every one of these decides who receives a seller's price list.
/// </summary>
public class SellerMailStoreTests
{
    /// <summary>The operator's own matching workbook, as a CSV — the columns are what matters here,
    /// not the container. <c>TabularFile</c> picks the reader off the extension.</summary>
    static Stream Csv(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    const string OperatorHeaders = "Seller,SellerId,Email,DosyaAdi,LeadTime0,LeadTime1";

    [Fact]
    public void TheOperatorsOwnWorkbookImportsWithoutBeingReshaped()
    {
        var rows = SellerMailStore.ReadWorkbook(
            Csv($"{OperatorHeaders}\nTedarik Türkiye,12421,topcuu@mms-marketplace.com,Tedarik Türkiye.xlsx,0,19700\n"),
            "satici_mail_eslesme.csv");

        var entry = Assert.Single(rows);
        Assert.Equal("Tedarik Türkiye", entry.SellerName);
        Assert.Equal("12421", entry.SellerId);
        Assert.Equal("topcuu@mms-marketplace.com", entry.Email);
        Assert.Equal("Tedarik Türkiye.xlsx", entry.FileName);
        Assert.Equal(0, entry.LeadTime0);
        Assert.Equal(19700, entry.LeadTime1);
    }

    /// <summary>
    /// The orders export writes seller ids as floats. The same normalisation the WhatsApp mapping
    /// applies has to run here, or a row imported from one place stops matching the same seller
    /// imported from the other.
    /// </summary>
    [Fact]
    public void SellerIdsLoseTheFloatTailTheExportGivesThem()
    {
        var rows = SellerMailStore.ReadWorkbook(
            Csv($"{OperatorHeaders}\nAkpa DTM,11616.0,a@b.com,Akpa DTM.xlsx,3,4\n"),
            "mapping.csv");

        Assert.Equal("11616", Assert.Single(rows).SellerId);
    }

    /// <summary>A half-filled row is the operator part-way through the table, not junk. Dropping it
    /// silently would make a seller disappear from a list they were being added to.</summary>
    [Fact]
    public void ARowWithNoAddressOrNoFileIsKept()
    {
        var rows = SellerMailStore.ReadWorkbook(
            Csv($"{OperatorHeaders}\nAnatoptan,10001,,,0,0\nAntenci,10002,x@y.com,,0,0\n"),
            "mapping.csv");

        Assert.Equal(2, rows.Count);
        Assert.Equal("", rows[0].Email);
        Assert.Equal("", rows[1].FileName);
    }

    /// <summary>A row identifying no seller cannot be matched to anything on a later import.</summary>
    [Fact]
    public void ARowWithNoSellerAtAllIsDropped()
    {
        var rows = SellerMailStore.ReadWorkbook(
            Csv($"{OperatorHeaders}\n,,orphan@x.com,Some File.xlsx,1,2\n"),
            "mapping.csv");

        Assert.Empty(rows);
    }

    [Fact]
    public void AMissingEmailColumnIsRefusedByName()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SellerMailStore.ReadWorkbook(
            Csv("Seller,SellerId,DosyaAdi\nAkpa DTM,11616,Akpa DTM.xlsx\n"),
            "mapping.csv"));

        Assert.Contains("Email", error.Message);
    }

    [Fact]
    public void AMissingFileNameColumnIsRefusedByName()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SellerMailStore.ReadWorkbook(
            Csv("Seller,SellerId,Email\nAkpa DTM,11616,a@b.com\n"),
            "mapping.csv"));

        Assert.Contains("DosyaAdi", error.Message);
    }

    /// <summary>Export and re-import is how the operator edits 188 rows, so the sheet this app writes
    /// has to be one it can read back — including the leading zeros on a seller id.</summary>
    [Fact]
    public void TheExportedWorkbookReadsBackUnchanged()
    {
        List<SellerMailEntry> original =
        [
            new("08664", "Ada Beyaz Eşya", "ada@example.com", "ADA BEYAZ ESYA.xlsx", 12, 340),
            new("12421", "Tedarik Türkiye", "topcuu@mms-marketplace.com", "Tedarik Türkiye.xlsx", 0, 19700),
        ];

        using var stream = new MemoryStream(SellerMailStore.BuildWorkbook(original));
        var round = SellerMailStore.ReadWorkbook(stream, "satici-mail-eslesme.xlsx");

        Assert.Equal(original, round);
    }

    /// <summary>
    /// The problem this module exists to avoid: two sellers set up to receive the same offer list.
    /// It has to be named before a run, not discovered by the seller who received it.
    /// </summary>
    [Fact]
    public void TwoSellersSharingOneAttachmentIsReported()
    {
        var warnings = SellerMailStore.FindTableProblems(
        [
            new("1", "Alpha", "alpha@x.com", "Alpha.xlsx", 0, 0),
            new("2", "Beta", "beta@x.com", "alpha.xlsx", 0, 0),
        ]);

        Assert.Contains(warnings, w => w.Contains("Alpha.xlsx") && w.Contains("2 sellers"));
    }

    /// <summary>
    /// One agency running several storefronts is normal, and each of those mails carries a different
    /// seller's offer list — so all of them have to go out. Said out loud, not treated as an error.
    /// </summary>
    [Fact]
    public void AnAddressOnSeveralSellersIsReportedAsExpectedRatherThanRefused()
    {
        var warnings = SellerMailStore.FindTableProblems(
        [
            new("1", "Alpha", "agency@x.com", "Alpha.xlsx", 0, 0),
            new("2", "Beta", "AGENCY@x.com; beta@x.com", "Beta.xlsx", 0, 0),
        ]);

        var warning = Assert.Single(warnings);
        Assert.Contains("2 sellers", warning);
        Assert.Contains("2 separate mails", warning);
    }

    /// <summary>The same person listed twice on one seller is one recipient, not a shared address —
    /// it must not read as two sellers sharing an agency.</summary>
    [Fact]
    public void TheSameAddressTwiceOnOneRowIsNotASharedAddress()
    {
        Assert.Empty(SellerMailStore.FindTableProblems(
        [
            new("1", "Alpha", "a@x.com; A@X.com", "Alpha.xlsx", 0, 0),
        ]));
    }

    // -----------------------------------------------------------------
    // Multiple recipients per seller
    // -----------------------------------------------------------------

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

    [Fact]
    public void ACleanTableReportsNothing()
    {
        Assert.Empty(SellerMailStore.FindTableProblems(
        [
            new("1", "Alpha", "alpha@x.com", "Alpha.xlsx", 0, 0),
            new("2", "Beta", "beta@x.com", "Beta.xlsx", 0, 0),
        ]));
    }

    /// <summary>Blank rows must not collide with each other — half the table looks like this while it
    /// is being filled in.</summary>
    [Fact]
    public void HalfFilledRowsDoNotCountAsDuplicates()
    {
        Assert.Empty(SellerMailStore.FindTableProblems(
        [
            new("1", "Alpha", "", "", 0, 0),
            new("2", "Beta", "", "", 0, 0),
        ]));
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
