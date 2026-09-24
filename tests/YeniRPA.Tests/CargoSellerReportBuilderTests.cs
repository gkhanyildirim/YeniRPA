using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// Matches a cargo-invoice export, grouped by every seller it holds, against the Marketplace
/// return/exchange export and the MM Pazaryeri cargo data export. The tracking-code lookup's priority
/// and conflict rules, the seller grouping, and the header-row search that stands in for a broken
/// worksheet dimension, are pinned here.
/// </summary>
public class CargoSellerReportBuilderTests
{
    // -----------------------------------------------------------------
    // Analyze — header row search and seller column detection
    // -----------------------------------------------------------------

    [Fact]
    public void HeaderRowIsFoundEvenWhenPrecededByFreeTextRows()
    {
        // Row 0 ends with a semicolon purely so TabularFile.ReadCsv's delimiter sniff (which looks at
        // row 0 alone) picks ';' instead of defaulting to ',' for a title row that has neither.
        const string kargo =
            "Kargo Fatura Raporu - Eylül 2026;\n" +
            "Gönderi Kodu;Satıcı;İrsaliye Matrahı;KDV\n" +
            "100285010611;Zitek;10.50;1.89";

        var result = Analyze(kargo);

        Assert.True(result.SellerColumnResolved);
        Assert.Equal("Satıcı", result.SellerColumn);
        Assert.Equal("Gönderi Kodu", result.TrackingColumn);
        Assert.Equal("Zitek", Assert.Single(result.Sellers));
    }

    [Fact]
    public void SellerColumnFallsBackToTheNextKnownCandidateName()
    {
        const string kargo = "Gönderi Kodu;Satıcı Adı;İrsaliye Matrahı\n100285010611;Zitek;10.50";

        var result = Analyze(kargo);

        Assert.True(result.SellerColumnResolved);
        Assert.Equal("Satıcı Adı", result.SellerColumn);
    }

    [Fact]
    public void AliciMusteriIsPreferredOverGondericiMusteriWhenBothAreOnTheFile()
    {
        // The courier bills the marketplace's own account as the sender on every row, so "Gönderici
        // Müşteri" always reads as the marketplace, not the seller — "Alıcı Müşteri" is the one that
        // actually varies per shipment and names the seller.
        const string kargo =
            "Gönderi Kodu;Gönderici Müşteri;Alıcı Müşteri\n" +
            "100285010611;Marketplace A.Ş.;Zitek";

        var result = Analyze(kargo);

        Assert.True(result.SellerColumnResolved);
        Assert.Equal("Alıcı Müşteri", result.SellerColumn);
        Assert.Equal("Zitek", Assert.Single(result.Sellers));
    }

    [Fact]
    public void UnresolvedSellerColumnReturnsEveryHeaderForManualSelection()
    {
        const string kargo = "Gönderi Kodu;Firma;İrsaliye Matrahı\n100285010611;Zitek;10.50";

        var result = Analyze(kargo);

        Assert.False(result.SellerColumnResolved);
        Assert.Null(result.SellerColumn);
        Assert.Empty(result.Sellers);
        Assert.Equal(["Gönderi Kodu", "Firma", "İrsaliye Matrahı"], result.Headers);
    }

    [Fact]
    public void ManualSellerColumnOverrideIsHonoredEvenWhenNotAKnownCandidateName()
    {
        const string kargo = "Gönderi Kodu;Firma;İrsaliye Matrahı\n100285010611;Zitek;10.50";

        var result = Analyze(kargo, sellerColumnOverride: "Firma");

        Assert.True(result.SellerColumnResolved);
        Assert.Equal("Firma", result.SellerColumn);
        Assert.Equal("Zitek", Assert.Single(result.Sellers));
    }

    [Fact]
    public void MissingTrackingCodeColumnThrowsAClearError()
    {
        const string kargo = "Satıcı;İrsaliye Matrahı\nZitek;10.50";

        var ex = Assert.Throws<InvalidOperationException>(() => Analyze(kargo));
        Assert.Contains("Gönderi Kodu", ex.Message);
    }

    // -----------------------------------------------------------------
    // GenerateAll — grouping by seller
    // -----------------------------------------------------------------

    [Fact]
    public void EverySellerInTheFileGetsItsOwnResult()
    {
        const string kargo =
            "Gönderi Kodu;Satıcı\n" +
            "100285010611;Zitek\n" +
            "200000000000;Acme\n" +
            "300000000000;";

        var results = GenerateAll(
            kargo, "SiparişNo;Kargo Takip Kodu", "CustomerOrderNumber;YK Takip Kodu");

        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r.SellerName == "Zitek" && r.Rows.Count == 1);
        Assert.Contains(results, r => r.SellerName == "Acme" && r.Rows.Count == 1);
        // A blank seller cell is kept and labeled instead of the row silently disappearing.
        Assert.Contains(results, r => r.SellerName == "(Satıcı Belirtilmemiş)" && r.Rows.Count == 1);
    }

    [Fact]
    public void SellersAreGroupedCaseAndWhitespaceTolerantly()
    {
        const string kargo =
            "Gönderi Kodu;Satıcı\n" +
            "100285010611;Zitek\n" +
            "200000000000; ZITEK \n" +
            "300000000000;Acme";

        var results = GenerateAll(
            kargo, "SiparişNo;Kargo Takip Kodu", "CustomerOrderNumber;YK Takip Kodu");

        Assert.Equal(2, results.Count);
        var zitek = Assert.Single(results, r => r.SellerName == "Zitek");
        Assert.Equal(2, zitek.Rows.Count);
    }

    [Fact]
    public void NoCargoRecordsAtAllThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            GenerateAll("Gönderi Kodu;Satıcı", "SiparişNo;Kargo Takip Kodu", "CustomerOrderNumber;YK Takip Kodu"));

        Assert.Contains("No cargo records", ex.Message);
    }

    // -----------------------------------------------------------------
    // GenerateAll — tracking-code normalization and match priority
    // -----------------------------------------------------------------

    [Fact]
    public void ScientificNotationTrackingCodeMatchesAPlainDigitCodeInTheLookupFile()
    {
        const string kargo = "Gönderi Kodu;Satıcı\n1.00285010611E11;Zitek";
        const string marketplace = "SiparişNo;Kargo Takip Kodu\n311911494-A;100285010611";
        const string mm = "CustomerOrderNumber;YK Takip Kodu";

        var result = GenerateOne(kargo, marketplace, mm);

        var row = Assert.Single(result.Rows);
        Assert.Equal("100285010611", row.TrackingCode);
        Assert.Equal("311911494-A", row.OrderNumber);
        Assert.Equal(CargoSellerReportBuilder.MatchSourceMarketplace, row.MatchSource);
        Assert.Equal(CargoSellerReportBuilder.MatchStatusMatched, row.MatchStatus);
    }

    [Fact]
    public void MarketplaceMatchTakesPriorityOverMmEvenWhenBothFilesHaveTheCode()
    {
        const string kargo = "Gönderi Kodu;Satıcı\n100285010611;Zitek";
        const string marketplace = "SiparişNo;Kargo Takip Kodu\n311911494-A;100285010611";
        const string mm = "CustomerOrderNumber;YK Takip Kodu\n999999999-B;100285010611";

        var result = GenerateOne(kargo, marketplace, mm);

        var row = Assert.Single(result.Rows);
        Assert.Equal("311911494-A", row.OrderNumber);
        Assert.Equal(CargoSellerReportBuilder.MatchSourceMarketplace, row.MatchSource);
    }

    [Fact]
    public void MmIsUsedOnlyWhenMarketplaceHasNoRecordOfTheCode()
    {
        const string kargo = "Gönderi Kodu;Satıcı\n100285010611;Zitek";
        const string marketplace = "SiparişNo;Kargo Takip Kodu\n555555555-A;200000000000";
        const string mm = "CustomerOrderNumber;YK Takip Kodu\n999999999-B;100285010611";

        var result = GenerateOne(kargo, marketplace, mm);

        var row = Assert.Single(result.Rows);
        Assert.Equal("999999999-B", row.OrderNumber);
        Assert.Equal(CargoSellerReportBuilder.MatchSourceMm, row.MatchSource);
    }

    [Fact]
    public void ConflictingOrderNumbersForTheSameCodeAreMarkedAsConflictAndDoNotFallThroughToMm()
    {
        const string kargo = "Gönderi Kodu;Satıcı\n100285010611;Zitek";
        const string marketplace =
            "SiparişNo;Kargo Takip Kodu\n" +
            "111111111-A;100285010611\n" +
            "222222222-A;100285010611";
        // Also present, cleanly, in MM — Marketplace already saw the code (even if conflicted), so MM
        // must never be consulted for it.
        const string mm = "CustomerOrderNumber;YK Takip Kodu\n333333333-A;100285010611";

        var result = GenerateOne(kargo, marketplace, mm);

        var row = Assert.Single(result.Rows);
        Assert.Equal("", row.OrderNumber);
        Assert.Equal(CargoSellerReportBuilder.MatchSourceConflict, row.MatchSource);
        Assert.Equal(CargoSellerReportBuilder.MatchStatusConflicted, row.MatchStatus);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("100285010611", conflict.TrackingCode);
        Assert.Equal(CargoSellerReportBuilder.MatchSourceMarketplace, conflict.MatchSource);
        Assert.Equal(["111111111-A", "222222222-A"], conflict.OrderNumbers);
    }

    [Fact]
    public void TheSameOrderNumberRepeatedForOneCodeIsNotAConflict()
    {
        const string kargo = "Gönderi Kodu;Satıcı\n100285010611;Zitek";
        const string marketplace =
            "SiparişNo;Kargo Takip Kodu\n" +
            "111111111-A;100285010611\n" +
            "111111111-A;100285010611";
        const string mm = "CustomerOrderNumber;YK Takip Kodu";

        var result = GenerateOne(kargo, marketplace, mm);

        var row = Assert.Single(result.Rows);
        Assert.Equal("111111111-A", row.OrderNumber);
        Assert.Equal(CargoSellerReportBuilder.MatchStatusMatched, row.MatchStatus);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void CodeNotFoundInEitherFileIsUnmatched()
    {
        const string kargo = "Gönderi Kodu;Satıcı\n100285010611;Zitek";
        const string marketplace = "SiparişNo;Kargo Takip Kodu";
        const string mm = "CustomerOrderNumber;YK Takip Kodu";

        var result = GenerateOne(kargo, marketplace, mm);

        var row = Assert.Single(result.Rows);
        Assert.Equal(CargoSellerReportBuilder.MatchSourceNone, row.MatchSource);
        Assert.Equal(CargoSellerReportBuilder.MatchStatusUnmatched, row.MatchStatus);

        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal("100285010611", unmatched.TrackingCode);
        Assert.Equal("Zitek", unmatched.Seller);
    }

    // -----------------------------------------------------------------
    // GenerateAll — summary totals
    // -----------------------------------------------------------------

    [Fact]
    public void IrsaliyeMatrahiIsTheCargoFeeColumnAheadOfOtherCandidateNames()
    {
        const string kargo =
            "Gönderi Kodu;Satıcı;İrsaliye Matrahı;Kargo Bedeli;KDV\n" +
            "100285010611;Zitek;10.50;999.00;1.89";

        var result = GenerateOne(
            kargo,
            "SiparişNo;Kargo Takip Kodu\n311911494-A;100285010611",
            "CustomerOrderNumber;YK Takip Kodu");

        Assert.Equal(10.50m, result.Summary.TotalCargoFee);
        Assert.Equal(1.89m, result.Summary.TotalVat);
        Assert.Equal(12.39m, result.Summary.TotalCargoFeePlusVat);
    }

    [Fact]
    public void SummaryTotalsOnlyIncludeOptionalColumnsWhenTheFileHasThem()
    {
        const string kargoWithoutFee = "Gönderi Kodu;Satıcı\n100285010611;Zitek";
        var withoutFee = GenerateOne(
            kargoWithoutFee,
            "SiparişNo;Kargo Takip Kodu\n311911494-A;100285010611",
            "CustomerOrderNumber;YK Takip Kodu");

        Assert.Null(withoutFee.Summary.TotalCargoFee);
        Assert.Null(withoutFee.Summary.TotalVat);
        Assert.Equal(1, withoutFee.Summary.MatchedViaMarketplace);
        Assert.Equal(1, withoutFee.Summary.TotalMatched);

        const string kargoWithFee =
            "Gönderi Kodu;Satıcı;İrsaliye Matrahı;KDV\n" +
            "100285010611;Zitek;10.50;1.89\n" +
            "200000000000;Zitek;5.00;0.90";
        var withFee = GenerateOne(
            kargoWithFee,
            "SiparişNo;Kargo Takip Kodu\n311911494-A;100285010611",
            "CustomerOrderNumber;YK Takip Kodu\n555555555-B;200000000000");

        Assert.Equal(15.50m, withFee.Summary.TotalCargoFee);
        Assert.Equal(2.79m, withFee.Summary.TotalVat);
        Assert.Equal(18.29m, withFee.Summary.TotalCargoFeePlusVat);
        Assert.Equal(1, withFee.Summary.MatchedViaMarketplace);
        Assert.Equal(1, withFee.Summary.MatchedViaMmCargoData);
        Assert.Equal(2, withFee.Summary.TotalMatched);
    }

    [Fact]
    public void SummarizeAddsUpEveryResult()
    {
        const string kargo =
            "Gönderi Kodu;Satıcı\n" +
            "100285010611;Zitek\n" +
            "200000000000;Acme";

        var results = GenerateAll(
            kargo,
            "SiparişNo;Kargo Takip Kodu\n311911494-A;100285010611",
            "CustomerOrderNumber;YK Takip Kodu");

        var summary = CargoSellerReportBuilder.Summarize(results);

        Assert.Equal(2, summary.SellerCount);
        Assert.Equal(2, summary.TotalRecords);
        Assert.Equal(1, summary.MatchedViaMarketplace);
        Assert.Equal(1, summary.Unmatched);
        Assert.Equal(2, summary.Sellers.Count);
    }

    // -----------------------------------------------------------------
    // Workbook / Zip
    // -----------------------------------------------------------------

    [Fact]
    public void BuildWorkbookIsOneSheetMirroringTheSourceWithOrderNumberFirst()
    {
        var result = GenerateOne(
            "Gönderi Kodu;Satıcı\n100285010611;Zitek\n999999999999;Zitek",
            "SiparişNo;Kargo Takip Kodu\n311911494-A;100285010611",
            "CustomerOrderNumber;YK Takip Kodu");

        var bytes = CargoSellerReportBuilder.BuildWorkbook(result);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        var sheet = Assert.Single(workbook.Worksheets);

        Assert.Equal("Sipariş Numarası", sheet.Cell(1, 1).GetString());
        Assert.Equal("Gönderi Kodu", sheet.Cell(1, 2).GetString());
        Assert.Equal("Satıcı", sheet.Cell(1, 3).GetString());

        // First row's code matched Marketplace, so its order number leads; the second's did not match
        // anything, so the new column is blank rather than carrying anything invented.
        Assert.Equal("311911494-A", sheet.Cell(2, 1).GetString());
        Assert.Equal("100285010611", sheet.Cell(2, 2).GetString());
        Assert.Equal("Zitek", sheet.Cell(2, 3).GetString());

        Assert.Equal("", sheet.Cell(3, 1).GetString());
        Assert.Equal("999999999999", sheet.Cell(3, 2).GetString());
    }

    // -----------------------------------------------------------------
    // A real .xlsx, matching the actual courier export: title/date rows above the header, a genuine
    // DateTime cell, and an id long enough to be a scientific-notation risk if number formatting is
    // skipped (as OfferExportReader deliberately does, and as this module deliberately does not).
    // -----------------------------------------------------------------

    [Fact]
    public void RealWorkbookKeepsDatesAndLongIdsReadableAndFindsTheHeaderPastTheTitleRows()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Rapor");

        sheet.Cell(1, 1).SetValue("Toplu Fatura Detay Raporu");
        sheet.Cell(2, 1).SetValue("Rapor Tarihi : 09/09/2026");

        sheet.Cell(3, 1).SetValue("Fatura Gönderi Kodu");
        sheet.Cell(3, 2).SetValue("Oluşturulma Tarihi");
        sheet.Cell(3, 3).SetValue("Satıcı");
        sheet.Cell(3, 4).SetValue("Gönderi Kodu");

        sheet.Cell(4, 1).Value = 910080902860d; // 12 digits — the id that showed up scientific before
        sheet.Cell(4, 2).Value = new DateTime(2026, 8, 5); // a plain day, no time of day
        sheet.Cell(4, 3).SetValue("Zitek");
        sheet.Cell(4, 4).Value = 100285010611d;

        using var kargoStream = new MemoryStream();
        workbook.SaveAs(kargoStream);
        kargoStream.Position = 0;

        var result = Assert.Single(GenerateAllFromXlsx(
            kargoStream,
            "SiparişNo;Kargo Takip Kodu\n311911494-A;100285010611",
            "CustomerOrderNumber;YK Takip Kodu"));

        var row = Assert.Single(result.Rows);
        Assert.Equal("910080902860", row.Cells[0]); // Fatura Gönderi Kodu — not "9.1008090286E11"
        Assert.Equal("05.08.2026", row.Cells[1]);   // Oluşturulma Tarihi — not "46239.0" or a bare .ToString()
        Assert.Equal("100285010611", row.TrackingCode);
        Assert.Equal("311911494-A", row.OrderNumber);
    }

    [Fact]
    public void BuildZipProducesOneEntryPerSellerPlusOneOverview()
    {
        const string kargo =
            "Gönderi Kodu;Satıcı\n" +
            "100285010611;Zitek\n" +
            "200000000000;Acme";

        var results = GenerateAll(
            kargo,
            "SiparişNo;Kargo Takip Kodu\n311911494-A;100285010611",
            "CustomerOrderNumber;YK Takip Kodu");

        var bytes = CargoSellerReportBuilder.BuildZip(results, new DateTime(2026, 9, 22, 10, 30, 0));

        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(3, archive.Entries.Count);
        Assert.Contains(archive.Entries, e => e.Name.StartsWith("kargo_raporu_Acme_", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, e => e.Name.StartsWith("kargo_raporu_Zitek_", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, e => e.Name.StartsWith("Tum_Saticilar_Ozeti_", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------
    // Helpers — CSV in memory, the same shortcut ReturnListBuilderTests uses: the reader is picked
    // from the file name, so a whole export fits in a couple of lines of text.
    // -----------------------------------------------------------------

    static CargoSellerReportAnalyzeResult Analyze(string kargo, string? sellerColumnOverride = null)
    {
        using var stream = Csv(kargo);
        return CargoSellerReportBuilder.Analyze(stream, "kargo.csv", sellerColumnOverride);
    }

    static IReadOnlyList<CargoSellerReportResult> GenerateAll(
        string kargo, string marketplace, string mm, string sellerColumn = "Satıcı")
    {
        using var kargoStream = Csv(kargo);
        using var marketplaceStream = Csv(marketplace);
        using var mmStream = Csv(mm);

        return CargoSellerReportBuilder.GenerateAll(
            kargoStream, "kargo.csv", sellerColumn,
            marketplaceStream, "marketplace.csv",
            mmStream, "mm.csv");
    }

    /// <summary>For fixtures that only ever put one seller in the cargo file.</summary>
    static CargoSellerReportResult GenerateOne(
        string kargo, string marketplace, string mm, string sellerColumn = "Satıcı") =>
        Assert.Single(GenerateAll(kargo, marketplace, mm, sellerColumn));

    /// <summary>For fixtures that need a real workbook rather than the CSV shortcut — a genuine
    /// DateTime cell or a number format only exists once something actually writes an .xlsx.</summary>
    static IReadOnlyList<CargoSellerReportResult> GenerateAllFromXlsx(
        Stream kargoStream, string marketplace, string mm, string sellerColumn = "Satıcı")
    {
        using var marketplaceStream = Csv(marketplace);
        using var mmStream = Csv(mm);

        return CargoSellerReportBuilder.GenerateAll(
            kargoStream, "kargo.xlsx", sellerColumn,
            marketplaceStream, "marketplace.csv",
            mmStream, "mm.csv");
    }

    static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));
}
