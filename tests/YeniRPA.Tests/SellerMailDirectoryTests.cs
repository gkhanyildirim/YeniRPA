using System.Text;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// How a seller in the offer export is matched to an address in the uploaded list.
///
/// <para>The measured cost of matching only exactly is eight sellers out of 131 that have to be
/// entered by hand once. The cost of relaxing it is in
/// <see cref="ASellerWhoIsNotInTheListIsNeverMatchedToASimilarName"/>.</para>
/// </summary>
public class SellerMailDirectoryTests
{
    const string Headers = "Satıcı;Mail";

    static SellerMailDirectory Read(string body)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"{Headers}\n{body}"));
        return SellerMailDirectory.Read(stream, "adresler.csv", null);
    }

    [Fact]
    public void AnAddressIsFoundByTheSellersName()
    {
        var directory = Read("Prodesk;info@prodesk.com\nNethouse;bilgi@nethouse.com\n");

        Assert.Equal(2, directory.RowCount);
        Assert.Equal("info@prodesk.com", directory.Find("11835", "Prodesk").Email);
        Assert.Null(directory.Find("11835", "Prodesk").Problem);
    }

    /// <summary>
    /// <b>The test this whole design turns on.</b> "Yazıcı Bende" is a real seller in the export and is
    /// in no address list; "Yazıcı Ticaret" is a different, real company. Any rule loose enough to
    /// connect them — prefix, contains, edit distance — mails one seller's complete price and stock
    /// list to the other. Nothing is returned but a problem.
    /// </summary>
    [Fact]
    public void ASellerWhoIsNotInTheListIsNeverMatchedToASimilarName()
    {
        var directory = Read("Yazıcı Ticaret;huseyin@example.com\nHepsiyazıcı;bilgi@example.com\n");

        var match = directory.Find("12482", "Yazıcı Bende");

        Assert.Null(match.Email);
        Assert.NotNull(match.Problem);
    }

    /// <summary>
    /// The same widening <see cref="SellerGroupMap.FoldName"/> exists for: no built-in comparison gets
    /// the Turkish i-family right, and every human spelling of these names has to collide or the
    /// operator who types it in one case misses.
    /// </summary>
    [Theory]
    [InlineData("FırsatKurdu")]
    [InlineData("FIRSATKURDU")]
    [InlineData("fırsatkurdu")]
    [InlineData("Firsatkurdu")]
    public void EveryHumanSpellingOfATurkishNameFindsTheSameRow(string spelling)
    {
        var directory = Read("FırsatKurdu;satis@firsatkurdu.com\n");

        Assert.Equal("satis@firsatkurdu.com", directory.Find("", spelling).Email);
    }

    /// <summary>
    /// Two rows, one name, two different addresses. Picking one would be picking whose inbox a price
    /// list lands in, so neither is used and the operator is told which name to fix.
    /// </summary>
    [Fact]
    public void TwoDifferentAddressesForOneNameStopThatSeller()
    {
        var directory = Read("Prodesk;info@prodesk.com\nProdesk;satis@prodesk.com\n");

        var match = directory.Find("", "Prodesk");

        Assert.Null(match.Email);
        Assert.Contains("two different", match.Problem);
        Assert.Contains(directory.Warnings, w => w.Contains("prodesk"));
    }

    /// <summary>A duplicated row is a duplicated row, not a conflict — the address is the same one.</summary>
    [Fact]
    public void TheSameAddressOnTwoRowsIsNotAConflict()
    {
        var directory = Read("Prodesk;info@prodesk.com\nProdesk;INFO@Prodesk.com\n");

        Assert.Equal("info@prodesk.com", directory.Find("", "Prodesk").Email);
        Assert.Empty(directory.Warnings);
    }

    /// <summary>
    /// The real onboarding workbook is full of "#N/A" and "#REF!" where a lookup broke. Treating one
    /// as an address would put it in a To line; treating it as "this seller has an address" would hide
    /// the seller from the list of ones needing a hand.
    /// </summary>
    [Fact]
    public void SpreadsheetErrorCellsAreNotAddresses()
    {
        var directory = Read("Miniöde;#REF!\nMars İletişim;#N/A\nProdesk;info@prodesk.com\n");

        Assert.Equal(1, directory.RowCount);
        Assert.Null(directory.Find("", "Miniöde").Email);
        Assert.Contains(directory.Warnings, w => w.Contains("spreadsheet error"));
    }

    /// <summary>A seller usually has several users in the back office and they all belong on one mail,
    /// so a cell holding a list comes out canonicalised rather than rejected.</summary>
    [Fact]
    public void SeveralUsersInOneCellBecomeOneToLine()
    {
        var directory = Read("Prodesk;\"info@prodesk.com, satis@prodesk.com; info@prodesk.com\"\n");

        // One separator, no repeats, original order.
        Assert.Equal("info@prodesk.com; satis@prodesk.com", directory.Find("", "Prodesk").Email);
    }

    /// <summary>Ids are stable; a storefront name changes whenever a seller edits it. So when the list
    /// carries ids, the id decides.</summary>
    [Fact]
    public void TheSellerIdWinsOverTheName()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "Seller id;Satıcı;Mail\n11835;Prodesk;byid@example.com\n;Prodesk Eski;byname@example.com\n"));

        var directory = SellerMailDirectory.Read(stream, "adresler.csv", null);

        Assert.Equal("byid@example.com", directory.Find("11835", "Prodesk Eski").Email);
    }

    [Fact]
    public void ASellerMissingFromTheListIsReportedRatherThanLeftBlank()
    {
        var match = Read("Prodesk;info@prodesk.com\n").Find("13346", "Karataş Online");

        Assert.Null(match.Email);
        Assert.Contains("not in the uploaded address list", match.Problem);
    }

    /// <summary>Without an address column the file is not an address list, and saying so beats
    /// reporting 131 sellers as unmatched.</summary>
    [Fact]
    public void AListWithNoAddressColumnIsRefusedByName()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Satıcı;Telefon\nProdesk;5551234567\n"));

        var error = Assert.Throws<InvalidOperationException>(
            () => SellerMailDirectory.Read(stream, "adresler.csv", null));

        Assert.Contains("Mail", error.Message);
    }
}
