namespace YeniRPA.Web.Services.SalesAnalysis;

/// <summary>
/// The one place the Mirakl orders export's column headers are spelled out for the Sales Analysis.
/// Every header the reader looks up is a constant here, so a renamed export column is a one-line fix
/// and the "missing column" message names exactly the header the operator has to look for.
///
/// <para><b>Personal data is deliberately absent.</b> Customer e-mail, phone, name and address
/// columns are never mapped, so <see cref="SalesOrderReader"/> cannot read them even by accident —
/// the analysis has no use for them. <see cref="CustomerId"/> is the only customer identifier read,
/// and the city is the only part of the shipping address.</para>
/// </summary>
public static class SalesColumnMap
{
    public const string DateCreated = "Date created";
    public const string OrderNumber = "Order number";
    public const string OrderLineNo = "Order line no.";
    public const string Status = "Status";
    public const string Quantity = "Quantity";
    public const string UnitPrice = "Unit price";
    public const string Amount = "Amount";
    public const string Currency = "Currency";
    public const string ShippingPrice = "Shipping price";
    public const string OrderTotalExclTaxes = "Total order amount excl. taxes (including shipping charges)";
    public const string OrderTotalInclVat = "Total order amount incl. VAT (including shipping charges)";
    public const string CategoryLabel = "Category label";
    public const string CategoryCode = "Category code";
    public const string Brand = "Brand";
    public const string Seller = "Seller";
    public const string ProductSku = "Product SKU";
    public const string SellerSku = "Seller SKU";
    public const string ProductTitle = "Localized Product Title";
    public const string OfferState = "Offer state";
    public const string Commission = "Commission (excluding taxes)";
    public const string TransferredToSeller = "Amount transferred to seller (including taxes)";
    public const string LineWithCancelations = "Order line with cancelations";
    public const string CanceledAmount = "Total canceled amount (including taxes)";
    public const string CancellationRequestStatus = "Cancellation Request Status";
    public const string City = "Shipping address city";
    public const string ShippingCompany = "Shipping company";
    public const string LeadTimeToShip = "Lead time to ship";
    public const string CustomerId = "Customer ID";

    /// <summary>Columns without which no figure on the page can be computed.</summary>
    public static readonly string[] Required =
    [
        DateCreated, OrderNumber, Status, Quantity, Amount, ProductSku,
    ];

    /// <summary>
    /// Columns that only feed one breakdown each. A file without one still loads; the affected
    /// breakdown shows everything under a single "(unknown)" bucket and the load result lists the
    /// column as missing.
    /// </summary>
    public static readonly string[] Optional =
    [
        OrderLineNo, UnitPrice, Currency, ShippingPrice, OrderTotalExclTaxes, OrderTotalInclVat,
        CategoryLabel, CategoryCode, Brand, Seller, SellerSku, ProductTitle, OfferState, Commission,
        TransferredToSeller, LineWithCancelations, CanceledAmount, CancellationRequestStatus, City,
        ShippingCompany, LeadTimeToShip, CustomerId,
    ];
}
