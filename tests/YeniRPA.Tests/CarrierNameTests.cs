using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// Carrier canonicalisation. The orders export has no carrier column — the shipping company is typed
/// by hand — so these rules decide whether one carrier reads as one row on the report or as four.
/// </summary>
public class CarrierNameTests
{
    [Theory]
    [InlineData("YURTİÇİ")]
    [InlineData("Yurtiçi")]
    [InlineData("yurtici")]
    [InlineData("YURTICI KARGO")]
    [InlineData("Yurtiçi Kargo")]
    [InlineData("yurticikargo")]
    public void EverySpellingOfOneCarrierFoldsOntoOneName(string typed)
    {
        Assert.Equal("Yurtiçi Kargo", CarrierNames.Resolve(typed, ""));
    }

    [Theory]
    [InlineData("ARAS KARGO", "Aras Kargo")]
    [InlineData("araskargo", "Aras Kargo")]
    [InlineData("sürat kargo", "Sürat Kargo")]
    [InlineData("SÜRAT KARGO", "Sürat Kargo")]
    [InlineData("MNG KARGO", "MNG Kargo")]
    [InlineData("mng kargo", "MNG Kargo")]
    [InlineData("ARÇELİK YETKİLİ SERVİS", "Arçelik Yetkili Servis")]
    [InlineData("Arçelik Yetkili Servisi", "Arçelik Yetkili Servis")]
    public void TurkishSpellingAndCaseDoNotSplitACarrier(string typed, string expected)
    {
        Assert.Equal(expected, CarrierNames.Resolve(typed, ""));
    }

    /// <summary>The export separates these two; operations reconciles them as one carrier.</summary>
    [Theory]
    [InlineData("DHL")]
    [InlineData("DHL e-Commerce")]
    [InlineData("DHL eCommerce")]
    public void DhlAndItsECommerceArmAreOneCarrier(string typed)
    {
        Assert.Equal("DHL", CarrierNames.Resolve(typed, ""));
    }

    /// <summary>
    /// No fuzzy matching, ever: a name the catalogue does not know is never attached to a similar
    /// one. It keeps its own group, which the caller builds from the folded spelling.
    /// </summary>
    [Theory]
    [InlineData("Bilinmeyen Kargo A.Ş.")]
    [InlineData("Firma Sevk Aracı")]
    [InlineData("DEPODAN SEVK")]
    public void AnUnknownCarrierIsNeverGuessedAt(string typed)
    {
        Assert.Null(CarrierNames.Resolve(typed, ""));
    }

    [Fact]
    public void SpellingsOfAnUnknownCarrierStillShareOneKey()
    {
        Assert.Equal(
            CarrierNames.Fold("Bilinmeyen Kargo A.Ş."),
            CarrierNames.Fold("BİLİNMEYEN KARGO A.S."));
    }

    /// <summary>The link is copied from the carrier's own site, so its host outranks the typed name.</summary>
    [Fact]
    public void TrackingUrlNamesTheCarrierWhenTheNameIsMissing()
    {
        Assert.Equal("Yurtiçi Kargo", CarrierNames.Resolve(
            "", "https://www.yurticikargo.com/tr/online-servisler/gonderi-sorgula?code=1"));
    }

    [Fact]
    public void TrackingUrlWinsOverTheTypedName()
    {
        Assert.Equal("Aras Kargo", CarrierNames.Resolve("MNG KARGO", "https://kargotakip.araskargo.com.tr/?code=1"));
    }

    [Fact]
    public void NothingToGoOnResolvesToNothing()
    {
        Assert.Null(CarrierNames.Resolve("", ""));
        Assert.Null(CarrierNames.Resolve("   ", "not a url"));
    }

    /// <summary>
    /// A three-letter alias only matches a whole word, so it cannot fire inside an unrelated name.
    /// </summary>
    [Theory]
    [InlineData("UPS", "UPS")]
    [InlineData("Grupsan Lojistik", null)]
    public void ShortAliasesMatchWholeWordsOnly(string typed, string? expected)
    {
        Assert.Equal(expected, CarrierNames.Resolve(typed, ""));
    }
}
