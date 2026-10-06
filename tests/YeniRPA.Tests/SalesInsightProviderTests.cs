using ClosedXML.Excel;
using YeniRPA.Web.Services.SalesAnalysis;
using YeniRPA.Web.Services.SalesAnalysis.Providers;
using static YeniRPA.Tests.SalesAnalysisFixtures;

namespace YeniRPA.Tests;

/// <summary>
/// The insight providers, the outlier switch, the Reasons composer and the service's caching and
/// lazy sections — on small hand-built periods.
/// </summary>
public class SalesInsightProviderTests
{
    // A: 400 (phones 300, TVs 100). B: 660 (phones 600, TVs 50, cables 10).
    static readonly SalesLine[] A =
    [
        Line("a1", "P1", 10, 300, category: "SMARTPHONES", brand: "APPLE", seller: "S1"),
        Line("a2", "T1", 1, 100, category: "TELEVISIONS", brand: "LG", seller: "S2"),
    ];

    static readonly SalesLine[] B =
    [
        Line("b1", "P1", 20, 600, day: 1, category: "SMARTPHONES", brand: "APPLE", seller: "S1"),
        Line("b2", "T1", 1, 50, day: 1, category: "TELEVISIONS", brand: "LG", seller: "S2"),
        Line("b3", "C1", 1, 10, day: 1, category: "CABLES", brand: "TTEC", seller: "S3"),
    ];

    static DimensionData Category(SalesAnalysisContext ctx) => (DimensionData)new CategoryProvider().Compute(ctx).Data!;

    [Fact]
    public void Category_contributions_add_up_to_the_whole_change()
    {
        var data = Category(Context(A, B, otherThresholdPct: 0));

        Assert.Equal(1, data.Rows.Sum(r => r.Contribution ?? 0), 10);
        var phones = data.Rows.Single(r => r.Key == "SMARTPHONES");
        Assert.Equal("Smartphones", phones.Label);
        Assert.Equal(300, phones.Delta);
        Assert.Equal(300.0 / 260.0, phones.Contribution!.Value, 10);
    }

    [Fact]
    public void Small_categories_fold_into_other_without_losing_sales()
    {
        var lines = Enumerable.Range(0, 10)
            .Select(i => Line("x" + i, "S" + i, 1, 1, day: 1, category: "SMALL" + i))
            .Concat(B)
            .ToList();

        var data = Category(Context(A, lines, otherThresholdPct: 2));

        var other = Assert.Single(data.Rows, r => r.Key == DimensionBreakdown.OtherKey);
        Assert.Equal(11, other.Members);            // the ten SMALL* plus CABLES (10 of 670, under 2%)
        Assert.Equal(670, data.Rows.Sum(r => r.SalesB), 10);
        Assert.Equal(1, data.Rows.Sum(r => r.Contribution ?? 0), 10);
    }

    [Fact]
    public void Concentration_reports_the_top_share_in_both_periods()
    {
        var rows = DimensionBreakdown.Build(A, B, l => l.Seller, k => k);
        var c = DimensionBreakdown.TopShare(rows, 1);

        Assert.Equal(0.75, c.ShareA);
        Assert.Equal(600.0 / 660.0, c.ShareB!.Value, 10);
    }

    [Fact]
    public void Driver_sentence_is_built_from_the_numbers()
    {
        var result = new CategoryProvider().Compute(Context(A, B));

        var finding = result.Findings[0];
        Assert.StartsWith("Smartphones kategorisinden gelen artış, toplam artıştan bile büyük", finding.Text);
        Assert.Equal(300, finding.Impact);
    }

    [Fact]
    public void Products_new_in_B_and_lost_from_A_are_listed()
    {
        var data = (ProductData)new ProductProvider().Compute(Context(A, B)).Data!;

        Assert.Equal("C1", Assert.Single(data.NewProducts).Sku);
        Assert.Empty(data.LostProducts);
        Assert.Equal(1, data.NewTotal.Count);
        Assert.Equal("P1", data.TopBySales[0].Sku);
    }

    [Fact]
    public void Pvm_provider_reconciles_to_the_kpi_difference()
    {
        var ctx = Context(A, B);
        var pvm = (PvmData)new PvmProvider().Compute(ctx).Data!;

        Assert.Equal(ctx.SalesDelta, pvm.Volume + pvm.Price + pvm.Mix, 6);
    }

    [Fact]
    public void Reasons_headline_states_the_move_in_turkish()
    {
        var ctx = Context(A, B);
        IInsightProvider[] providers = [new PvmProvider(), new CategoryProvider(), new ProductProvider()];

        var reasons = (ReasonsData)ReasonsComposer.Compose(ctx, providers.Select(p => p.Compute(ctx))).Data!;

        Assert.StartsWith("Satışlar %65,0 arttı: 400 TL'den 660 TL'ye çıktı.", reasons.Headline);
        Assert.InRange(reasons.Reasons.Count, 1, 5);
        Assert.Equal(reasons.Reasons.OrderByDescending(r => Math.Abs(r.Impact)).Select(r => r.Text), reasons.Reasons.Select(r => r.Text));
    }

    [Fact]
    public void Reasons_with_an_empty_base_period_never_divide_by_zero()
    {
        var ctx = Context([], B);
        IInsightProvider[] providers = [new KpiProvider(), new PvmProvider(), new CategoryProvider(), new OutlierProvider(), new StatusProvider()];

        var reasons = (ReasonsData)ReasonsComposer.Compose(ctx, providers.Select(p => p.Compute(ctx))).Data!;

        Assert.Contains("Dönem A'da hiç satış yok", reasons.Headline);
        Assert.DoesNotContain(reasons.Reasons, r => r.Text.Contains("NaN") || r.Text.Contains("∞"));
    }

    [Fact]
    public void Outlier_top_one_percent_drops_the_single_largest_order_from_both_periods()
    {
        var a = Enumerable.Range(0, 50).Select(i => Line("a" + i, "X", 1, 100)).ToList();
        var b = Enumerable.Range(0, 49).Select(i => Line("b" + i, "X", 1, 100, day: 1))
            .Append(Line("big", "IPHONE", 1, 236_000, day: 1)).ToList();

        var (fa, fb, exclusion) = OutlierFilter.Apply(a, b, DefaultStatuses(), OutlierFilter.TopOnePercent);

        Assert.Equal(1, exclusion!.Orders);
        Assert.Equal(236_000, exclusion.Amount);
        Assert.Equal(50, fa.Count);
        Assert.DoesNotContain(fb, l => l.OrderNumber == "big");
    }

    [Fact]
    public void Outlier_iqr_fence_drops_only_orders_above_it()
    {
        var a = Enumerable.Range(0, 20).Select(i => Line("a" + i, "X", 1, 100 + i)).ToList();
        var b = new List<SalesLine> { Line("big", "X", 1, 10_000, day: 1) };

        var (_, fb, exclusion) = OutlierFilter.Apply(a, b, DefaultStatuses(), OutlierFilter.Iqr);

        Assert.Equal(1, exclusion!.Orders);
        Assert.Empty(fb);
    }

    [Fact]
    public void Outlier_provider_shows_how_much_of_the_change_one_order_explains()
    {
        var a = Enumerable.Range(0, 10).Select(i => Line("a" + i, "X", 1, 100)).ToList();
        var b = Enumerable.Range(0, 10).Select(i => Line("b" + i, "X", 1, 100, day: 1))
            .Append(Line("big", "IPHONE", 1, 5_000, day: 1)).ToList();

        var result = new OutlierProvider().Compute(Context(a, b));
        var data = (OutlierData)result.Data!;

        var top1 = data.Scenarios[0];
        Assert.Equal(5_000, data.Delta);
        Assert.Equal(100, top1.DeltaWithout);       // B without "big" (1000) − A without one 100 order (900)
        Assert.Equal(0.98, top1.ShareOfDelta!.Value, 10);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public void Status_provider_compares_cancellation_rates()
    {
        var a = new[] { Line("a1", "X", 1, 100), Line("a2", "X", 0, 0, status: "Canceled", canceledAmount: 100) };
        var b = new[] { Line("b1", "X", 1, 100, day: 1), Line("b2", "X", 0, 0, day: 1, status: "Canceled", canceledAmount: 100),
                        Line("b3", "X", 0, 0, day: 1, status: "Canceled", canceledAmount: 100), Line("b4", "X", 0, 0, day: 1, status: "Canceled", canceledAmount: 100) };

        var result = new StatusProvider().Compute(Context(a, b));

        Assert.StartsWith("İptal oranı yükseldi: Dönem A'da satırların %50,0 kadarı iptal edilmişti, Dönem B'de %75,0 kadarı.", result.Summary[0]);
    }

    [Fact]
    public void Time_series_aligns_both_periods_on_a_shared_index()
    {
        var a = new[] { Line("a1", "X", 1, 100, day: 0, hour: 10), Line("a2", "X", 1, 50, day: 1, hour: 10) };
        var b = new[] { Line("b1", "X", 1, 70, day: 4, hour: 10) };

        var data = (TimeSeriesData)new TimeSeriesProvider().Compute(Context(a, b, days: 4)).Data!;

        Assert.Equal("day", data.Granularity);
        Assert.Equal(4, data.Points.Count);
        Assert.Equal(100, data.Points[0].SalesA);
        Assert.Equal(70, data.Points[0].SalesB);
        Assert.Equal("02.10.2026", data.PeakB!.Date);
        Assert.Equal(0, data.LowB!.Sales);
    }

    // ---------------------------------------------------------------------------------------------
    // Service: lazy sections and the result cache.
    // ---------------------------------------------------------------------------------------------

    static SalesAnalysisService Service() => new(new SalesAnalysisStore(),
    [
        new KpiProvider(), new PvmProvider(), new CategoryProvider(), new ProductProvider(), new BrandProvider(),
        new SellerProvider(), new StatusProvider(), new TimeSeriesProvider(), new CityProvider(),
        new ProfitabilityProvider(), new OutlierProvider(),
    ]);

    static MemoryStream SmallExport()
    {
        string[] headers = ["Date created", "Order number", "Status", "Quantity", "Amount", "Product SKU", "Category label"];
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Data");
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];

        var rows = new (string Date, string Order, double Qty, double Amount)[]
        {
            ("09/28/2026 10:00:00 AM", "O1", 1, 100), ("09/29/2026 10:00:00 AM", "O2", 2, 300),
            ("09/30/2026 10:00:00 AM", "O3", 1, 150), ("10/01/2026 10:00:00 AM", "O4", 1, 250),
        };
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Cell(r + 2, 1).Value = rows[r].Date;
            sheet.Cell(r + 2, 2).Value = rows[r].Order;
            sheet.Cell(r + 2, 3).Value = "Received";
            sheet.Cell(r + 2, 4).Value = rows[r].Qty;
            sheet.Cell(r + 2, 5).Value = rows[r].Amount;
            sheet.Cell(r + 2, 6).Value = "SKU" + r;
            sheet.Cell(r + 2, 7).Value = "PHONES";
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Load_defaults_to_the_later_half_against_the_half_before_it()
    {
        using var stream = SmallExport();
        var load = Service().Load(stream, "orders.xlsx");

        Assert.Equal(new DateOnly(2026, 9, 28), load.DefaultA.From);
        Assert.Equal(new DateOnly(2026, 9, 29), load.DefaultA.To);
        Assert.Equal(new DateOnly(2026, 9, 30), load.DefaultB.From);
        Assert.Equal(new DateOnly(2026, 10, 1), load.DefaultB.To);
        Assert.Equal(["Received"], load.DefaultStatuses);
    }

    [Fact]
    public void Analyze_computes_only_the_requested_sections_and_caches_them()
    {
        var service = Service();
        using var stream = SmallExport();
        var load = service.Load(stream, "orders.xlsx");
        var request = new SalesAnalysisRequest(load.Token, load.DefaultA, load.DefaultB, null, null, null, null, "none", null, ["kpi"]);

        var first = service.Analyze(request);
        var second = service.Analyze(request);

        Assert.Equal(["kpi"], first.Sections.Keys);
        Assert.Same(first.Sections["kpi"], second.Sections["kpi"]);
        Assert.True(first.WeakData);
        Assert.Contains(first.Warnings, w => w.Contains("yanıltıcı olabilir"));
    }

    [Fact]
    public void Analyze_refuses_a_stale_token()
    {
        var service = Service();
        var request = new SalesAnalysisRequest("nope", new PeriodRange(Day1, Day1), new PeriodRange(Day1, Day1), null, null, null, null, null, null, null);

        Assert.Throws<InvalidOperationException>(() => service.Analyze(request));
    }

    [Fact]
    public void An_empty_base_period_gives_no_percent_change()
    {
        var service = Service();
        using var stream = SmallExport();
        var load = service.Load(stream, "orders.xlsx");
        var emptyA = new PeriodRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2));
        var response = service.Analyze(new SalesAnalysisRequest(load.Token, emptyA, load.DefaultB, null, null, null, null, null, null, ["kpi", "reasons"]));

        var kpis = (List<KpiItem>)response.Sections["kpi"].Data!;
        Assert.Null(kpis.Single(k => k.Key == "gross").Change.Pct);
        Assert.Contains(response.Warnings, w => w.Contains("Dönem A'da hiç satış yok"));
    }
}
