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
    public void MiraklSplitsAreSummedAndSubtractedFromTheTotalToGiveRetailGmv()
    {
        var bulut = BulutHeader + "\n1001;AKBANK;AKBANK;1000;5;500001;1;321778013\n1002;AKBANK;AKBANK;300;5;500002;1;999";
        var craftgate = CraftgateHeader + "\n500001;900001";
        var mirakl = "Order number;Amount\n01259_321778013-A;400\n01259_321778013-B;250,5\n01259_321778013-C;0";

        using var bulutStream = new MemoryStream(Encoding.UTF8.GetBytes(bulut));
        using var craftgateStream = new MemoryStream(Encoding.UTF8.GetBytes(craftgate));
        using var miraklStream = new MemoryStream(Encoding.UTF8.GetBytes(mirakl));
        var batch = PosReconciliationBuilder.Build(
            bulutStream, "bulut.csv", craftgateStream, "craftgate.csv", miraklStream, "mirakl.csv");

        Assert.Equal(650.5m, batch.Rows[0].MarketplaceGmv);
        Assert.Equal(349.5m, batch.Rows[0].RetailGmv);
        Assert.Equal(0m, batch.Rows[1].MarketplaceGmv);
        Assert.Equal(300m, batch.Rows[1].RetailGmv);
        Assert.Equal(650.5m, batch.Summary.TotalMarketplaceGmv);
        Assert.Equal(649.5m, batch.Summary.TotalRetailGmv);
    }

    [Theory]
    [InlineData("TDS_0000000000335216382", "335216382")]
    [InlineData("tds_000335150470", "335150470")]
    [InlineData("330703541", "330703541")]
    [InlineData("01259_321778013-A", "01259_321778013-A")]
    [InlineData("TDS_ABC", "TDS_ABC")]
    [InlineData("TDS_0000", "TDS_0000")]
    [InlineData("", "")]
    public void TdsPrefixedOrderNumbersAreReducedToTheBareOrder(string raw, string expected)
    {
        Assert.Equal(expected, PosReconciliationBuilder.NormalizeOrderNumber(raw));
    }

    [Fact]
    public void TdsOrderNumberNowMatchesMiraklAndGetsItsMarketplaceGmv()
    {
        var bulut = BulutHeader + "\n1001;AKBANK;AKBANK;1000;5;500001;1;TDS_0000000000335216382";
        var craftgate = CraftgateHeader + "\n500001;900001";
        var mirakl = "Order number;Amount\n01259_335216382-A;400";

        using var bulutStream = new MemoryStream(Encoding.UTF8.GetBytes(bulut));
        using var craftgateStream = new MemoryStream(Encoding.UTF8.GetBytes(craftgate));
        using var miraklStream = new MemoryStream(Encoding.UTF8.GetBytes(mirakl));
        var batch = PosReconciliationBuilder.Build(
            bulutStream, "bulut.csv", craftgateStream, "craftgate.csv", miraklStream, "mirakl.csv");

        var row = Assert.Single(batch.Rows);
        Assert.Equal("335216382", row.SiparisNumarasi);
        Assert.Equal(400m, row.MarketplaceGmv);
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
    // Kart tipi + Banka Oranları
    // -----------------------------------------------------------------

    const string BulutHeaderWithCardType = BulutHeader + ";POS Kart Tipi";

    /// <summary>A real LiteDB, in memory — the point is how it maps the positional records.</summary>
    sealed class MemoryContext : ILiteDbContext
    {
        readonly LiteDB.LiteDatabase _database = new(new MemoryStream());

        public string DatabasePath => "(in memory)";
        public LiteDB.ILiteCollection<T> GetCollection<T>(string name) => _database.GetCollection<T>(name);
    }

    [Fact]
    public void SavedRatesSurviveALiteDbRoundTrip()
    {
        var store = new PosRateStore(new MemoryContext());
        var edited = new PosRateSettings(
            [new PosBankRate("TEST BANK", 0.012m, 0.001m, null, 0.02m, [new PosInstallmentRate(6, 0.07m), new PosInstallmentRate(2, null)])],
            [new PosOnUsRule("TEST BANK", ["A", "B"])]);

        store.Save(edited);
        var loaded = store.Load();

        var bank = Assert.Single(loaded.Banks);
        Assert.Equal(0.012m, bank.ForeignRate);
        Assert.Null(bank.OneOnUsCredit);
        Assert.Equal([2, 6], bank.Installments.Select(i => i.Installment)); // ordered on save
        Assert.Equal(0.07m, bank.Installments[1].Rate);
        Assert.Equal(["A", "B"], Assert.Single(loaded.OnUsRules).CardBanks);
    }

    [Fact]
    public void PosKartTipiIsReadWhenPresentAndBlankWhenTheColumnIsMissing()
    {
        var craftgate = CraftgateHeader + "\n500001;900001";

        var with = Build(BulutHeaderWithCardType + "\n1;AKBANK;AKBANK;10;1;;1;S1;KREDİ KARTI", craftgate);
        Assert.Equal("KREDİ KARTI", Assert.Single(with.Rows).CardType);

        var without = Build(BulutHeader + "\n1;AKBANK;AKBANK;10;1;;1;S1", craftgate);
        Assert.Equal("", Assert.Single(without.Rows).CardType);
    }

    static PosReconciliationRow Row(string pos, string card, string type, decimal amount = 10) =>
        new("1", pos, card, amount, 0, "", 1, "S", "Orijinal", CardType: type);

    [Fact]
    public void ClassifierTreatsPrepaidAsDebitAndBlankTypeOrForeignBankAsForeign()
    {
        var classifier = new PosCardClassifier(new PosRateSettings([], []));

        Assert.Equal(PosCardKind.Credit, classifier.Kind(Row("AKBANK", "AKBANK", "KREDİ KARTI")));
        Assert.Equal(PosCardKind.Debit, classifier.Kind(Row("AKBANK", "AKBANK", "BANKA KARTI")));
        Assert.Equal(PosCardKind.Debit, classifier.Kind(Row("AKBANK", "AKBANK", "ÖN ÖDEMELİ KART")));
        Assert.Equal(PosCardKind.Foreign, classifier.Kind(Row("AKBANK", "YABANCI BANKA", "KREDİ KARTI")));
        Assert.Equal(PosCardKind.Foreign, classifier.Kind(Row("AKBANK", "AKBANK", "")));
    }

    [Fact]
    public void OnUsIsSameBankOrAListedFamilyBankAndNeverForeign()
    {
        var settings = new PosRateSettings([], [new PosOnUsRule("GARANTİ BBVA", ["DENİZBANK", "ING"])]);
        var classifier = new PosCardClassifier(settings);

        Assert.True(classifier.IsOnUs(Row("GARANTİ BBVA", "GARANTİ BBVA", "KREDİ KARTI")));
        Assert.True(classifier.IsOnUs(Row("GARANTİ BBVA", "DENİZBANK", "KREDİ KARTI")));
        Assert.True(classifier.IsOnUs(Row("GARANTİ BBVA", "ING", "BANKA KARTI")));
        Assert.False(classifier.IsOnUs(Row("AKBANK", "DENİZBANK", "KREDİ KARTI"))); // rule is per POS bank
        Assert.False(classifier.IsOnUs(Row("GARANTİ BBVA", "YABANCI BANKA", "KREDİ KARTI")));
    }

    [Fact]
    public void CardMixSharesAreCountedOnSalesOnlyAndAddUpToTheBankTotal()
    {
        var settings = new PosRateSettings([], [new PosOnUsRule("GARANTİ BBVA", ["ING"])]);
        PosReconciliationRow[] rows =
        [
            Row("GARANTİ BBVA", "GARANTİ BBVA", "KREDİ KARTI"),
            Row("GARANTİ BBVA", "ING", "BANKA KARTI"),
            Row("GARANTİ BBVA", "AKBANK", "KREDİ KARTI"),
            Row("GARANTİ BBVA", "YABANCI BANKA", ""),
            Row("GARANTİ BBVA", "GARANTİ BBVA", "KREDİ KARTI", amount: -10), // refund: not counted
        ];

        var mix = Assert.Single(PosReconciliationBuilder.ComputeCardMix(rows, new PosCardClassifier(settings), []));

        Assert.Equal(4, mix.Total);
        Assert.Equal(2, mix.OnUs);
        Assert.Equal(1, mix.Debit);
        Assert.Equal(2, mix.Credit);
        Assert.Equal(1, mix.Foreign);
        Assert.Equal(mix.Total, mix.Debit + mix.Credit + mix.Foreign);
    }

    [Fact]
    public void WorkbookHasTheBankRateSheetAndTheCardTypeColumn()
    {
        var batch = Build(BulutHeaderWithCardType + "\n1;AKBANK;AKBANK;10;1;;1;S1;KREDİ KARTI", CraftgateHeader + "\n1;1");

        var bytes = PosReconciliationBuilder.BuildWorkbook(batch, new PosRateStore(new MemoryContext()).Load());

        using var workbook = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
        Assert.Equal("Kart Tipi", workbook.Worksheet("Detay").Cell(1, 4).GetString());
        Assert.Equal("KREDİ KARTI", workbook.Worksheet("Detay").Cell(2, 4).GetString());
        Assert.Equal("On Us", workbook.Worksheet("Detay").Cell(2, 5).GetString());
        Assert.Equal("Komisyon Oranı (%)", workbook.Worksheet("Detay").Cell(1, 8).GetString());
        Assert.Equal(0.01, workbook.Worksheet("Detay").Cell(2, 8).GetDouble(), 6); // AKBANK on us credit, 1 taksit

        var rates = workbook.Worksheet("Banka Oranları");
        // Banks of the rate table come first, in its order; AKBANK is the third stock bank.
        Assert.Equal("AKBANK", rates.Cell(4, 1).GetString());
        Assert.Equal(1.0, rates.Cell(4, 2).GetDouble()); // on us
        Assert.Equal(1.0, rates.Cell(4, 5).GetDouble()); // credit
        Assert.Equal(0.009, rates.Cell(4, 7).GetDouble(), 6); // AKBANK foreign rate from the stock table
        Assert.True(rates.Cell(2, 2).IsEmpty()); // İŞ BANKASI has no sales: shares stay blank
    }

    [Fact]
    public void RateWorkbookRoundTripsTheSettings()
    {
        var original = new PosRateStore(new MemoryContext()).Load();

        var bytes = PosRateWorkbook.Build(original);
        var back = PosRateWorkbook.Read(new MemoryStream(bytes), []);

        Assert.Equal(original.Banks.Count, back.Banks.Count);
        Assert.Equal(original.Banks[0].PosBank, back.Banks[0].PosBank);
        Assert.Equal(original.Banks[0].OneNotOnUsDebit, back.Banks[0].OneNotOnUsDebit);
        Assert.Equal(original.Banks[0].Installments.Count, back.Banks[0].Installments.Count);
        Assert.Equal(["DENİZBANK", "ING"], back.OnUsRules.Single().CardBanks);
    }

    [Fact]
    public void ValidateRejectsRatesOutsideZeroToOneAndDuplicateBanks()
    {
        var bad = new PosRateSettings(
        [
            new PosBankRate("AKBANK", 1.5m, null, null, null, []),
            new PosBankRate("akbank", null, null, null, null, []),
        ], []);

        Assert.Equal(2, PosRateStore.Validate(bad).Count);
    }

    // -----------------------------------------------------------------
    // Detay "Komisyon Oranı (%)" — resolved from the bank rate table
    // -----------------------------------------------------------------

    static PosRateResolver Resolver(PosBankRate bank, params PosOnUsRule[] rules)
    {
        var settings = new PosRateSettings([bank], [.. rules]);
        return new PosRateResolver(settings, new PosCardClassifier(settings));
    }

    static PosReconciliationRow Row(string pos, string card, string type, int taksit) =>
        Row(pos, card, type) with { Taksit = taksit };

    [Fact]
    public void SinglePaymentPicksTheOnUsNotOnUsDebitCreditCell()
    {
        var bank = new PosBankRate("AKBANK", 0.009m, 0.011m, 0.012m, 0.013m, [], OneNotOnUsCredit: 0.014m);
        var resolver = Resolver(bank);

        Assert.Equal(0.011m, resolver.Resolve(Row("AKBANK", "AKBANK", "BANKA KARTI", 1)));
        Assert.Equal(0.012m, resolver.Resolve(Row("AKBANK", "AKBANK", "KREDİ KARTI", 1)));
        Assert.Equal(0.013m, resolver.Resolve(Row("AKBANK", "QNB", "BANKA KARTI", 1)));
        Assert.Equal(0.014m, resolver.Resolve(Row("AKBANK", "QNB", "KREDİ KARTI", 1)));
        Assert.Equal(0.011m, resolver.Resolve(Row("AKBANK", "AKBANK", "ÖN ÖDEMELİ KART", 0))); // prepaid = debit, 0 = 1 taksit
    }

    [Fact]
    public void NotOnUsCreditIsBlankUntilTheOperatorFillsIt()
    {
        var bank = new PosBankRate("AKBANK", null, 0.01m, 0.01m, 0.02m, []);

        Assert.Null(Resolver(bank).Resolve(Row("AKBANK", "QNB", "KREDİ KARTI", 1)));
    }

    [Fact]
    public void InstallmentsTakeTheInstallmentRateAndForeignTakesTheForeignRateAtAnyCount()
    {
        var bank = new PosBankRate("HALKBANK", 0m, 0.006m, 0.006m, 0.0159m,
            [new PosInstallmentRate(2, 0.0245m), new PosInstallmentRate(3, 0.0435m), new PosInstallmentRate(4, null)]);
        var resolver = Resolver(bank);

        Assert.Equal(0.0245m, resolver.Resolve(Row("HALKBANK", "HALKBANK", "KREDİ KARTI", 2)));
        Assert.Equal(0.0245m, resolver.Resolve(Row("HALKBANK", "QNB", "BANKA KARTI", 2))); // not split by card type
        Assert.Null(resolver.Resolve(Row("HALKBANK", "HALKBANK", "KREDİ KARTI", 4))); // blank cell
        Assert.Null(resolver.Resolve(Row("HALKBANK", "HALKBANK", "KREDİ KARTI", 6))); // not in the table
        Assert.Equal(0m, resolver.Resolve(Row("HALKBANK", "YABANCI BANKA", "KREDİ KARTI", 3))); // a real 0 rate, not blank
    }

    [Fact]
    public void BankMissingFromTheTableOrOfUnknownCardTypeHasNoRate()
    {
        var bank = new PosBankRate("AKBANK", 0.009m, 0.01m, 0.01m, 0.02m, []);
        var resolver = Resolver(bank);

        Assert.Null(resolver.Resolve(Row("QNB", "QNB", "KREDİ KARTI", 1)));
        Assert.Null(resolver.Resolve(Row("AKBANK", "AKBANK", "HEDİYE KARTI", 1)));
    }

    [Fact]
    public void FamilyBankCardOnTheGarantiPosUsesTheOnUsRate()
    {
        var bank = new PosBankRate("GARANTİ BBVA", 0.02m, 0.009m, 0.0091m, 0.0111m, []);
        var resolver = Resolver(bank, new PosOnUsRule("GARANTİ BBVA", ["ING"]));

        Assert.Equal(0.0091m, resolver.Resolve(Row("GARANTİ BBVA", "ING", "KREDİ KARTI", 1)));
    }

    [Fact]
    public void RateWorkbookRoundTripsTheNotOnUsCreditColumn()
    {
        var settings = new PosRateSettings(
            [new PosBankRate("AKBANK", 0.009m, 0.01m, 0.01m, 0.02m, [new PosInstallmentRate(2, 0.03m)], OneNotOnUsCredit: 0.025m)], []);

        var back = PosRateWorkbook.Read(new MemoryStream(PosRateWorkbook.Build(settings)), []);

        var bank = Assert.Single(back.Banks);
        Assert.Equal(0.025m, bank.OneNotOnUsCredit);
        Assert.Equal(0.02m, bank.OneNotOnUsDebit);
        Assert.Equal(0.03m, bank.Installments.Single().Rate);
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
