using System.Text;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

public class SkuOfferReportBuilderTests
{
    const string OffersHeader =
        "Seller;Seller ID;Offer SKU;Product SKU;Category Label;Brand;Product;Activated;Inactivity reason;Creation date";

    const string KpiHeader =
        "Offer id,Product id,Total Active Offers,Sold Qty [Tracked],GMV w/o Shipping  [Tracked]," +
        "Sessions by Product with PDP,Sessions by Product with Add to Cart from PDP";

    static MemoryStream Csv(params string[] lines) =>
        new(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\n", lines))).ToArray());

    const string ApprovalsHeader = "Durum,Ürün Kodu,Tarih,Marka ID,Marka,Kategori ID,Kategori,Barkod,Sorumlu";

    static readonly DateTime Today = new(2026, 10, 13);

    static SkuOfferReport Build(string[] offers, string[] kpis, string[]? approvals = null, DateTime? today = null)
    {
        using var o = Csv(offers);
        using var k = Csv(kpis);
        using var a = Csv(approvals ?? [ApprovalsHeader]);
        return SkuOfferReportBuilder.Build(o, "offers.csv", k, "kpi.csv", a, "approved.csv", today ?? Today);
    }

    static SkuOfferRow Row(SkuOfferReport report, string sku) => report.Rows.Single(r => r.Sku == sku);

    [Fact]
    public void First_offer_is_the_oldest_and_a_closed_one_raises_the_warning()
    {
        var report = Build(
            [OffersHeader,
             "A;1;S2;100;Cam;Sony;Cam X;yes;;03/05/2026 10:00:00 AM",
             "B;2;S1;100;Cam;Sony;Cam X;no;Out of stock;01/27/2026 11:25:55 AM"],
            ["Offer id,Product id,Sessions by Product with PDP", "1,100,500"]);

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
            ["Offer id,Product id,Sessions by Product with PDP", "1,100,10"]);

        Assert.Null(Row(report, "100").Warning);
        Assert.Equal(0, report.Summary.FirstOfferClosedSkus);
    }

    [Fact]
    public void No_active_offer_at_all_is_critical()
    {
        var report = Build(
            [OffersHeader, "A;1;S1;100;Cam;Sony;Cam X;no;Out of stock;01/27/2026 10:00:00 AM"],
            ["Offer id,Product id,Sessions by Product with PDP", "1,100,10"]);

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
            ["Offer id,Product id,Sessions by Product with PDP", "1,100,10"]);

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
        Assert.Equal(48, seen.SoldQty);
        Assert.Equal(2198514, seen.Gmv);

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
            ["Product id,Sessions by Product with PDP,Sessions by Product with Product Impr. in Search / Cat. Search", "100,10,900"]);

        Assert.True(report.Summary.HasImpressions);
        Assert.Equal(900, Row(report, "100").Impressions);
    }

    [Fact]
    public void Floats_from_excel_ids_still_join()
    {
        var report = Build(
            [OffersHeader, "A;1;S1;100.0;Cam;Sony;Cam X;yes;;01/27/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP", "100,10"]);

        Assert.True(Row(report, "100").HasTraffic);
    }

    [Fact]
    public void Quoted_fields_with_delimiters_and_newlines_survive_streaming()
    {
        var report = Build(
            [OffersHeader, "A;1;S1;100;Cam;Sony;\"Cam; \"\"X\"\"\nline2\";yes;;01/27/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP", "100,10"]);

        Assert.Equal("Cam; \"X\"\nline2", Row(report, "100").Product);
    }

    [Fact]
    public void Unreadable_dates_are_counted_and_never_raise_a_warning()
    {
        var report = Build(
            [OffersHeader, "A;1;S1;100;Cam;Sony;Cam X;no;x;not a date"],
            ["Product id,Sessions by Product with PDP", "100,10"]);

        Assert.Null(Row(report, "100").FirstOfferDate);
        Assert.Null(Row(report, "100").Warning);
        Assert.Equal(1, report.Summary.UnreadableDates);
    }

    [Fact]
    public void Swapped_files_name_the_missing_columns()
    {
        using var offers = Csv(KpiHeader, "1,100,2,48,1,1,1");
        using var kpi = Csv(OffersHeader);
        using var approvals = Csv(ApprovalsHeader);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SkuOfferReportBuilder.Build(offers, "a.csv", kpi, "b.csv", approvals, "c.csv", Today));
        Assert.Contains("'Product SKU'", ex.Message);
    }

    [Fact]
    public void Query_filters_searches_and_sorts()
    {
        var report = Build(
            [OffersHeader,
             "A;1;S1;100;Cam;Sony;Alpha;no;x;01/27/2026 10:00:00 AM",
             "A;1;S2;200;Cam;Canon;Beta;yes;;01/28/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP", "200,50"]);

        Assert.Equal(["100"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Filter = "firstClosed" }).Select(r => r.Sku));
        Assert.Equal(["100"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Filter = "noTraffic" }).Select(r => r.Sku));
        Assert.Equal(["200"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Search = "canon" }).Select(r => r.Sku));
        Assert.Equal(["200", "100"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Sort = "pdp", Descending = true }).Select(r => r.Sku));
    }

    [Fact]
    public void Approval_date_and_approver_are_joined_per_product_and_the_earliest_wins()
    {
        var report = Build(
            [OffersHeader,
             "A;1;S1;100;Cam;Sony;Cam X;yes;;01/27/2026 10:00:00 AM",
             "A;1;S2;200;Cam;Sony;Cam Y;yes;;01/28/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP", "100,10"],
            [ApprovalsHeader,
             "ACCEPTED,100,15.03.2026,1,Sony,2,Cam,111,Aysu",
             "ACCEPTED,100,2026-02-01,1,Sony,2,Cam,111,Bora"]);

        var seen = Row(report, "100");
        Assert.True(seen.HasApproval);
        Assert.Equal(new DateTime(2026, 2, 1), seen.ApprovalDate);
        Assert.Equal("Bora", seen.ApprovedBy);

        Assert.False(Row(report, "200").HasApproval);
        Assert.Equal(1, report.Summary.ApprovedSkus);
        Assert.Equal(1, report.Summary.NotApprovedSkus);
    }

    [Fact]
    public void Approvals_without_the_columns_name_what_is_missing()
    {
        using var offers = Csv(OffersHeader);
        using var kpi = Csv("Product id,Sessions by Product with PDP");
        using var approvals = Csv(OffersHeader);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SkuOfferReportBuilder.Build(offers, "a.csv", kpi, "b.csv", approvals, "c.csv", Today));
        Assert.Contains("'Ürün Kodu'", ex.Message);
    }

    [Theory]
    [InlineData("10/13/2026 09:00:00 AM", "baby")]   // same day
    [InlineData("09/14/2026 09:00:00 AM", "baby")]   // just under one month
    [InlineData("09/13/2026 09:00:00 AM", "young")]  // exactly one month
    [InlineData("07/14/2026 09:00:00 AM", "young")]  // just under three months
    [InlineData("07/13/2026 09:00:00 AM", "adult")]  // exactly three months
    [InlineData("04/14/2026 09:00:00 AM", "adult")]  // just under six months
    [InlineData("04/13/2026 09:00:00 AM", "mature")] // exactly six months
    [InlineData("01/01/2025 09:00:00 AM", "mature")]
    public void Age_group_follows_calendar_months_from_the_first_offer(string created, string expected)
    {
        var report = Build(
            [OffersHeader, $"A;1;S1;100;Cam;Sony;Cam X;yes;;{created}"],
            ["Product id,Sessions by Product with PDP", "100,10"]);

        Assert.Equal(expected, Row(report, "100").AgeGroup);
    }

    [Fact]
    public void Age_is_measured_from_the_first_offer_not_a_later_one()
    {
        var report = Build(
            [OffersHeader,
             "A;1;S1;100;Cam;Sony;Cam X;no;x;01/01/2026 10:00:00 AM",
             "A;1;S2;100;Cam;Sony;Cam X;yes;;10/10/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP", "100,10"]);

        Assert.Equal("mature", Row(report, "100").AgeGroup);
        Assert.Equal(1, report.Charts.ByAge.Single(a => a.Group == "mature").Skus);
        Assert.Equal(1, report.Charts.ByAge.Single(a => a.Group == "mature").Closed);
    }

    [Fact]
    public void Age_filter_combines_with_the_other_filters_and_unreadable_dates_have_no_group()
    {
        var report = Build(
            [OffersHeader,
             "A;1;S1;100;Cam;Sony;Old closed;no;x;01/01/2026 10:00:00 AM",
             "A;1;S2;200;Cam;Sony;Old open;yes;;01/01/2026 10:00:00 AM",
             "A;1;S3;300;Cam;Sony;New open;yes;;10/10/2026 10:00:00 AM",
             "A;1;S4;400;Cam;Sony;No date;yes;;not a date"],
            ["Product id,Sessions by Product with PDP", "100,10"]);

        Assert.Equal(["100"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { AgeGroup = "mature", Filter = "firstClosed" }).Select(r => r.Sku));
        Assert.Equal(["300"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { AgeGroup = "baby" }).Select(r => r.Sku));
        Assert.Null(Row(report, "400").AgeGroup);
    }

    [Fact]
    public void Approver_spellings_collapse_to_one_name_and_only_accepted_rows_count()
    {
        var report = Build(
            [OffersHeader,
             "A;1;S1;100;Cam;Sony;Cam X;yes;;01/27/2026 10:00:00 AM",
             "A;1;S2;200;Cam;Sony;Cam Y;yes;;01/28/2026 10:00:00 AM",
             "A;1;S3;300;Cam;Sony;Cam Z;yes;;01/28/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP", "100,10"],
            [ApprovalsHeader,
             "ACCEPTED,100,15.03.2026,1,Sony,2,Cam,111,ALİ ",
             "ACCEPTED,200,15.03.2026,1,Sony,2,Cam,111,zeynep",
             "CHANGES_REQUESTED,300,15.03.2026,1,Sony,2,Cam,111,Ali"]);

        Assert.Equal("Ali", Row(report, "100").ApprovedBy);
        Assert.Equal("Zeynep", Row(report, "200").ApprovedBy);
        Assert.False(Row(report, "300").HasApproval);
    }

    const string StockoutHeader =
        "gtin,Product id,Focus Category Name,Brand,Product Name,Offer condition,Backfilled,Retail buyable,Seller Name,GMV with shipping accepted,Sold items accepted";

    static SkuOfferReport BuildWithStockout(string[] offers, string[] kpis, string[] stockout)
    {
        using var o = Csv(offers);
        using var k = Csv(kpis);
        using var a = Csv(ApprovalsHeader);
        using var s = Csv(stockout);
        return SkuOfferReportBuilder.Build(o, "offers.csv", k, "kpi.csv", a, "approved.csv", Today, s, "stockout.csv");
    }

    [Fact]
    public void Without_a_stockout_file_the_report_has_no_stockout_section()
    {
        var report = Build(
            [OffersHeader, "A;1;S1;100;Cam;Sony;Cam X;yes;;01/27/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP", "100,10"]);

        Assert.Null(report.Stockout);
    }

    [Fact]
    public void Stockout_products_are_joined_to_pdp_and_the_offers_file()
    {
        var report = BuildWithStockout(
            [OffersHeader,
             "A;1;S1;100;Cam;Sony;Cam X;no;x;01/01/2026 10:00:00 AM",
             "A;1;S2;100;Cam;Sony;Cam X;yes;;03/01/2026 10:00:00 AM",
             "A;1;S3;300;Cam;Sony;Cam Z;yes;;10/10/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP,Sessions by Product with Add to Cart from PDP",
             "100,800,40", "300,200,10", "999,1000,5"],
            [StockoutHeader,
             "g1,100,IT HW,SONY,Cam X,NEW,false,true,Alpha,1000.5,2",
             "g2,200,IT HW,CANON,Cam Y,NEW,,false,Beta,500,1",
             "g3,300,Photo,SONY,Cam Z,NEW,,,Gamma,0,0"]);

        var stock = report.Stockout!;
        Assert.Equal(3, stock.Summary.Products);

        var joined = stock.Rows.Single(r => r.ProductId == "100");
        Assert.Equal(800, joined.PdpViews);
        Assert.Equal(40, joined.AddToCart);
        Assert.Equal(0.05, joined.AtcRate!.Value, 6);
        Assert.True(joined.InOffers);
        Assert.Equal(2, joined.TotalOffers);
        Assert.Equal(1, joined.ActiveOffers);
        Assert.True(joined.FirstOfferClosed);
        Assert.Equal("mature", joined.AgeGroup);
        Assert.Equal(true, joined.RetailBuyable);
        Assert.Equal(1000.5, joined.Gmv7d);
        Assert.Equal(2, joined.Sold7d);

        // Not in the KPI file and not in the offers file: unknown, never zero.
        var missing = stock.Rows.Single(r => r.ProductId == "200");
        Assert.False(missing.HasTraffic);
        Assert.Null(missing.PdpViews);
        Assert.False(missing.InOffers);
        Assert.Equal(false, missing.RetailBuyable);

        Assert.Null(stock.Rows.Single(r => r.ProductId == "300").RetailBuyable);

        Assert.Equal(2, stock.Summary.WithTraffic);
        Assert.Equal(1, stock.Summary.WithoutTraffic);
        Assert.Equal(1, stock.Summary.NotInOffers);
        Assert.Equal(1000, stock.Summary.TotalPdpViews);
        Assert.Equal(1000d / 2000d * 100, stock.Summary.SharePctOfAllPdp, 6); // 999 is in KPI, not stockout
        Assert.Equal(["100", "300"], stock.TopPdp.Select(t => t.ProductId));
        Assert.Equal("100", stock.Rows[0].ProductId); // most PDP first
    }

    [Fact]
    public void A_repeated_stockout_product_is_summed_and_its_sellers_listed_once()
    {
        var report = BuildWithStockout(
            [OffersHeader, "A;1;S1;100;Cam;Sony;Cam X;yes;;01/27/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP", "100,10"],
            [StockoutHeader,
             "g,100,IT HW,SONY,Cam X,NEW,,true,Alpha,100,1",
             "g,100,IT HW,SONY,Cam X,NEW,,true,Beta,50,2",
             "g,100,IT HW,SONY,Cam X,NEW,,true,Alpha,25,1"]);

        var row = report.Stockout!.Rows.Single();
        Assert.Equal(175, row.Gmv7d);
        Assert.Equal(4, row.Sold7d);
        Assert.Equal("Alpha, Beta", row.Sellers);
    }

    [Fact]
    public void A_stockout_file_without_product_id_names_the_missing_column()
    {
        using var offers = Csv(OffersHeader);
        using var kpi = Csv("Product id,Sessions by Product with PDP");
        using var approvals = Csv(ApprovalsHeader);
        using var stockout = Csv(OffersHeader);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SkuOfferReportBuilder.Build(offers, "a.csv", kpi, "b.csv", approvals, "c.csv", Today, stockout, "d.csv"));
        Assert.Contains("'Product id'", ex.Message);
    }

    const string SalesHeader =
        "Product id,Product Name,Sold Qty [Tracked],GMV w/o Shipping  [Tracked],Sessions by Product with Orders [Tracked]," +
        "Sessions by Product with Product Impr. in Search / Cat. Search,Sessions by Product with Product Clicks in Search / Cat. Search," +
        "Sessions by Product with PDP,Sessions by Product with Add to Cart from PDP,Sessions by Product in Checkout - Summary," +
        "GMV | 1 Eyl - 30 Kas,Adet | 1 Eyl - 30 Kas,GMV | 31 Agu - 14 Eki,Adet | 31 Agu - 14 Eki";

    static SkuOfferReport BuildFromSales(string[] offers, string[] sales, double rate = 1)
    {
        using var o = Csv(offers);
        using var k = Csv(sales);
        using var a = Csv(ApprovalsHeader);
        return SkuOfferReportBuilder.Build(o, "offers.csv", k, "sales.csv", a, "approved.csv", Today, eurTryRate: rate);
    }

    static readonly string[] TwoSkuOffers =
    [
        OffersHeader,
        "A;1;S1;100;Cam;Sony;Cam X;yes;;01/27/2026 10:00:00 AM",
        "A;1;S2;200;Cam;Sony;Cam Y;no;x;01/28/2026 10:00:00 AM",
        "A;1;S3;300;Cam;Sony;Cam Z;yes;;01/29/2026 10:00:00 AM",
    ];

    [Fact]
    public void Combined_sales_funnel_rates_are_recomputed_from_the_session_counts()
    {
        var report = BuildFromSales(TwoSkuOffers,
            [SalesHeader,
             // impressions 10000, clicks 500, pdp 400, atc 40, checkout 20, orders 10; sold 12 for 6000; periods 4/2000 and 8/4000
             "100,Cam X,12,6000,10,10000,500,400,40,20,2000,4,4000,8"]);

        var r = Row(report, "100");
        Assert.Equal(10000, r.Impressions);
        Assert.Equal(0.05, r.Ctr!.Value, 6);
        Assert.Equal(0.10, r.AtcRate!.Value, 6);
        Assert.Equal(10d / 400d, r.CrPdp!.Value, 6);
        Assert.Equal(10d / 20d, r.CrCheckout!.Value, 6);
        Assert.Equal(12, r.SoldQty);
        Assert.Equal(6000, r.Gmv);
        Assert.Equal([4d, 8d], r.PeriodQty!.Select(v => v!.Value).ToArray());
        Assert.Equal([2000d, 4000d], r.PeriodGmv!.Select(v => v!.Value).ToArray());
    }

    [Fact]
    public void Blank_cells_stay_unknown_and_a_zero_denominator_gives_no_rate()
    {
        var report = BuildFromSales(TwoSkuOffers,
            [SalesHeader, "200,Cam Y,,,,0,0,0,0,,,,,"]);

        var r = Row(report, "200");
        Assert.True(r.HasTraffic);
        Assert.Null(r.SoldQty);
        Assert.Null(r.Gmv);
        Assert.Null(r.PeriodQty);
        Assert.Null(r.Ctr);
        Assert.Null(r.CrPdp);
    }

    [Fact]
    public void Unique_skus_sold_counts_each_product_once_overall_and_per_period()
    {
        var report = BuildFromSales(TwoSkuOffers,
            [SalesHeader,
             "100,Cam X,12,6000,10,100,10,50,5,2,2000,4,4000,8",   // sold in both periods
             "200,Cam Y,3,900,3,100,10,50,5,2,900,3,,",            // first period only
             "300,Cam Z,,,,100,10,50,5,2,,,,",                      // traffic, never sold
             "999,Gone,5,500,5,100,10,50,5,2,,,500,5"]);           // sold, but not in the offers file

        var s = report.Summary;
        Assert.Equal(3, s.UniqueSkusSold);          // 100, 200, 999
        Assert.Equal(2, s.UniqueSkusSoldInOffers);  // 100, 200
        Assert.Equal(20, s.TotalSoldQty);
        Assert.Equal(7400, s.TotalGmv);
        Assert.Equal("1 Eyl - 14 Eki", s.SalesWindow);

        Assert.Equal(["1 Eyl - 30 Kas", "31 Agu - 14 Eki"], report.Charts.Periods.Select(p => p.Label));
        Assert.Equal(2, report.Charts.Periods[0].UniqueSkusSold);
        Assert.Equal(2, report.Charts.Periods[1].UniqueSkusSold);   // 100 and 999
        Assert.Equal(7, report.Charts.Periods[0].Qty);
        Assert.Equal(13, report.Charts.Periods[1].Qty);

        Assert.Equal(1, s.PdpButNeverSoldSkus);     // 300
    }

    [Fact]
    public void Traffic_on_products_without_an_active_offer_is_reported_as_lost()
    {
        var report = BuildFromSales(TwoSkuOffers,
            [SalesHeader,
             "100,Cam X,1,10,1,100,10,40,5,2,,,,",
             "200,Cam Y,,,,100,10,60,5,2,,,,"]);        // 200 has no active offer

        Assert.Equal(1, report.Summary.LostTrafficSkus);
        Assert.Equal(60, report.Summary.LostTrafficPdp);
        Assert.Equal(["200"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Filter = "lostTraffic" }).Select(r => r.Sku));
        Assert.Equal(["100"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Filter = "sold" }).Select(r => r.Sku));
        Assert.Equal(["200"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Filter = "neverSold" }).Select(r => r.Sku));
    }

    [Fact]
    public void Overall_rates_come_from_the_whole_sales_file()
    {
        var report = BuildFromSales(TwoSkuOffers,
            [SalesHeader,
             "100,A,1,10,10,1000,100,200,20,5,,,,",
             "999,B,1,10,10,3000,100,200,20,5,,,,"]);

        Assert.Equal(200d / 4000d, report.Summary.OverallCtr, 6);
        Assert.Equal(40d / 400d, report.Summary.OverallAtcRate, 6);
        Assert.Equal(20d / 400d, report.Summary.OverallCrPdp, 6);
        Assert.Equal(4000, report.Charts.Funnel.Impressions);
    }

    [Fact]
    public void A_sales_file_without_pdp_sessions_names_the_missing_column()
    {
        using var offers = Csv(OffersHeader);
        using var sales = Csv("Product id,Product Name");
        using var approvals = Csv(ApprovalsHeader);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SkuOfferReportBuilder.Build(offers, "a.csv", sales, "b.csv", approvals, "c.csv", Today));
        Assert.Contains("'Sessions by Product with PDP'", ex.Message);
    }

    [Fact]
    public void Active_offer_sellers_are_listed_when_the_first_offer_is_closed()
    {
        var report = Build(
            [OffersHeader,
             "Alpha;1;S1;100;Cam;Sony;Cam X;no;Out of stock;01/01/2026 10:00:00 AM",
             "Beta;2;S2;100;Cam;Sony;Cam X;yes;;02/01/2026 10:00:00 AM",
             "Beta;2;S3;100;Cam;Sony;Cam X;yes;;03/01/2026 10:00:00 AM",
             "Gamma;3;S4;100;Cam;Sony;Cam X;no;x;04/01/2026 10:00:00 AM",
             "Delta;4;S5;100;Cam;Sony;Cam X;yes;;05/01/2026 10:00:00 AM"],
            ["Product id,Sessions by Product with PDP", "100,10"]);

        var row = Row(report, "100");
        Assert.Equal("Alpha", row.FirstOfferSeller);
        Assert.False(row.FirstOfferActive);
        Assert.Equal(3, row.ActiveOffers);
        Assert.Equal(["Beta", "Delta"], row.ActiveSellers); // Beta holds two offers but is named once
        Assert.Equal(["100"], SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery { Search = "delta" }).Select(r => r.Sku));
    }

    [Fact]
    public void Every_active_seller_is_kept_and_a_product_with_none_active_has_none()
    {
        var offers = new List<string> { OffersHeader };
        for (var i = 1; i <= 6; i++)
            offers.Add($"Seller{i};{i};S{i};100;Cam;Sony;Cam X;yes;;01/{i:00}/2026 10:00:00 AM");
        offers.Add("Closed;9;S9;200;Cam;Sony;Cam Y;no;x;01/01/2026 10:00:00 AM");

        var report = Build([.. offers], ["Product id,Sessions by Product with PDP", "100,10"]);

        Assert.Equal(["Seller1", "Seller2", "Seller3", "Seller4", "Seller5", "Seller6"], Row(report, "100").ActiveSellers);
        Assert.Empty(Row(report, "200").ActiveSellers);
    }

    const string MoneyHeader =
        "Product id,Sessions by Product with PDP,Sessions by Product with Orders [Tracked],Ø Product Price,AOV," +
        "GMV w/o Shipping  [Tracked],Sold Qty [Tracked],GMV | 1 Eyl - 30 Kas,Adet | 1 Eyl - 30 Kas";

    [Fact]
    public void The_eur_try_rate_converts_every_money_figure_but_no_count()
    {
        var report = BuildFromSales(TwoSkuOffers,
            [MoneyHeader, "100,50,5,10,12,600,60,200,20"], rate: 40);

        var r = Row(report, "100");
        Assert.Equal(24_000, r.Gmv);        // 600 EUR
        Assert.Equal(400, r.AvgPrice);      // 10 EUR
        Assert.Equal(480, r.Aov);           // 12 EUR
        Assert.Equal(8_000, r.PeriodGmv![0]);
        Assert.Equal(60, r.SoldQty);        // a quantity, not money
        Assert.Equal(50, r.PdpViews);
        Assert.Equal(5, r.OrderSessions);
        Assert.Equal(5d / 50d, r.CrPdp!.Value, 6);

        Assert.Equal(24_000, report.Summary.TotalGmv);
        Assert.Equal(8_000, report.Charts.Periods[0].Gmv);
        Assert.Equal(20, report.Charts.Periods[0].Qty);  // period quantity untouched
        Assert.Equal(60, report.Summary.TotalSoldQty);
        Assert.Equal(40, report.Summary.EurTryRate);
    }

    [Fact]
    public void A_rate_of_one_leaves_the_sales_file_amounts_as_they_are()
    {
        var report = BuildFromSales(TwoSkuOffers, [MoneyHeader, "100,50,5,10,12,600,60,200,20"]);

        Assert.Equal(600, Row(report, "100").Gmv);
        Assert.Equal(10, Row(report, "100").AvgPrice);
    }

    [Fact]
    public void The_stockout_files_own_gmv_is_already_try_and_is_not_converted()
    {
        using var o = Csv(TwoSkuOffers);
        using var k = Csv(MoneyHeader, "100,50,5,10,12,600,60,200,20");
        using var a = Csv(ApprovalsHeader);
        using var s = Csv(StockoutHeader, "g,100,IT HW,SONY,Cam X,NEW,,true,Alpha,1000,2");

        var report = SkuOfferReportBuilder.Build(o, "o.csv", k, "k.csv", a, "a.csv", Today, s, "s.csv", 40);

        var row = report.Stockout!.Rows.Single();
        Assert.Equal(1000, row.Gmv7d);      // from the stockout file: untouched
        Assert.Equal(24_000, row.Gmv);      // from the sales file: converted
    }

    [Theory]
    [InlineData("48.5", 48.5)]
    [InlineData("48,5", 48.5)]
    [InlineData(" 50 ", 50)]
    [InlineData("1000", 1000)]
    public void A_typed_rate_is_read_with_a_point_or_a_comma(string text, double expected)
    {
        Assert.True(YeniRPA.Web.Controllers.SkuOfferReportController.TryParseRate(text, out var rate));
        Assert.Equal(expected, rate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1001")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1.234,5")]
    public void An_empty_or_unusable_rate_is_refused(string? text)
    {
        Assert.False(YeniRPA.Web.Controllers.SkuOfferReportController.TryParseRate(text, out _));
    }
}