namespace YeniRPA.Web.Services.SalesAnalysis;

/// <summary>
/// One order line of the Mirakl orders export, as the Sales Analysis sees it. Only the fields an
/// analysis reads are carried — see <see cref="SalesColumnMap"/> for why no personal data is here.
///
/// <para><see cref="Created"/> is the export's wall-clock time, read as Europe/Istanbul local time
/// and kept as <see cref="DateTimeKind.Unspecified"/>. The export carries no offset, and every
/// period filter compares calendar days in that same frame, so no conversion is ever applied.</para>
///
/// <para>Order-level money (<see cref="ShippingPrice"/>, <see cref="OrderTotalInclVat"/>, …) repeats
/// on every line of a multi-line order. Anything that sums it must first reduce to one value per
/// <see cref="OrderNumber"/> — <see cref="SalesMetrics"/> does; line-level figures use
/// <see cref="Amount"/>.</para>
/// </summary>
public sealed record SalesLine
{
    public string OrderNumber { get; init; } = "";
    public string OrderLineNo { get; init; } = "";
    public DateTime Created { get; init; }
    public string Status { get; init; } = "";
    public double Quantity { get; init; }

    /// <summary>Line total including VAT. The basis of every sales figure in the analysis.</summary>
    public double Amount { get; init; }

    /// <summary>Kept for reference only: it excludes the withholding tax, so Unit price × Quantity
    /// sums to slightly less than <see cref="Amount"/>. Prices are derived as Amount / Quantity.</summary>
    public double UnitPrice { get; init; }

    public string Currency { get; init; } = "";
    public double ShippingPrice { get; init; }
    public double OrderTotalExclTaxes { get; init; }
    public double OrderTotalInclVat { get; init; }
    public string CategoryLabel { get; init; } = "";
    public string CategoryCode { get; init; } = "";
    public string Brand { get; init; } = "";
    public string Seller { get; init; } = "";
    public string ProductSku { get; init; } = "";
    public string SellerSku { get; init; } = "";
    public string Title { get; init; } = "";
    public string OfferState { get; init; } = "";
    public double Commission { get; init; }
    public double TransferredToSeller { get; init; }
    public bool LineWithCancelations { get; init; }
    public double CanceledAmount { get; init; }
    public string CancellationRequestStatus { get; init; } = "";
    public string City { get; init; } = "";
    public string ShippingCompany { get; init; } = "";
    public double LeadTimeToShip { get; init; }
    public string CustomerId { get; init; } = "";

    public DateOnly Day => DateOnly.FromDateTime(Created);

    public bool IsCanceled => string.Equals(Status, SalesStatuses.Canceled, StringComparison.OrdinalIgnoreCase);
    public bool IsRejected => string.Equals(Status, SalesStatuses.Rejected, StringComparison.OrdinalIgnoreCase);

    /// <summary>The product identity: Product SKU (Seller SKU is blank on about a third of lines),
    /// falling back to the title only when the SKU itself is missing.</summary>
    public string ProductKey => ProductSku.Length > 0 ? ProductSku : Title;
}

/// <summary>The Mirakl line statuses the analysis treats specially, and their Turkish labels.</summary>
public static class SalesStatuses
{
    public const string Canceled = "Canceled";
    public const string Rejected = "Rejected";

    /// <summary>The statuses excluded from "sales" when the operator has not chosen any.</summary>
    public static readonly string[] ExcludedByDefault = [Canceled, Rejected];

    static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Received"] = "Teslim alındı",
        ["Shipped"] = "Kargoda",
        ["Awaiting shipment"] = "Kargo bekliyor",
        ["Pending acceptance"] = "Onay bekliyor",
        ["Canceled"] = "İptal",
        ["Incident Open"] = "Sorun açık",
        ["Rejected"] = "Reddedildi",
        ["Refunded"] = "İade edildi",
        ["Closed"] = "Kapandı",
    };

    public static string Label(string status) =>
        Labels.TryGetValue(status, out var label) ? label : status;
}
