using YeniRPA.Web.Services.SalesAnalysis;

namespace YeniRPA.Tests;

/// <summary>Small hand-built order lines and contexts for the Sales Analysis tests.</summary>
static class SalesAnalysisFixtures
{
    public static readonly DateOnly Day1 = new(2026, 9, 28);

    public static SalesLine Line(
        string order, string sku, double qty, double amount, int day = 0, string status = "Received",
        string category = "SMARTPHONES", string brand = "APPLE", string seller = "Seller A", string city = "İstanbul",
        double shipping = 0, double commission = 0, double canceledAmount = 0, int hour = 12, string lineNo = "") =>
        new()
        {
            OrderNumber = order,
            OrderLineNo = lineNo,
            ProductSku = sku,
            Title = "Product " + sku,
            Quantity = qty,
            Amount = amount,
            Created = Day1.AddDays(day).ToDateTime(new TimeOnly(hour, 0)),
            Status = status,
            CategoryLabel = category,
            Brand = brand,
            Seller = seller,
            City = city,
            ShippingPrice = shipping,
            Commission = commission,
            CanceledAmount = canceledAmount,
        };

    public static HashSet<string> DefaultStatuses() =>
        SalesMetrics.DefaultSalesStatuses(["Received", "Shipped", "Awaiting shipment", "Pending acceptance", "Canceled", "Rejected", "Incident Open"]);

    /// <summary>A context with A = day 0..aDays-1 and B = the same length right after it.</summary>
    public static SalesAnalysisContext Context(
        IReadOnlyList<SalesLine> a, IReadOnlyList<SalesLine> b, int days = 1, double otherThresholdPct = 1, string outlier = "none")
    {
        var options = new SalesAnalysisOptions(
            Day1, Day1.AddDays(days - 1), Day1.AddDays(days), Day1.AddDays(2 * days - 1),
            DefaultStatuses(), otherThresholdPct, outlier);
        return new SalesAnalysisContext(options, a, b);
    }
}
