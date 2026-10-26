namespace YeniRPA.Web.Models;

/// <summary>One product (Product SKU) with its offer history and, when the combined sales file knows it, its funnel and sales.</summary>
public sealed class SkuOfferRow
{
    public string Sku { get; init; } = "";
    public string Product { get; init; } = "";
    public string Brand { get; init; } = "";
    public string Category { get; init; } = "";

    /// <summary>Creation date of the oldest offer on the product; <c>null</c> when none of its dates could be read.</summary>
    public DateTime? FirstOfferDate { get; init; }
    public string FirstOfferSeller { get; init; } = "";
    public bool FirstOfferActive { get; init; }
    public string FirstOfferReason { get; init; } = "";

    public int TotalOffers { get; init; }
    public int ActiveOffers { get; init; }

    /// <summary>Every seller that still holds an active offer on the product, once each, in the order they first appear; empty when none is active.</summary>
    public IReadOnlyList<string> ActiveSellers { get; init; } = [];

    /// <summary><c>false</c> when the product is missing from the combined sales file — unknown, not zero.</summary>
    public bool HasTraffic { get; init; }

    /// <summary>Search / category-search product impressions (sessions) over the file's whole period.</summary>
    public double? Impressions { get; init; }
    public double? Clicks { get; init; }
    public double? Ctr { get; init; }
    public double? PdpViews { get; init; }
    public double? AddToCart { get; init; }
    public double? AtcRate { get; init; }
    public double? CheckoutSessions { get; init; }
    public double? OrderSessions { get; init; }

    /// <summary>Order sessions / PDP sessions.</summary>
    public double? CrPdp { get; init; }

    /// <summary>Order sessions / checkout sessions.</summary>
    public double? CrCheckout { get; init; }
    public double? RemoveFromCart { get; init; }

    /// <summary>Sold quantity and GMV (without shipping) over the whole period; <c>null</c> when the product has no tracked sales.</summary>
    public double? SoldQty { get; init; }
    public double? Gmv { get; init; }
    public double? AvgPrice { get; init; }
    public double? Aov { get; init; }

    /// <summary>Quantity / GMV per period, in the order of <see cref="SkuOfferCharts.Periods"/>; <c>null</c> without any sale.</summary>
    public double?[]? PeriodQty { get; init; }
    public double?[]? PeriodGmv { get; init; }

    /// <summary><c>false</c> when the product is missing from the approvals file.</summary>
    public bool HasApproval { get; init; }
    public DateTime? ApprovalDate { get; init; }
    public string ApprovedBy { get; init; } = "";

    /// <summary>baby | young | adult | mature (see <see cref="SkuAgeGroups"/>); <c>null</c> without a readable first-offer date.</summary>
    public string? AgeGroup { get; init; }
    public int? AgeDays { get; init; }

    /// <summary>Set when the first offer is no longer active; the sentence the team asked for.</summary>
    public string? Warning { get; init; }

    /// <summary><c>critical</c> when no offer on the product is active any more, <c>warning</c> when others still are.</summary>
    public string? Severity { get; init; }
}

public sealed record SkuOfferSummary(
    int TotalSkus,
    int TotalOffers,
    int ActiveOffers,
    int FirstOfferClosedSkus,
    int NoActiveOfferSkus,
    int SkusWithTraffic,
    int SkusWithoutTraffic,
    int TrafficProductsNotInOffers,
    int UnreadableDates,
    double TotalPdpViews,
    double TotalAddToCart,
    bool HasImpressions,
    double TotalImpressions,
    int ApprovedSkus,
    int NotApprovedSkus,
    int ApprovalProductsNotInOffers,
    string SalesWindow,
    int UniqueSkusSold,
    int UniqueSkusSoldInOffers,
    double TotalSoldQty,
    double TotalGmv,
    double OverallCtr,
    double OverallAtcRate,
    double OverallCrPdp,
    int LostTrafficSkus,
    double LostTrafficPdp,
    int PdpButNeverSoldSkus,
    double EurTryRate);

/// <summary>
/// Age of a product's first offer, measured to today in calendar months:
/// under 1 month baby, 1 to under 3 young, 3 to under 6 adult, 6 and over mature.
/// </summary>
public static class SkuAgeGroups
{
    public const string Baby = "baby";
    public const string Young = "young";
    public const string Adult = "adult";
    public const string Mature = "mature";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Baby, "Baby (0-1 mo)"), (Young, "Young (1-3 mo)"), (Adult, "Adult (3-6 mo)"), (Mature, "Mature (6+ mo)")
    ];

    public static string? For(DateTime? firstOffer, DateTime today)
    {
        if (firstOffer is not { } first)
            return null;

        var created = first.Date;
        if (today < created.AddMonths(1)) return Baby;
        if (today < created.AddMonths(3)) return Young;
        if (today < created.AddMonths(6)) return Adult;
        return Mature;
    }
}

public sealed record SkuAgePoint(string Group, string Label, int Skus, int Closed);

public sealed record SkuMonthPoint(string Month, int Skus, int Closed);

public sealed record SkuCategoryPoint(string Category, int Active, int Closed);

public sealed record SkuTopTrafficPoint(string Sku, string Product, double PdpViews, bool FirstOfferClosed);

/// <summary>One period of the combined sales file: how many different products sold in it, and how much.</summary>
public sealed record SkuPeriodPoint(string Label, int UniqueSkusSold, double Qty, double Gmv);

/// <summary>Whole-file funnel totals: impressions down to order sessions.</summary>
public sealed record SkuFunnel(double Impressions, double Clicks, double Pdp, double AddToCart, double Checkout, double Orders);

public sealed record SkuOfferCharts(
    IReadOnlyList<SkuMonthPoint> ByMonth,
    IReadOnlyList<SkuCategoryPoint> ByCategory,
    IReadOnlyList<SkuTopTrafficPoint> TopTraffic,
    IReadOnlyList<SkuAgePoint> ByAge,
    IReadOnlyList<SkuPeriodPoint> Periods,
    SkuFunnel Funnel);

/// <summary>One product from the Products Unavailable (stockout) export, joined to traffic and the offers file.</summary>
public sealed class StockoutRow
{
    public string ProductId { get; init; } = "";
    public string Product { get; init; } = "";
    public string Brand { get; init; } = "";
    public string Category { get; init; } = "";
    public string Sellers { get; init; } = "";

    /// <summary><c>null</c> when the export leaves "Retail buyable" blank.</summary>
    public bool? RetailBuyable { get; init; }

    /// <summary>Last 7 days, from the stockout export.</summary>
    public double Sold7d { get; init; }
    public double Gmv7d { get; init; }

    /// <summary><c>false</c> when the product is missing from the combined sales file: unknown, not zero. PDP figures cover the whole file period.</summary>
    public bool HasTraffic { get; init; }
    public double? PdpViews { get; init; }
    public double? AddToCart { get; init; }
    public double? AtcRate { get; init; }
    public double? Impressions { get; init; }
    public double? CrPdp { get; init; }
    public double? SoldQty { get; init; }
    public double? Gmv { get; init; }

    public bool InOffers { get; init; }
    public int TotalOffers { get; init; }
    public int ActiveOffers { get; init; }
    public DateTime? FirstOfferDate { get; init; }
    public bool FirstOfferClosed { get; init; }
    public string? AgeGroup { get; init; }
    public DateTime? ApprovalDate { get; init; }
    public string ApprovedBy { get; init; } = "";
}

public sealed record StockoutSummary(
    int Products,
    int WithTraffic,
    int WithoutTraffic,
    int NotInOffers,
    double TotalImpressions,
    double TotalPdpViews,
    double SharePctOfAllPdp,
    double TotalAddToCart,
    double Gmv7d,
    double Sold7d);

public sealed record StockoutTopPoint(string ProductId, string Product, double PdpViews);

public sealed class StockoutReport
{
    public required IReadOnlyList<StockoutRow> Rows { get; init; }
    public required StockoutSummary Summary { get; init; }
    public required IReadOnlyList<StockoutTopPoint> TopPdp { get; init; }
}

public sealed class SkuOfferReport
{
    /// <summary>Present only when the optional stockout file was uploaded.</summary>
    public StockoutReport? Stockout { get; init; }

    public required IReadOnlyList<SkuOfferRow> Rows { get; init; }
    public required SkuOfferSummary Summary { get; init; }
    public required SkuOfferCharts Charts { get; init; }
}

public sealed class SkuOfferQuery
{
    public string? Search { get; init; }

    /// <summary>all | firstClosed | noActive | noTraffic | hasTraffic | noApproval | sold | neverSold | lostTraffic</summary>
    public string? Filter { get; init; }

    /// <summary>baby | young | adult | mature; combined with <see cref="Filter"/>.</summary>
    public string? AgeGroup { get; init; }
    public string? Sort { get; init; }
    public bool Descending { get; init; }
}

public sealed record SkuOfferPage(int Total, int Page, int PageSize, IReadOnlyList<SkuOfferRow> Rows);
