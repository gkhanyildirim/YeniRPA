using System.Text;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// Builds the two incident exports as semicolon CSV in memory.
///
/// <para>CSV rather than XLSX on purpose: <c>TabularFile.Read</c> picks its reader from the file
/// name, so a test can describe a whole export in a few lines of text and still go through exactly
/// the same delimiter detection, quoting and date parsing the app does.</para>
/// </summary>
static class IncidentFiles
{
    const char Sep = ';';

    /// <summary>The header both exports carry, in the order Mirakl writes it.</summary>
    public static readonly string[] Header =
    [
        "Order number", "Order created on", "Customer name", "Seller", "Status",
        "Number of messages", "Opened by", "Opened by user", "Opened on", "Reason",
        "Closed by", "Closed by user", "Closed on", "Closing reason",
        "Product SKU", "Product", "Quantity",
        "Total order amount incl. VAT (including shipping charges)", "Currency",
        "Last action by", "Last action by user", "Last action date", "Last action",
    ];

    public static string File(params Row[] rows)
    {
        var lines = new List<string> { Join(Header) };

        foreach (var row in rows)
            lines.Add(Join([
                row.OrderNumber, row.OrderCreatedOn, row.CustomerName, row.Seller, row.Status,
                row.MessageCount, row.OpenedBy, row.OpenedByUser, row.OpenedOn, row.Reason,
                row.ClosedBy, row.ClosedByUser, row.ClosedOn, row.ClosingReason,
                row.ProductSku, row.Product, row.Quantity, row.Amount, row.Currency,
                row.LastActionBy, row.LastActionByUser, row.LastActionDate, row.LastAction,
            ]));

        return string.Join("\n", lines);
    }

    public static IncidentsData Build(string? openFile, string? closedFile = null)
    {
        using var openStream = openFile is null ? null : Stream(openFile);
        using var closedStream = closedFile is null ? null : Stream(closedFile);

        return IncidentsReportBuilder.BuildData(
            openStream, openStream is null ? null : "incidents.csv",
            closedStream, closedStream is null ? null : "incidents-closed.csv");
    }

    /// <summary>A date the export's way: month first, 12-hour clock with an AM/PM marker.</summary>
    public static string HoursAgo(double hours) =>
        DateTime.Now.AddHours(-hours).ToString("MM/dd/yyyy hh:mm:ss tt",
            System.Globalization.CultureInfo.InvariantCulture);

    public static string DaysAgo(double days) => HoursAgo(days * 24);

    static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    static string Join(string[] fields) => string.Join(Sep, fields);

    /// <summary>
    /// One incident row. The defaults describe a plain open incident the customer raised, so a test
    /// only has to name the columns it is actually about.
    /// </summary>
    public sealed record Row(
        string OrderNumber = "01259_326674352-A",
        string OrderCreatedOn = "08/29/2026 02:50:02 PM",
        string CustomerName = "Onur Umulgan",
        string Seller = "Test Seller",
        string Status = "Incident in progress",
        string MessageCount = "3",
        string OpenedBy = "Customer",
        string OpenedByUser = "",
        string OpenedOn = "09/01/2026 09:00:00 AM",
        string Reason = "Defective item",
        string ClosedBy = "",
        string ClosedByUser = "",
        string ClosedOn = "",
        string ClosingReason = "",
        string ProductSku = "165612306",
        string Product = "E284S",
        string Quantity = "1",
        string Amount = "3599.00",
        string Currency = "TRY",
        string LastActionBy = "Customer",
        string LastActionByUser = "",
        string LastActionDate = "09/02/2026 10:00:00 AM",
        string LastAction = "Message");
}
