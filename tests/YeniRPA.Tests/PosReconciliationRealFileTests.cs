using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// <see cref="PosReconciliationBuilder"/> run end to end over the real Bulut Tahsilat / Craftgate
/// exports this module was built against (14,267 / 13,422 rows).
///
/// <para>These numbers matter more than usual here: the first run of this test — before
/// <c>PosReconciliationBuilder.ParseMoney</c> existed — summed every bank's İşlem Tutarı and Toplam
/// Komisyon Tutarı to a suspiciously round, roughly 18x too large figure. <see cref="TabularFile"/>'s
/// XLSX reader renders a numeric cell's decimal point using <see cref="System.Globalization.CultureInfo.CurrentCulture"/>,
/// and on the operator's own Turkish machine that means a comma (<c>"81,27"</c> for 81.27 TL); parsing
/// that back with <see cref="TabularFile.ParseNumber"/>'s invariant-culture parse reads the comma as a
/// misplaced thousands separator and strips it, turning 81,27 into 8127. <c>ParseMoney</c> exists
/// because of exactly that bug, so this file pins the true totals read directly off it, not a figure
/// guessed before the fix existed. See <see cref="TitleCleanerRealFileTests"/> for why the sample
/// workbooks live in this project's <c>samples/</c> folder rather than the web project's
/// <c>wwwroot</c> — real transaction amounts and bank names are not something this app serves as a
/// static file to anyone who guesses the URL.</para>
/// </summary>
public class PosReconciliationRealFileTests
{
    const string BulutFile = "buluttahsilat ağustos '26.xlsx";
    const string CraftgateFile = "craftgate Aug '26.xlsx";

    static YeniRPA.Web.Models.PosReconciliationBatch Build()
    {
        var bulutPath = Path.Combine(AppContext.BaseDirectory, "samples", BulutFile);
        var craftgatePath = Path.Combine(AppContext.BaseDirectory, "samples", CraftgateFile);
        Assert.True(File.Exists(bulutPath), $"The sample workbook is missing from the test output: {bulutPath}");
        Assert.True(File.Exists(craftgatePath), $"The sample workbook is missing from the test output: {craftgatePath}");

        using var bulutStream = File.OpenRead(bulutPath);
        using var craftgateStream = File.OpenRead(craftgatePath);
        return PosReconciliationBuilder.Build(bulutStream, BulutFile, craftgateStream, CraftgateFile);
    }

    [Fact]
    public void Every_row_with_a_blank_order_number_is_accounted_for_as_matched_unmatched_or_conflicted()
    {
        var batch = Build();

        Assert.Equal(14267, batch.Summary.TotalRecords);
        Assert.Equal(13317, batch.Summary.OriginalOrderNumbers);
        Assert.Equal(938, batch.Summary.BackfilledViaCraftgate);
        Assert.Equal(2, batch.Summary.Unmatched);
        Assert.Equal(10, batch.Summary.Conflicted);

        // 950 rows had a blank Sipariş Numarası going in (945 of them Akbank, per the sample's own
        // notes tab) — every one of them lands in exactly one of these three buckets.
        Assert.Equal(950, batch.Summary.BackfilledViaCraftgate + batch.Summary.Unmatched + batch.Summary.Conflicted);
        Assert.Equal(12, batch.Unmatched.Count); // 2 unmatched + 10 conflicted
    }

    [Fact]
    public void Totals_match_the_real_export_after_the_culture_correct_money_parse()
    {
        var batch = Build();

        Assert.Equal(172601312.66m, batch.Summary.TotalIslemTutari);
        Assert.Equal(5230334.52m, batch.Summary.TotalKomisyonTutari);
    }

    [Fact]
    public void Pivot_has_one_row_per_bank_plus_a_closing_grand_total_row_that_reconciles_with_the_summary()
    {
        var batch = Build();
        var pivot = batch.Pivot;

        Assert.Equal(["1", "2", "3", "4", "5", "9", PosReconciliationBuilder.GrandTotalLabel], pivot.Labels);
        Assert.Equal(8, pivot.Rows.Count); // 7 banks + Genel Toplam

        var grandTotal = pivot.Rows[^1];
        Assert.Equal(PosReconciliationBuilder.GrandTotalLabel, grandTotal.PosBanka);
        Assert.Equal(batch.Summary.TotalIslemTutari, grandTotal.Cells[^1].IslemTutari);
        Assert.Equal(batch.Summary.TotalKomisyonTutari, grandTotal.Cells[^1].ToplamKomisyonTutari);

        var akbank = pivot.Rows.Single(r => r.PosBanka == "AKBANK");
        Assert.Equal(9703128.70m, akbank.Cells[^1].IslemTutari);
    }
}
