using System.Text;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// Backfills Bulut Tahsilat's blank "Sipariş Numarası" rows from Craftgate ("Provizyon No" →
/// "authCode" → "externalId") and pivots the result by POS Banka × Taksit. The join's match/no-match/
/// conflict rules and the pivot's grand total row/column are pinned here.
/// </summary>
public class PosReconciliationBuilderTests
{
    const string BulutHeader = "İşlem Kodu;POS Banka;Kart Banka;İşlem Tutarı;Toplam Komisyon Tutarı;Provizyon No;Taksit;Sipariş Numarası";
    const string CraftgateHeader = "authCode;externalId";

    static PosReconciliationBatch Build(string bulutCsv, string craftgateCsv)
    {
        using var bulutStream = new MemoryStream(Encoding.UTF8.GetBytes(bulutCsv));
        using var craftgateStream = new MemoryStream(Encoding.UTF8.GetBytes(craftgateCsv));
        return PosReconciliationBuilder.Build(bulutStream, "bulut.csv", craftgateStream, "craftgate.csv");
    }

    // -----------------------------------------------------------------
    // Backfill — match / no match / conflict
    // -----------------------------------------------------------------

    [Fact]
    public void UniqueAuthCodeMatchBackfillsTheOrderNumber()
    {
        var bulut = BulutHeader + "\n1001;AKBANK;AKBANK;100;5;500001;1;";
        var craftgate = CraftgateHeader + "\n500001;900001";

        var batch = Build(bulut, craftgate);

        var row = Assert.Single(batch.Rows);
        Assert.Equal("900001", row.SiparisNumarasi);
        Assert.Equal(PosReconciliationBuilder.SourceMatched, row.SiparisKaynagi);
        Assert.Empty(batch.Unmatched);
        Assert.Equal(1, batch.Summary.BackfilledViaCraftgate);
    }

    [Fact]
    public void ProvizyonNoWithNoMatchingAuthCodeIsReportedAsUnmatchedAndLeftBlank()
    {
        var bulut = BulutHeader + "\n1001;AKBANK;AKBANK;100;5;500002;1;";
        var craftgate = CraftgateHeader + "\n500001;900001";

        var batch = Build(bulut, craftgate);

        var row = Assert.Single(batch.Rows);
        Assert.Equal("", row.SiparisNumarasi);
        Assert.Equal(PosReconciliationBuilder.SourceUnmatched, row.SiparisKaynagi);
        Assert.Single(batch.Unmatched);
        Assert.Equal(1, batch.Summary.Unmatched);
    }

    [Fact]
    public void AuthCodeResolvingToTwoDifferentExternalIdsIsReportedAsConflictAndLeftBlank()
    {
        var bulut = BulutHeader + "\n1001;AKBANK;AKBANK;100;5;500003;1;";
        var craftgate = CraftgateHeader + "\n500003;900003\n500003;900004";

        var batch = Build(bulut, craftgate);

        var row = Assert.Single(batch.Rows);
        Assert.Equal("", row.SiparisNumarasi);
        Assert.Equal(PosReconciliationBuilder.SourceConflict, row.SiparisKaynagi);
        Assert.Single(batch.Unmatched);
        Assert.Equal(1, batch.Summary.Conflicted);
    }

    [Fact]
    public void BlankProvizyonNoIsReportedAsUnmatchedWithoutAttemptingALookup()
    {
        var bulut = BulutHeader + "\n1001;AKBANK;AKBANK;100;5;;1;";
        var craftgate = CraftgateHeader + "\n500001;900001";

        var batch = Build(bulut, craftgate);

        var row = Assert.Single(batch.Rows);
        Assert.Equal(PosReconciliationBuilder.SourceUnmatched, row.SiparisKaynagi);
        Assert.Contains("Provizyon No", Assert.Single(batch.Unmatched).Reason);
    }

    [Fact]
    public void RowWithAnExistingOrderNumberIsNeverLookedUp()
    {
        // Provizyon No matches nothing in Craftgate on purpose — if the row were looked up anyway it
        // would land in Unmatched, which is exactly what this pins against.
        var bulut = BulutHeader + "\n1001;AKBANK;AKBANK;100;5;500009;1;800001";
        var craftgate = CraftgateHeader + "\n500001;900001";

        var batch = Build(bulut, craftgate);

        var row = Assert.Single(batch.Rows);
        Assert.Equal("800001", row.SiparisNumarasi);
        Assert.Equal(PosReconciliationBuilder.SourceOriginal, row.SiparisKaynagi);
        Assert.Empty(batch.Unmatched);
    }

    // -----------------------------------------------------------------
    // Pivot — POS Banka rows x Taksit columns, plus a closing Genel Toplam row/column
    // -----------------------------------------------------------------

    [Fact]
    public void PivotSumsAmountsAndCommissionsPerBankAndInstallmentCountWithGrandTotals()
    {
        var bulut = string.Join('\n',
        [
            BulutHeader,
            "1;AKBANK;AKBANK;10;1;;1;S1",
            "2;AKBANK;AKBANK;20;2;;2;S2",
            "3;GARANTİ BBVA;GARANTİ BBVA;5;0.5;;1;S3",
            "4;GARANTİ BBVA;GARANTİ BBVA;5;0.5;;1;S4",
        ]);
        var craftgate = CraftgateHeader + "\n500001;900001";

        var batch = Build(bulut, craftgate);
        var pivot = batch.Pivot;

        Assert.Equal(["1", "2", PosReconciliationBuilder.GrandTotalLabel], pivot.Labels);

        var akbank = pivot.Rows.Single(r => r.PosBanka == "AKBANK");
        Assert.Equal(10, akbank.Cells[0].IslemTutari);
        Assert.Equal(1, akbank.Cells[0].ToplamKomisyonTutari);
        Assert.Equal(20, akbank.Cells[1].IslemTutari);
        Assert.Equal(30, akbank.Cells[2].IslemTutari); // row's own Genel Toplam cell

        var garanti = pivot.Rows.Single(r => r.PosBanka == "GARANTİ BBVA");
        Assert.Equal(10, garanti.Cells[0].IslemTutari); // 5 + 5
        Assert.Equal(1, garanti.Cells[0].ToplamKomisyonTutari);
        Assert.Equal(0, garanti.Cells[1].IslemTutari); // no 2-installment row for this bank

        var grandTotalRow = pivot.Rows.Single(r => r.PosBanka == PosReconciliationBuilder.GrandTotalLabel);
        Assert.Equal(20, grandTotalRow.Cells[0].IslemTutari); // column 1: 10 (AKBANK) + 10 (GARANTİ)
        Assert.Equal(20, grandTotalRow.Cells[1].IslemTutari); // column 2: 20 (AKBANK only)
        Assert.Equal(40, grandTotalRow.Cells[2].IslemTutari); // grand total: 30 + 10
        Assert.Equal(4, grandTotalRow.Cells[2].ToplamKomisyonTutari);
    }
}
