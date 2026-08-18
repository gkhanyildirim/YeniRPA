using System.Text;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// Builds the three uploads the Return SLA report reads, as CSV in memory.
///
/// <para>CSV rather than XLSX on purpose: <c>TabularFile.Read</c> picks its reader from the file
/// name, so a test can describe a whole export in a few lines of text and still go through exactly
/// the same parsing the app does.</para>
/// </summary>
static class ReturnFiles
{
    const char Sep = ';';

    /// <summary>The orders export, one row per order line.</summary>
    public static string Orders(params OrderRow[] rows)
    {
        var lines = new List<string>
        {
            Join("Order number", "Seller ID", "Seller", "Status", "Date created",
                 "Customer debit date", "Amount", "Currency")
        };

        foreach (var row in rows)
            lines.Add(Join(row.OrderNumber, row.SellerId, row.Seller, row.Status,
                row.DateCreated, row.DebitDate, row.Amount, "TRY"));

        return string.Join("\n", lines);
    }

    /// <summary>Return template A — "Marketplace Iade &amp; Degisim Talepleri".</summary>
    public static string TemplateA(params TemplateARow[] rows)
    {
        var lines = new List<string>
        {
            Join("SiparişNo", "Satıcı Id", "Kargo Takip Kodu", "Talep Tarihi", "Talep Nedeni")
        };

        foreach (var row in rows)
            lines.Add(Join(row.OrderNo, row.SellerId, row.TrackingCode, row.RequestDate, row.Reason));

        return string.Join("\n", lines);
    }

    /// <summary>Return template B — the "…-MP" export.</summary>
    public static string TemplateB(params TemplateBRow[] rows)
    {
        var lines = new List<string>
        {
            Join("CustomerOrderNumber", "MarketPlaceId", "YK Takip Kodu",
                 "Kargo Kodu Oluşturma Tarihi", "State")
        };

        foreach (var row in rows)
            lines.Add(Join(row.OrderNo, row.MarketPlaceId, row.TrackingCode, row.ShipDate, row.State));

        return string.Join("\n", lines);
    }

    public static ReturnSlaData Build(string orders, string? templateA = null, string? templateB = null)
    {
        using var ordersStream = Stream(orders);
        using var aStream = templateA is null ? null : Stream(templateA);
        using var bStream = templateB is null ? null : Stream(templateB);

        return ReturnSlaReportBuilder.BuildData(
            ordersStream, "orders.csv",
            aStream, aStream is null ? null : "template-a.csv",
            bStream, bStream is null ? null : "template-b.csv");
    }

    /// <summary>A date the templates' way: day first, dotted.</summary>
    public static string DaysAgo(int days) => DateTime.Now.AddDays(-days).ToString("dd.MM.yyyy");

    static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    static string Join(params string[] fields) => string.Join(Sep, fields);

    public sealed record OrderRow(
        string OrderNumber,
        string Status,
        string Seller = "Test Seller",
        string SellerId = "11616.0",
        string DateCreated = "2026-07-01 10:00:00",
        string DebitDate = "",
        string Amount = "100.00");

    public sealed record TemplateARow(
        string OrderNo,
        string TrackingCode = "1234567890",
        string RequestDate = "",
        string SellerId = "",
        string Reason = "Ürünü beğenmedim");

    public sealed record TemplateBRow(
        string OrderNo,
        string TrackingCode = "1234567890",
        string ShipDate = "",
        string MarketPlaceId = "",
        string State = "Shipped");
}
