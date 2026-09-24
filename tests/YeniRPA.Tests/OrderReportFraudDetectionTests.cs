using ClosedXML.Excel;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The shipping-address fraud-suspect heuristic in <see cref="OrderReportBuilder"/>: orders in the
/// same upload whose shipping address is mutually similar to every other member of a group
/// (complete-linkage), but which belong to different customers, are grouped for manual review. See
/// <see cref="AddressSimilarity"/> for why fuzzy matching is allowed here even though it is banned
/// everywhere else in this codebase.
/// </summary>
public class OrderReportFraudDetectionTests
{
    static readonly string[] Headers =
    [
        "Seller", "Order number", "Date created", "Status", "Quantity", "Amount", "Currency",
        "Shipping deadline", "Shipping date", "Reason", "Shipping company", "Received date",
        "Shipping address street 1", "Shipping address street 2", "Shipping address zip",
        "Shipping address city", "Shipping address country", "Shipping address first name",
        "Shipping address last name", "Customer ID", "Customer email address",
    ];

    sealed record Row(
        string Seller = "Seller A", string OrderNumber = "01259_1-A", string Status = "Received",
        string Street1 = "Atatürk Cad. No:5", string Street2 = "", string Zip = "34000",
        string City = "İstanbul", string Country = "TR", string FirstName = "", string LastName = "",
        string CustomerId = "C1", string CustomerEmail = "", double Amount = 100);

    static MemoryStream Workbook(string[] headers, IReadOnlyList<Row> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Data");

        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).Value = headers[c];

        int Col(string name) => Array.IndexOf(headers, name) + 1;

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var excelRow = r + 2;
            void Set(string col, string value)
            {
                var idx = Col(col);
                if (idx > 0) sheet.Cell(excelRow, idx).Value = value;
            }

            Set("Seller", row.Seller);
            Set("Order number", row.OrderNumber);
            Set("Status", row.Status);
            Set("Currency", "TRY");
            if (Col("Amount") > 0) sheet.Cell(excelRow, Col("Amount")).Value = row.Amount;
            Set("Shipping address street 1", row.Street1);
            Set("Shipping address street 2", row.Street2);
            Set("Shipping address zip", row.Zip);
            Set("Shipping address city", row.City);
            Set("Shipping address country", row.Country);
            Set("Shipping address first name", row.FirstName);
            Set("Shipping address last name", row.LastName);
            Set("Customer ID", row.CustomerId);
            Set("Customer email address", row.CustomerEmail);
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    static YeniRPA.Web.Models.OrderReportData Build(string[] headers, params Row[] rows) =>
        OrderReportBuilder.BuildData(Workbook(headers, rows));

    [Fact]
    public void DifferentCustomersWithNearIdenticalAddressAreGroupedTogether()
    {
        var data = Build(Headers,
            new Row(OrderNumber: "01259_1-A", CustomerId: "C1", Street1: "Atatürk Cad. No:5"),
            new Row(OrderNumber: "01259_2-A", CustomerId: "C2", Street1: "Ataturk Cad No 5"));

        var group = Assert.Single(data.FraudGroups);
        Assert.Equal(2, group.GroupSize);
        Assert.True(group.MinSimilarityPercent > 80);
        Assert.Equal(["01259_1-A", "01259_2-A"], group.Members.Select(m => m.OrderNumber).OrderBy(x => x));
    }

    /// <summary>
    /// The complaint that motivated the min/max split: a 17-member real group showed 80.8% on every
    /// row even though most pairs in it were 95%+ similar — the badge was the group's single weakest
    /// pair, repeated everywhere, which made a tightly-matched order look as loose as the worst one.
    /// This pins the fix with three orders whose three pairwise scores genuinely differ (verified
    /// directly against <see cref="AddressSimilarity.Ratio"/>: A-B = 90.2%, A-C = 97.6%, B-C = 88.1%)
    /// so the group's range and each member's own best-match score are asserted against known,
    /// non-coincidental numbers rather than just "some number above 80".
    /// </summary>
    [Fact]
    public void GroupRangeAndPerMemberSimilarityReflectTheActualSpreadNotJustTheWorstPair()
    {
        var data = Build(Headers,
            new Row(OrderNumber: "01259_A", CustomerId: "C1", Zip: "34000", City: "", Street1: "Merkez Mahallesi Uzun Sokak No 9"),
            new Row(OrderNumber: "01259_B", CustomerId: "C2", Zip: "34000", City: "", Street1: "Merkez Mahallesi Kisa Sokak No 9"),
            new Row(OrderNumber: "01259_C", CustomerId: "C3", Zip: "34000", City: "", Street1: "Merkez Mahallesi Uzun Sokak No 90"));

        var group = Assert.Single(data.FraudGroups);
        Assert.Equal(3, group.GroupSize);
        Assert.Equal(88.1, group.MinSimilarityPercent, 1);
        Assert.Equal(97.6, group.MaxSimilarityPercent, 1);

        // A and C are each other's closest match (97.6%) — that should show on their own rows, not
        // the group's 88.1% floor. B's own best match tops out at its pair with A (90.2%).
        Assert.Equal(97.6, group.Members.Single(m => m.OrderNumber == "01259_A").SimilarityPercent, 1);
        Assert.Equal(90.2, group.Members.Single(m => m.OrderNumber == "01259_B").SimilarityPercent, 1);
        Assert.Equal(97.6, group.Members.Single(m => m.OrderNumber == "01259_C").SimilarityPercent, 1);
    }

    [Fact]
    public void ThreeMutuallySimilarDifferentCustomerOrdersFormOneGroupOfThree()
    {
        var data = Build(Headers,
            new Row(OrderNumber: "01259_1-A", CustomerId: "C1", Street1: "Atatürk Cad. No:5"),
            new Row(OrderNumber: "01259_2-A", CustomerId: "C2", Street1: "Ataturk Cad No 5"),
            new Row(OrderNumber: "01259_3-A", CustomerId: "C3", Street1: "Atatürk Cad No:5"));

        var group = Assert.Single(data.FraudGroups);
        Assert.Equal(3, group.GroupSize);
        Assert.Equal(3, group.Members.Count);
        Assert.True(group.MinSimilarityPercent > 80);
    }

    [Fact]
    public void TwoUnrelatedSimilarAddressPairsFormTwoDistinctGroups()
    {
        var data = Build(Headers,
            new Row(OrderNumber: "01259_1-A", CustomerId: "C1", Zip: "34000", Street1: "Atatürk Cad. No:5"),
            new Row(OrderNumber: "01259_2-A", CustomerId: "C2", Zip: "34000", Street1: "Ataturk Cad No 5"),
            new Row(OrderNumber: "01259_3-A", CustomerId: "C3", Zip: "06000", Street1: "Bağdat Cad. No:120"),
            new Row(OrderNumber: "01259_4-A", CustomerId: "C4", Zip: "06000", Street1: "Bagdat Cad No 120"));

        Assert.Equal(2, data.FraudGroups.Count);
        Assert.All(data.FraudGroups, g => Assert.Equal(2, g.GroupSize));

        var groupOf12 = data.FraudGroups.Single(g => g.Members.Any(m => m.OrderNumber == "01259_1-A"));
        var groupOf34 = data.FraudGroups.Single(g => g.Members.Any(m => m.OrderNumber == "01259_3-A"));
        Assert.NotSame(groupOf12, groupOf34);
        Assert.Contains(groupOf12.Members, m => m.OrderNumber == "01259_2-A");
        Assert.Contains(groupOf34.Members, m => m.OrderNumber == "01259_4-A");
    }

    /// <summary>
    /// The regression the real data exposed: with single-linkage (union-find) grouping, A~B and B~C
    /// each clearing the threshold was enough to pull A and C into one group even though A and C are
    /// not alike at all — exactly what produced the implausible 80-100-member groups on a real
    /// export. Complete-linkage requires every member to be similar to every other member, so this
    /// chain must now split rather than merge into one group of three.
    ///
    /// <para>Verified directly against <see cref="AddressSimilarity.Ratio"/>: A-B = 90.2%, B-C =
    /// 87.8%, A-C = 78.0% (below the 80% threshold) — a real chain, not a coincidence of unrelated
    /// addresses that never matched anything.</para>
    /// </summary>
    [Fact]
    public void AChainOfPairwiseMatchesDoesNotMergeIntoOneGroup()
    {
        const string a = "Merkez Mahallesi Uzun Sokak No 9";
        const string b = "Merkez Mahallesi Kisa Sokak No 9";
        const string c = "Yeni Mahallesi Kisa Sokak No 9";

        // Empty city keeps the shared zip/country suffix short, so the street text (where A, B and C
        // deliberately differ) actually drives the similarity score instead of being diluted by a
        // long identical tail.
        var data = Build(Headers,
            new Row(OrderNumber: "01259_A", CustomerId: "C1", Zip: "34000", City: "", Street1: a),
            new Row(OrderNumber: "01259_B", CustomerId: "C2", Zip: "34000", City: "", Street1: b),
            new Row(OrderNumber: "01259_C", CustomerId: "C3", Zip: "34000", City: "", Street1: c));

        // A joins first, B fits with A (>80%), C does not fit with A (78%) so it cannot join the
        // {A,B} group even though C fits with B alone — that lone pairing is exactly the chain a
        // single-linkage algorithm would have merged in.
        var group = Assert.Single(data.FraudGroups);
        Assert.Equal(["01259_A", "01259_B"], group.Members.Select(m => m.OrderNumber).OrderBy(x => x));
    }

    [Fact]
    public void SameCustomerReorderingToTheSameAddressIsNeverFlagged()
    {
        var data = Build(Headers,
            new Row(OrderNumber: "01259_1-A", CustomerId: "C1", Street1: "Atatürk Cad. No:5"),
            new Row(OrderNumber: "01259_2-A", CustomerId: "C1", Street1: "Atatürk Cad. No:5"));

        Assert.Empty(data.FraudGroups);
    }

    [Fact]
    public void MultipleLinesOfTheSameOrderAreNotSelfFlagged()
    {
        var data = Build(Headers,
            new Row(OrderNumber: "01259_1-A", CustomerId: "C1", Street1: "Atatürk Cad. No:5"),
            new Row(OrderNumber: "01259_1-A", CustomerId: "C1", Street1: "Atatürk Cad. No:5"));

        Assert.Empty(data.FraudGroups);
    }

    [Fact]
    public void SimilarStreetTextWithADifferentZipIsNotCompared()
    {
        var data = Build(Headers,
            new Row(OrderNumber: "01259_1-A", CustomerId: "C1", Zip: "34000", Street1: "Atatürk Cad. No:5"),
            new Row(OrderNumber: "01259_2-A", CustomerId: "C2", Zip: "06000", Street1: "Ataturk Cad No 5"));

        Assert.Empty(data.FraudGroups);
    }

    [Fact]
    public void UnrelatedAddressesAreNotFlagged()
    {
        var data = Build(Headers,
            new Row(OrderNumber: "01259_1-A", CustomerId: "C1", Zip: "34000", Street1: "Atatürk Cad. No:5"),
            new Row(OrderNumber: "01259_2-A", CustomerId: "C2", Zip: "34000",
                Street1: "Bağdat Caddesi No:120 Kat:3 Daire:7 Blok B"));

        Assert.Empty(data.FraudGroups);
    }

    [Fact]
    public void MissingCustomerIdentityColumnsDisablesTheFeatureEntirely()
    {
        var headers = Headers.Where(h => h is not ("Customer ID" or "Customer email address")).ToArray();

        var data = Build(headers,
            new Row(OrderNumber: "01259_1-A", Street1: "Atatürk Cad. No:5"),
            new Row(OrderNumber: "01259_2-A", Street1: "Ataturk Cad No 5"));

        Assert.Empty(data.FraudGroups);
    }

    [Fact]
    public void CustomerEmailIsUsedWhenCustomerIdIsBlank()
    {
        var data = Build(Headers,
            new Row(OrderNumber: "01259_1-A", CustomerId: "", CustomerEmail: "a@example.com", Street1: "Atatürk Cad. No:5"),
            new Row(OrderNumber: "01259_2-A", CustomerId: "", CustomerEmail: "a@example.com", Street1: "Atatürk Cad. No:5"));

        // Same customer identity via e-mail fallback — not flagged even though the address matches exactly.
        Assert.Empty(data.FraudGroups);
    }

    [Fact]
    public void MemberAmountAndCurrencySurfaceOnTheGroup()
    {
        var data = Build(Headers,
            new Row(OrderNumber: "01259_1-A", CustomerId: "C1", Street1: "Atatürk Cad. No:5", Amount: 249.9),
            new Row(OrderNumber: "01259_2-A", CustomerId: "C2", Street1: "Ataturk Cad No 5", Amount: 599));

        var group = Assert.Single(data.FraudGroups);
        var first = group.Members.Single(m => m.OrderNumber == "01259_1-A");
        var second = group.Members.Single(m => m.OrderNumber == "01259_2-A");
        Assert.Equal(249.9, first.Amount);
        Assert.Equal(599, second.Amount);
        Assert.Equal("TRY", first.Currency);
    }

    [Fact]
    public void MemberRecipientNameSurfacesSeparatelyFromTheAddress()
    {
        var data = Build(Headers,
            new Row(OrderNumber: "01259_1-A", CustomerId: "C1", Street1: "Atatürk Cad. No:5",
                FirstName: "Mehmet", LastName: "Seçil"),
            new Row(OrderNumber: "01259_2-A", CustomerId: "C2", Street1: "Ataturk Cad No 5",
                FirstName: "Sevda", LastName: "Akça"));

        var group = Assert.Single(data.FraudGroups);
        Assert.Equal("Mehmet Seçil", group.Members.Single(m => m.OrderNumber == "01259_1-A").Name);
        Assert.Equal("Sevda Akça", group.Members.Single(m => m.OrderNumber == "01259_2-A").Name);
    }

    [Fact]
    public void FraudThresholdTravelsWithThePayload()
    {
        var data = Build(Headers, new Row());

        Assert.Equal(OrderReportBuilder.FraudSimilarityThreshold, data.FraudThreshold);
    }

    /// <summary>
    /// A degenerate zip shared by an implausible number of orders (a placeholder/default value, or a
    /// very coarse city-wide code) used to be matched anyway: every candidate was checked against
    /// every group already formed in that bucket, an O(bucket^2) cost with no upper bound. On a real
    /// 75 MB upload that hung for minutes. This pins the fix — the bucket is skipped outright once it
    /// exceeds <see cref="OrderReportBuilder.FraudSimilarityThreshold"/>'s sibling cap — by building
    /// 2,000 different-customer orders sharing one zip with byte-identical addresses (the worst case:
    /// unbounded, every candidate would have matched and grown a single ever-larger group) and
    /// asserting both that no group comes out of it and that building the report stays fast.
    /// </summary>
    [Fact]
    public void AnImplausiblyLargeZipBucketIsSkippedRatherThanMatchedQuadratically()
    {
        var rows = Enumerable.Range(1, 2000)
            .Select(i => new Row(OrderNumber: $"01259_{i}-A", CustomerId: $"C{i}", Zip: "34000",
                Street1: "Atatürk Cad. No:5"))
            .ToArray();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var data = Build(Headers, rows);
        sw.Stop();

        Assert.Empty(data.FraudGroups);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"BuildData took {sw.Elapsed} for a 2,000-row single zip bucket — the O(n^2) cap regressed.");
    }
}
