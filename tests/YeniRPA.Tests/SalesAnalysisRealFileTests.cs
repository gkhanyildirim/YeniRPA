using YeniRPA.Web.Services.SalesAnalysis;
using YeniRPA.Web.Services.SalesAnalysis.Providers;

namespace YeniRPA.Tests;

/// <summary>
/// The Sales Analysis end to end over the real one-week orders export (28.09–05.10.2026, 3,554
/// lines), comparing 28.09–01.10 with 02.10–05.10. The status totals were read off the file
/// independently of this code; see <see cref="PosReconciliationRealFileTests"/> for why the sample
/// lives in <c>samples/</c> rather than in the web project.
/// </summary>
public class SalesAnalysisRealFileTests
{
    const string SampleFile = "orders (5).xlsx";

    static readonly PeriodRange A = new(new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 1));
    static readonly PeriodRange B = new(new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 5));

    static (SalesAnalysisService Service, LoadResult Load) Loaded()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", SampleFile);
        Assert.True(File.Exists(path), $"The sample workbook is missing from the test output: {path}");

        var service = new SalesAnalysisService(new SalesAnalysisStore(),
        [
            new KpiProvider(), new PvmProvider(), new CategoryProvider(), new ProductProvider(), new BrandProvider(),
            new SellerProvider(), new StatusProvider(), new TimeSeriesProvider(), new CityProvider(),
            new ProfitabilityProvider(), new OutlierProvider(),
        ]);
        using var stream = File.OpenRead(path);
        return (service, service.Load(stream, SampleFile));
    }

    static AnalysisResponse Analyze(SalesAnalysisService service, LoadResult load, params string[] sections) =>
        service.Analyze(new SalesAnalysisRequest(load.Token, A, B, null, null, null, null, "none", null, sections));

    [Fact]
    public void Loads_every_line_and_defaults_to_the_example_comparison()
    {
        var (_, load) = Loaded();

        Assert.Equal(3554, load.Lines);
        Assert.Equal(3467, load.Orders);
        Assert.Equal("2026-09-28", load.MinDate);
        Assert.Equal("2026-10-05", load.MaxDate);
        Assert.Equal(A, load.DefaultA);
        Assert.Equal(B, load.DefaultB);
        Assert.DoesNotContain(load.Report.Issues, i => i.Kind == "unparsedDate");
        Assert.DoesNotContain("Canceled", load.DefaultStatuses);
        Assert.DoesNotContain("Rejected", load.DefaultStatuses);
    }

    [Fact]
    public void Gross_sales_of_both_periods_add_up_to_the_file_total()
    {
        var (service, load) = Loaded();
        var kpi = (List<KpiItem>)Analyze(service, load, "kpi").Sections["kpi"].Data!;
        var gross = kpi.Single(k => k.Key == "gross").Change;

        // Σ Amount of every non-Canceled, non-Rejected line in the file, read independently.
        Assert.Equal(44_862_499.69, gross.A!.Value + gross.B!.Value, 2);
        Assert.True(gross.A > 0 && gross.B > 0);
    }

    [Fact]
    public void Pvm_reconciles_exactly_on_the_real_file()
    {
        var (service, load) = Loaded();
        var response = Analyze(service, load, "kpi", "pvm");
        var pvm = (PvmData)response.Sections["pvm"].Data!;
        var gross = ((List<KpiItem>)response.Sections["kpi"].Data!).Single(k => k.Key == "gross").Change;

        Assert.Equal(gross.Abs!.Value, pvm.Delta, 4);
        Assert.Equal(pvm.Delta, pvm.Volume + pvm.Price + pvm.Mix, 4);
    }

    [Fact]
    public void Cancellations_match_the_file()
    {
        var (service, load) = Loaded();
        var status = (StatusData)Analyze(service, load, "status").Sections["status"].Data!;

        var canceled = status.Statuses.Single(s => s.Status == "Canceled");
        Assert.Equal(140, canceled.LinesA + canceled.LinesB);
    }

    [Fact]
    public void Every_section_and_the_reasons_panel_render_without_errors()
    {
        var (service, load) = Loaded();
        var keys = service.Providers.Select(p => p.Key).Append(ReasonsComposer.Key).ToArray();

        var response = Analyze(service, load, keys);

        Assert.Equal(keys.Length, response.Sections.Count);
        var reasons = (ReasonsData)response.Sections[ReasonsComposer.Key].Data!;
        Assert.InRange(reasons.Reasons.Count, 3, 5);
        Assert.False(response.WeakData);

        // 05.10 holds 26 lines against ~500 on every other day: the export was taken that morning.
        Assert.Contains(response.Warnings, w => w.StartsWith("05.10.2026 günü dosyada eksik görünüyor") && w.Contains("Dönem B"));
        Assert.DoesNotContain(reasons.Reasons, r => r.Text.Contains("NaN") || r.Text.Contains("∞"));
    }
}
