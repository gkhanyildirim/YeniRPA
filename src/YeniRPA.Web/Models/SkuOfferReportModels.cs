namespace YeniRPA.Web.Models;

/// <summary>One product (Product SKU) with its offer history and, when the traffic file knows it, its visibility.</summary>
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

    /// <summary><c>false</c> when the product is missing from the traffic file — unknown, not zero.</summary>
    public bool HasTraffic { get; init; }
    public double? PdpViews { get; init; }
    public double? AddToCart { get; init; }
    public double? AtcRate { get; init; }
    public double? Impressions { get; init; }
    public double? Sold30d { get; init; }
    public double? Gmv30d { get; init; }

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
    double TotalImpressions);

public sealed record SkuMonthPoint(string Month, int Skus, int Closed);

public sealed record SkuCategoryPoint(string Category, int Active, int Closed);

public sealed record SkuTopTrafficPoint(string Sku, string Product, double PdpViews, bool FirstOfferClosed);

public sealed record SkuOfferCharts(
    IReadOnlyList<SkuMonthPoint> ByMonth,
    IReadOnlyList<SkuCategoryPoint> ByCategory,
    IReadOnlyList<SkuTopTrafficPoint> TopTraffic);

public sealed class SkuOfferReport
{
    public required IReadOnlyList<SkuOfferRow> Rows { get; init; }
    public required SkuOfferSummary Summary { get; init; }
    public required SkuOfferCharts Charts { get; init; }
}

public sealed class SkuOfferQuery
{
    public string? Search { get; init; }

    /// <summary>all | firstClosed | noActive | noTraffic | hasTraffic</summary>
    public string? Filter { get; init; }
    public string? Sort { get; init; }
    public bool Descending { get; init; }
}

public sealed record SkuOfferPage(int Total, int Page, int PageSize, IReadOnlyList<SkuOfferRow> Rows);
