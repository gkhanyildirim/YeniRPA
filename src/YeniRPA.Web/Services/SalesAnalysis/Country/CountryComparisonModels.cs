namespace YeniRPA.Web.Services.SalesAnalysis.Country;

/// <summary>
/// The request of one country comparison. <see cref="DateScope"/> is "all" (each file's full span)
/// or "overlap" (only the calendar days both files cover). <see cref="Rate"/> is how many units of
/// country A's currency one unit of country B's currency is worth; it is only used when the two
/// files' currencies differ.
/// </summary>
public sealed record CountryComparisonRequest(
    string? Token,
    string? NameA,
    string? NameB,
    string[]? Statuses,
    string? DateScope,
    double? Rate);

/// <summary>What one uploaded file holds. <see cref="Currency"/> is null when the file has none or several.</summary>
public sealed record CountryFileInfo(
    string Name,
    string FileName,
    string MinDate,
    string MaxDate,
    int Days,
    int Lines,
    int Orders,
    string? Currency,
    ImportReport Report);

public sealed record CountryLoadResult(
    string Token,
    CountryFileInfo A,
    CountryFileInfo B,
    bool SameCurrency,
    bool HasOverlap,
    IReadOnlyList<FilterOption> Statuses,
    IReadOnlyList<string> DefaultStatuses);

/// <summary>One country as analysed: the window actually used and the currency its money is shown in.</summary>
public sealed record CountrySide(
    string Name,
    string From,
    string To,
    int Days,
    int Orders,
    int Lines,
    string? Currency,
    string DisplayCurrency);

/// <summary>
/// One metric in both countries. <see cref="Abs"/> is A − B; <see cref="Pct"/> is that difference
/// relative to B (null for rates, whose gap is <see cref="Abs"/> in percentage points).
/// <see cref="Higher"/> says which side has the larger value ("a", "b", "tie");
/// <see cref="Leader"/> says which side is better, and is null for metrics where neither direction
/// is good news. A metric that cannot be compared carries both values but no difference, and a
/// <see cref="Note"/> saying why.
/// </summary>
public sealed record CountryKpi(
    string Key,
    string Label,
    string Kind,
    string? GoodWhen,
    double? A,
    double? B,
    double? Abs,
    double? Pct,
    string? Higher,
    string? Leader,
    bool Comparable,
    string? Note);

/// <summary>One category or brand in both countries. <see cref="Gap"/> is ShareA − ShareB (a ratio difference).</summary>
public sealed record CountryDimensionRow(
    string Key,
    string Label,
    double SalesA,
    double SalesB,
    double? ShareA,
    double? ShareB,
    double? Gap,
    double QtyA,
    double QtyB,
    int OrdersA,
    int OrdersB,
    double? PriceA,
    double? PriceB);

public sealed record CountryDimension(
    bool Available,
    IReadOnlyList<CountryDimensionRow> Rows,
    IReadOnlyList<CountryDimensionRow> StrongA,
    IReadOnlyList<CountryDimensionRow> StrongB,
    int DistinctA,
    int DistinctB,
    int Common,
    double? CommonShareA,
    double? CommonShareB,
    double? Top5ShareA,
    double? Top5ShareB,
    IReadOnlyList<string> Summary,
    string? Note);

/// <summary>One product's figures in one country, with its units in the other country when the SKU sold there too.</summary>
public sealed record CountryProduct(
    string Sku,
    string Title,
    string Brand,
    string Category,
    double Qty,
    double Sales,
    double? Share,
    double OtherQty,
    bool InOther);

public sealed record CountryProducts(
    IReadOnlyList<CountryProduct> TopUnitsA,
    IReadOnlyList<CountryProduct> TopUnitsB,
    IReadOnlyList<CountryProduct> TopSalesA,
    IReadOnlyList<CountryProduct> TopSalesB,
    int DistinctA,
    int DistinctB,
    int Common,
    double? CommonShareA,
    double? CommonShareB,
    IReadOnlyList<string> Summary,
    string? Note);

/// <summary>A count in both countries and each one's share of its own total — e.g. orders on a weekday.</summary>
public sealed record ShareRow(string Key, string Label, int A, int B, double? ShareA, double? ShareB);

/// <summary>Day <see cref="Index"/> of each country's window, aligned by position (day 1 with day 1).</summary>
public sealed record DailyPoint(int Index, string? LabelA, string? LabelB, int OrdersA, int OrdersB, double SalesA, double SalesB);

/// <summary>
/// Order behaviour and timing. <see cref="TrendA"/>/<see cref="TrendB"/> are the change in average
/// daily orders from the first half of the window to the second (null with under four full days).
/// The weekday split is only filled when both windows hold at least a full week.
/// </summary>
public sealed record CountryBehaviour(
    IReadOnlyList<ShareRow> Weekdays,
    bool WeekdaysComparable,
    IReadOnlyList<ShareRow> Hours,
    IReadOnlyList<ShareRow> BasketSizes,
    IReadOnlyList<ShareRow> Statuses,
    IReadOnlyList<DailyPoint> Daily,
    double? TrendA,
    double? TrendB,
    IReadOnlyList<string> Summary);

/// <summary>A highlighted difference. <see cref="Side"/> is the country it favours ("a"/"b") or null.</summary>
public sealed record CountryInsight(string Text, string? Side, string Topic, double Score);

public sealed record CountryComparisonResponse(
    CountrySide A,
    CountrySide B,
    bool MoneyComparable,
    double? Rate,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> NotComparable,
    IReadOnlyList<CountryInsight> Insights,
    IReadOnlyList<string> StrengthsA,
    IReadOnlyList<string> StrengthsB,
    IReadOnlyList<CountryKpi> Kpis,
    CountryDimension Category,
    CountryDimension Brand,
    CountryProducts Products,
    CountryBehaviour Behaviour);
