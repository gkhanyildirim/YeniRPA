using System.Text;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

public class SkuOfferReportBuilderTests
{
    const string OffersHeader =
        "Seller;Seller ID;Offer SKU;Product SKU;Category Label;Brand;Product;Activated;Inactivity reason;Creation date";

    const string KpiHeader =
        "Offer id,Product id,Total Active Offers,Product - Sold items (30d),Product - GMV incl. Shipping (30d)," +
        "Sessions by Product with PDP (30d),Sessions by Product with Add to Cart in pdp (30d)";

    static MemoryStream Csv(params string[] lines) =>
        new(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\n", lines))).ToArray());

    static SkuOfferReport Build(string[] offers, string[] kpis)
    {
        using var o = Csv(offers);
        using var k = Csv(kpis);
        return SkuOfferReportBuilder.Build(o, "offers.csv", k, "kpi.csv");
    }

    static SkuOfferRow Row(SkuOfferReport report, string sku) => report.Rows.Single(r => r.Sku == sku);

    [Fact]
    public void First_offer_is_the_oldest_and_a_closed_one_raises_the_warning()
    {
        var report = Build(
            [OffersHeader,
             "A;1;S2;100;Cam;Sony;Cam X;yes;;03/05/2026 10:00:00 AM",
             "B;2;S1;100;Cam;Sony;Cam X;no;Out of stock;01/27/2026 11:25:55 AM"],
            ["Offer id,Product id,Sessions by Product with PDP (30d)", "1,100,500"]);

        var row = Row(report, "100");
        Assert.Equal(new DateTime(2026, 1, 27, 11, 25, 55), row.FirstOfferDate);
        Assert.False(row.FirstOfferActive);
        Assert.Equal("B", row.FirstOfferSeller);
        Assert.Equal("Out of stock", row.FirstOfferReason);
        Assert.Equal(2, row.TotalOffers);
        Assert.Equal(1, row.ActiveOffers);
        Assert.Equal("Bu ürün için ilk teklif 27.01.2026 tarihinde oluşturulmuş, ancak mevcut durumda teklif kapalıdır.", row.Warning);
        Assert.Equal("warning", row.Severity);
    }

    [Fact]
    public void An_active_first_offer_has_no_warning()
    {
        var report = Build(
            [OffersHeader,
             "A;1;S1;100;Cam;Sony;Cam X;yes;;01/27/2026 10:00:00 AM",
             "B;2;S2;100;Cam;Sony;Cam X;no;Out of stock;03/05/2026 10:00:00 AM"],
            ["Offer id,Product id,Sessions by Product with PDP (30d)", "1,100,10"]);

        Assert.Null(Row(report, "100").Warning);
        Assert.Equal(0, report.Summary.FirstOfferClosedSkus);
    }

    [Fact]
    public void No_active_offer_at_all_is_critical()
    {
        var report = Build(
            [OffersHeader, "A;1;S1;100;Cam;Sony;Cam X;no;Out of stock;01/27/2026 10:00:00 AM"],
            ["Offer id,Product id,Sessions by Product with PDP (30d)", "1,100,10"]);

        Assert.Equal("critical", Row(report, "100").Severity);
        Assert.Equal(1, report.Summary.NoActiveOfferSkus);
    }

    [Fact]
    public void Same_creation_time_is_broken_by_the_lowest_offer_sku()
    {
        var report = Build(
            [OffersHeader,
             "A;1;S9;100;Cam;Sony;Cam X;yes;;01/27/2026 10:00:00 AM",
             "B;2;S1;100;Cam;Sony;Cam X;no;x;01/27/2026 10:00:00 AM"],
            ["Offer id,Product id,Sessions by Product with PDP (30d)", "1,100,10"]);

        Assert.Equal("B", Row(report, "100").FirstOfferSeller);
    }

    [Fact]
    public void Traffic_is_read_once_per_product_and_a_missing_product_is_unknown_not_zero()
    {
        var report = Build(
            [OffersHeader,
             "A;1;S1;100;Cam;Sony;Cam X;yes;;01/27/2026 10:00:00 AM",
             "A;1;S2;200;Cam;Sony;Cam Y;yes;;01/28/2026 10:00:00 AM"],
            [KpiHeader,
             "1,100,2,48,2198514,10343,430",
             "2,100,2,48,2198514,10343,430",
             "3,999,1,0,0,5,1"]);

        var seen = Row(report, "100");
        Assert.True(seen.HasTraffic);
        Assert.Equal(10343, seen.PdpViews);
        Assert.Equal(430, seen.AddToCart);
        Assert.Equal(430d / 10343d, seen.AtcRate!.Value, 6);
        Assert.Equal(48, seen.Sold30d);

        var unseen = Row(report, "200");
        Assert.False(unseen.HasTraffic);
        Assert.Null(unseen.PdpViews);

        Assert.Equal(10343, report.Summary.TotalPdpViews);
        Assert.Equal(1, report.Summary.SkusWithTraffic);
        Assert.Equal(1, report.Summary.SkusWithoutTraffic);
        Assert.Equal(1, report.Summary.TrafficProductsNotInOffers);
    }

    [Fact]
    public void An_impression_column_is_picked_up_when_present()
    {
        var report = Build(
            [OffersHeader, "A;1;S1;100;Cam;Sony;Cam X;yes;;01/27/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP (30d),Impressions (30d)", "100,10,900"]);

        Assert.True(report.Summary.HasImpressions);
        Assert.Equal(900, Row(report, "100").Impressions);
    }

    [Fact]
    public void Floats_from_excel_ids_still_join()
    {
        var report = Build(
            [OffersHeader, "A;1;S1;100.0;Cam;Sony;Cam X;yes;;01/27/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP (30d)", "100,10"]);

        Assert.True(Row(report, "100").HasTraffic);
    }

    [Fact]
    public void Quoted_fields_with_delimiters_and_newlines_survive_streaming()
    {
        var report = Build(
            [OffersHeader, "A;1;S1;100;Cam;Sony;\"Cam; \"\"X\"\"\nline2\";yes;;01/27/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP (30d)", "100,10"]);

        Assert.Equal("Cam; \"X\"\nline2", Row(report, "100").Product);
    }

    [Fact]
    public void Unreadable_dates_are_counted_and_never_raise_a_warning()
    {
        var report = Build(
            [OffersHeader, "A;1;S1;100;Cam;Sony;Cam X;no;x;not a date"],
            ["Product id,Sessions by Product with PDP (30d)", "100,10"]);

        Assert.Null(Row(report, "100").FirstOfferDate);
        Assert.Null(Row(report, "100").Warning);
        Assert.Equal(1, report.Summary.UnreadableDates);
    }

    [Fact]
    public void Swapped_files_name_the_missing_columns()
    {
        using var offers = Csv(KpiHeader, "1,100,2,48,1,1,1");
        using var kpi = Csv(OffersHeader);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SkuOfferReportBuilder.Build(offers, "a.csv", kpi, "b.csv"));
        Assert.Contains("'Product SKU'", ex.Message);
    }

    [Fact]
    public void Query_filters_searches_and_sorts()
    {
        var report = Build(
            [OffersHeader,
             "A;1;S1;100;Cam;Sony;Alpha;no;x;01/27/2026 10:00:00 AM",
             "A;1;S2;200;Cam;Canon;Beta;yes;;01/28/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP (30d)", "200,50"]);

        Assert.Equal(["100"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Filter = "firstClosed" }).Select(r => r.Sku));
        Assert.Equal(["100"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Filter = "noTraffic" }).Select(r => r.Sku));
        Assert.Equal(["200"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Search = "canon" }).Select(r => r.Sku));
        Assert.Equal(["200", "100"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Sort = "pdp", Descending = true }).Select(r => r.Sku));
    }
}
