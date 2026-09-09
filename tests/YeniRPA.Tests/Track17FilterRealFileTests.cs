using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// Runs <see cref="Track17Filter"/> against the real Mirakl orders export this module was built
/// against — 13,200 rows, "Shipping company" written by five different sellers in every casing and
/// diacritic combination the export ever carries. The counts below are lower than a naive count of
/// "Shipping company" spellings alone (861 matched rows, not ~1020): 161 of the rows whose carrier is
/// one of the three targets have a blank or literal "NULL" tracking number, same as
/// <see cref="TabularFile.ReadTracking"/>'s doc comment describes for the return templates — the
/// carrier is already assigned in the export before the shipment has actually been dispatched. Those
/// are correctly skipped rather than counted, so this file pins the true post-skip numbers read
/// directly off <see cref="Track17Filter.Filter"/>, not a guess made before the filter existed. See
/// <see cref="TitleCleanerRealFileTests"/> for why the sample lives in this project's <c>samples/</c>
/// folder rather than the web project's <c>wwwroot</c>.
/// </summary>
public class Track17FilterRealFileTests
{
    const string SampleFile = "orders (2).xlsx";

    [Fact]
    public void Filter_matches_861_rows_across_the_three_target_carriers()
    {
        var (rows, skipped) = Track17Filter.Filter(Table(SampleFile));

        Assert.Equal(335, rows.Count(r => r.Carrier == "Kolay Gelsin"));
        Assert.Equal(437, rows.Count(r => r.Carrier == "Sürat Kargo"));
        Assert.Equal(89, rows.Count(r => r.Carrier == "PTT Kargo"));
        Assert.Equal(861, rows.Count);
        Assert.Equal(161, skipped);
    }

    [Fact]
    public void Filter_never_matches_a_carrier_outside_the_three_targets()
    {
        var (rows, _) = Track17Filter.Filter(Table(SampleFile));

        Assert.All(rows, r => Assert.Contains(r.Carrier, Track17Filter.TargetCarriers));
    }

    [Fact]
    public void Every_matched_tracking_number_is_all_digits()
    {
        var (rows, _) = Track17Filter.Filter(Table(SampleFile));

        Assert.All(rows, r => Assert.True(r.TrackingNumber.Length > 0 && r.TrackingNumber.All(char.IsAsciiDigit)));
    }

    static List<List<string>> Table(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", fileName);
        Assert.True(File.Exists(path), $"The sample workbook is missing from the test output: {path}");

        using var stream = File.OpenRead(path);
        return TabularFile.Read(stream, fileName);
    }
}
