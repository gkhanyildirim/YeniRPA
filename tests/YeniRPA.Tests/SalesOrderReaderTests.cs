using ClosedXML.Excel;
using YeniRPA.Web.Services.SalesAnalysis;

namespace YeniRPA.Tests;

/// <summary>
/// <see cref="SalesOrderReader"/>: column mapping, the import validation report, and that personal
/// data columns present in the export never reach the model.
/// </summary>
public class SalesOrderReaderTests
{
    static readonly string[] Headers =
    [
        "Date created", "Order number", "Order line no.", "Status", "Quantity", "Amount", "Unit price",
        "Product SKU", "Category label", "Brand", "Seller", "Localized Product Title", "Currency",
        "Customer email address", "Shipping address phone", "Shipping address first name", "Shipping address city",
    ];

    static MemoryStream Workbook(string[] headers, params object[][] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Data");
        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).Value = headers[c];

        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                sheet.Cell(r + 2, c + 1).Value = rows[r][c] switch
                {
                    double d => d,
                    int i => i,
                    var other => other.ToString(),
                };
            }
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    static object[] Row(string date, string order, string lineNo, string status, double qty, double amount, string sku = "184268797") =>
        [date, order, lineNo, status, qty, amount, amount, sku, "SMARTPHONES", "APPLE", "Seller A", "iPhone", "TRY",
         "someone@example.com", "+90 555 000 00 00", "Ayşe", "İstanbul"];

    [Fact]
    public void Reads_the_export_date_format_month_first()
    {
        using var stream = Workbook(Headers, Row("09/28/2026 12:07:33 AM", "O1", "L1", "Received", 1, 399.9));

        var line = Assert.Single(SalesOrderReader.Read(stream, "orders.xlsx").Lines);

        Assert.Equal(new DateTime(2026, 9, 28, 0, 7, 33), line.Created);
        Assert.Equal(DateTimeKind.Unspecified, line.Created.Kind);
        Assert.Equal(399.9, line.Amount);
        Assert.Equal("184268797", line.ProductSku);
        Assert.Equal("İstanbul", line.City);
    }

    [Fact]
    public void A_missing_required_column_names_it()
    {
        var headers = Headers.Where(h => h != "Product SKU").ToArray();
        using var stream = Workbook(headers, ["09/28/2026 12:07:33 AM", "O1"]);

        var ex = Assert.Throws<InvalidOperationException>(() => SalesOrderReader.Read(stream, "orders.xlsx"));

        Assert.Contains("'Product SKU'", ex.Message);
    }

    [Fact]
    public void Missing_optional_columns_are_listed_not_fatal()
    {
        string[] headers = ["Date created", "Order number", "Status", "Quantity", "Amount", "Product SKU"];
        using var stream = Workbook(headers, ["09/28/2026 12:07:33 AM", "O1", "Received", 1.0, 10.0, "A"]);

        var dataset = SalesOrderReader.Read(stream, "orders.xlsx");

        Assert.Single(dataset.Lines);
        Assert.Contains("Category label", dataset.Report.MissingOptionalColumns);
    }

    [Fact]
    public void Validation_reports_bad_dates_negative_amounts_zero_quantities_and_duplicate_lines()
    {
        using var stream = Workbook(Headers,
            Row("not a date", "O1", "L1", "Received", 1, 10),
            Row("09/28/2026 01:00:00 PM", "O2", "L2", "Received", 1, -5),
            Row("09/28/2026 02:00:00 PM", "O3", "L3", "Shipped", 0, 10),
            Row("09/28/2026 03:00:00 PM", "O4", "L4", "Canceled", 0, 0),
            Row("09/28/2026 04:00:00 PM", "O5", "L4", "Received", 1, 10));

        var dataset = SalesOrderReader.Read(stream, "orders.xlsx");
        var issues = dataset.Report.Issues.ToDictionary(i => i.Kind);

        Assert.Equal(5, dataset.Report.RowsRead);
        Assert.Equal(4, dataset.Report.RowsUsed);
        Assert.Equal([2], issues["unparsedDate"].SampleRows);
        Assert.Equal(1, issues["negative"].Count);
        Assert.Equal(1, issues["zeroQuantity"].Count);      // the canceled line is not flagged
        Assert.Equal([6], issues["duplicateLine"].SampleRows);
    }

    [Fact]
    public void The_model_carries_no_personal_data()
    {
        string[] forbidden = ["Email", "Mail", "Phone", "Street", "FirstName", "LastName", "Zip", "Address"];
        var properties = typeof(SalesLine).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain(properties, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("184268797.0", "184268797")]
    [InlineData("ABC-12.0", "ABC-12.0")]
    [InlineData("12345", "12345")]
    public void Numeric_skus_lose_the_float_suffix_others_are_kept(string raw, string expected)
    {
        Assert.Equal(expected, SalesOrderReader.NormalizeSku(raw));
    }
}
