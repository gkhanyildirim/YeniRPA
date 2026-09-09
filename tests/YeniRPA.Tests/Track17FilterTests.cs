using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

public class Track17FilterTests
{
    static List<List<string>> Table(params (string Order, string Tracking, string Shipping)[] rows)
    {
        var table = new List<List<string>>
        {
            new() { "Order number", "Tracking number", "Shipping company" }
        };

        foreach (var row in rows)
            table.Add(new List<string> { row.Order, row.Tracking, row.Shipping });

        return table;
    }

    [Theory]
    [InlineData("Surat kargo")]
    [InlineData("Sürat Kargo")]
    [InlineData("sürat")]
    [InlineData("SÜRAT KARGO")]
    [InlineData("SÜRAT")]
    public void Filter_folds_every_spelling_of_Surat_Kargo_to_one_carrier(string spelling)
    {
        var (rows, skipped) = Track17Filter.Filter(Table(("O1", "123456", spelling)));

        Assert.Equal(0, skipped);
        var row = Assert.Single(rows);
        Assert.Equal("Sürat Kargo", row.Carrier);
        Assert.Equal("123456", row.TrackingNumber);
    }

    [Theory]
    [InlineData("Kolay Gelsin")]
    [InlineData("kolaygelsin")]
    public void Filter_matches_Kolay_Gelsin(string spelling)
    {
        var (rows, _) = Track17Filter.Filter(Table(("O1", "1", spelling)));
        Assert.Equal("Kolay Gelsin", Assert.Single(rows).Carrier);
    }

    [Theory]
    [InlineData("PTT Kargo")]
    [InlineData("ptt")]
    public void Filter_matches_PTT_Kargo(string spelling)
    {
        var (rows, _) = Track17Filter.Filter(Table(("O1", "1", spelling)));
        Assert.Equal("PTT Kargo", Assert.Single(rows).Carrier);
    }

    [Theory]
    [InlineData("Yurtiçi")]
    [InlineData("Aras Kargo")]
    [InlineData("Arçelik Yetkili Servisi")]
    [InlineData("UPS")]
    [InlineData("")]
    public void Filter_excludes_carriers_outside_the_three_targets(string spelling)
    {
        var (rows, _) = Track17Filter.Filter(Table(("O1", "123", spelling)));
        Assert.Empty(rows);
    }

    [Fact]
    public void Filter_skips_and_counts_a_malformed_tracking_number()
    {
        var (rows, skipped) = Track17Filter.Filter(Table(
            ("O1", "NULL", "PTT Kargo"),
            ("O2", "not-a-number", "Kolay Gelsin"),
            ("O3", "999", "Sürat Kargo")));

        Assert.Equal(2, skipped);
        var row = Assert.Single(rows);
        Assert.Equal("O3", row.OrderNumber);
    }

    [Fact]
    public void Summarize_counts_by_carrier_and_batches_by_forty()
    {
        var rows = Enumerable.Range(1, 81)
            .Select(i => new Track17Row($"O{i}", i.ToString(), "PTT Kargo", "PTT Kargo"))
            .ToList();

        var summary = Track17Filter.Summarize("batch-1", 81, rows, 0);

        Assert.Equal(81, summary.DistinctTrackingNumbers);
        Assert.Equal(3, summary.BatchCount);
        Assert.Equal(81, summary.MatchedByCarrier["PTT Kargo"]);
    }
}
