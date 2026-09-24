using System.Text;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// How Custom Mail reads which sellers to reach out of the uploaded seller list — an id and a name
/// per row, with no address of its own. <see cref="SellerMailDirectoryTests"/> covers the matching
/// half this list is joined against.
/// </summary>
public class CustomMailSellerListReaderTests
{
    [Fact]
    public void EveryRowWithAnIdOrNameBecomesASeller()
    {
        using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes("Kimlik;Satıcı\n8610;Ebrar Bilgisayar\n9012;Nethouse\n"));

        var sellers = CustomMailSellerListReader.Read(stream, "shops.csv");

        Assert.Equal(2, sellers.Count);
        Assert.Contains(sellers, s => s is { SellerId: "8610", SellerName: "Ebrar Bilgisayar" });
        Assert.Contains(sellers, s => s is { SellerId: "9012", SellerName: "Nethouse" });
    }

    /// <summary>The seller list's own e-mail column, when it has one, is not part of what this reader
    /// extracts — Custom Mail resolves every address from a separate directory instead.</summary>
    [Fact]
    public void AnEmailColumnOnTheSellerListIsIgnored()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "Kimlik;Satıcı;E-posta\n8610;Ebrar Bilgisayar;stale-address@example.com\n"));

        var sellers = CustomMailSellerListReader.Read(stream, "shops.csv");

        Assert.Single(sellers);
        Assert.Equal("8610", sellers[0].SellerId);
        Assert.Equal("Ebrar Bilgisayar", sellers[0].SellerName);
    }

    [Fact]
    public void TheSameSellerOnTwoRowsIsOneSeller()
    {
        using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes("Kimlik;Satıcı\n8610;Ebrar Bilgisayar\n8610;Ebrar Bilgisayar\n"));

        var sellers = CustomMailSellerListReader.Read(stream, "shops.csv");

        Assert.Single(sellers);
    }

    [Fact]
    public void ARowWithNeitherAnIdNorANameIsSkipped()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Kimlik;Satıcı\n;\n8610;Ebrar Bilgisayar\n"));

        var sellers = CustomMailSellerListReader.Read(stream, "shops.csv");

        Assert.Single(sellers);
    }

    /// <summary>Without an id or a name column the file cannot identify a single seller, and saying
    /// so beats silently reporting zero sellers.</summary>
    [Fact]
    public void AFileWithNeitherAnIdNorANameColumnIsRefusedByName()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Durum;Para Birimi\nAçık;TRY\n"));

        var error = Assert.Throws<InvalidOperationException>(
            () => CustomMailSellerListReader.Read(stream, "shops.csv"));

        Assert.Contains("shops.csv", error.Message);
    }

    [Fact]
    public void AnEmptyFileIsRefusedByName()
    {
        using var stream = new MemoryStream([]);

        var error = Assert.Throws<InvalidOperationException>(
            () => CustomMailSellerListReader.Read(stream, "shops.csv"));

        Assert.Contains("empty", error.Message);
    }
}
