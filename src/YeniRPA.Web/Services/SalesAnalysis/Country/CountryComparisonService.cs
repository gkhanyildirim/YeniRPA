using System.Globalization;

namespace YeniRPA.Web.Services.SalesAnalysis.Country;

/// <summary>
/// The "Ülke Kıyaslama" mode of the Sales Analysis: two orders exports, one per country, compared
/// side by side. Country A plays the role of period A and country B of period B, so every headline
/// figure still comes from <see cref="SalesMetrics.Compute"/> and every breakdown from
/// <see cref="DimensionBreakdown"/> — the two modes cannot disagree about what "sales" means.
///
/// <para>Money is only compared when it is in one currency: either both files carry the same one, or
/// the operator gave an exchange rate, in which case country B's amounts are converted into country
/// A's currency before anything is summed. Otherwise money is shown side by side, each in its own
/// currency, with no difference computed — and the page says so.</para>
/// </summary>
public sealed class CountryComparisonService
{
    /// <summary>A share gap smaller than this (in share points, 0.02 = 2 points) is not called a strength.</summary>
    public const double MinShareGap = 0.02;

    /// <summary>A value has to hold at least this share in one of the countries to be listed as strong/weak.</summary>
    public const double MinShare = 0.01;

    const int TopProducts = 10;
    const int TopStrong = 5;
    const int MaxDimensionRows = 300;

    internal static readonly string[] WeekdayNames = ["Pazartesi", "Salı", "Çarşamba", "Perşembe", "Cuma", "Cumartesi", "Pazar"];

    readonly CountryComparisonStore _store;

    public CountryComparisonService(CountryComparisonStore store) => _store = store;

    // -------------------------------------------------------------------------------------------
    // Load
    // -------------------------------------------------------------------------------------------

    public CountryLoadResult Load(Stream streamA, string fileA, Stream streamB, string fileB)
    {
        var a = Read(streamA, fileA, "Ülke A");
        var b = Read(streamB, fileB, "Ülke B");
        var token = _store.Put(new CountryPair(a, fileA, b, fileB));

        var all = a.Lines.Concat(b.Lines).ToList();
        var statuses = all.GroupBy(l => l.Status, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .Select(g => new FilterOption(g.Key, SalesStatuses.Label(g.Key), g.Count()))
            .OrderByDescending(o => o.Lines)
            .ThenBy(o => o.Label, StringComparer.Create(SalesTextTr.Tr, true))
            .ToList();

        var infoA = Info(a, fileA, "Ülke A");
        var infoB = Info(b, fileB, "Ülke B");
        var rangeA = Range(a.Lines);
        var rangeB = Range(b.Lines);

        return new CountryLoadResult(
            token,
            infoA,
            infoB,
            infoA.Currency is not null && string.Equals(infoA.Currency, infoB.Currency, StringComparison.OrdinalIgnoreCase),
            Overlap(rangeA, rangeB) is not null,
            statuses,
            [.. SalesMetrics.DefaultSalesStatuses(statuses.Select(s => s.Value))]);
    }

    static SalesDataset Read(Stream stream, string fileName, string side)
    {
        SalesDataset dataset;
        try
        {
            dataset = SalesOrderReader.Read(stream, fileName);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{side} dosyası ({fileName}): {ex.Message}", ex);
        }
        if (dataset.Lines.Count == 0)
            throw new InvalidOperationException($"{side} dosyasında ({fileName}) tarihi okunabilen sipariş satırı yok.");
        return dataset;
    }

    static CountryFileInfo Info(SalesDataset d, string fileName, string fallback)
    {
        var range = Range(d.Lines);
        return new CountryFileInfo(
            DefaultName(fileName, fallback),
            fileName,
            Iso(range.From),
            Iso(range.To),
            Days(range),
            d.Lines.Count,
            SalesMetrics.CountOrders(d.Lines),
            SingleCurrency(d.Report),
            d.Report);
    }

    /// <summary>The file name without its extension, as a starting suggestion for the country's name.</summary>
    internal static string DefaultName(string fileName, string fallback)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? "").Trim();
        if (name.Length == 0)
            return fallback;
        return name.Length > 40 ? name[..40].TrimEnd() : name;
    }

    // -------------------------------------------------------------------------------------------
    // Analyze
    // -------------------------------------------------------------------------------------------

    public CountryComparisonResponse Analyze(CountryComparisonRequest request)
    {
        var pair = _store.Get(request.Token)
            ?? throw new InvalidOperationException("Yüklenen dosyalar artık bellekte değil (uygulama yeniden başlamış ya da başka dosyalar yüklenmiş). İki dosyayı yeniden yükleyin.");

        var nameA = CleanName(request.NameA, DefaultName(pair.FileA, "Ülke A"));
        var nameB = CleanName(request.NameB, DefaultName(pair.FileB, "Ülke B"));
        if (string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase))
        {
            nameA += " (A)";
            nameB += " (B)";
        }

        var fullA = Range(pair.A.Lines);
        var fullB = Range(pair.B.Lines);
        var overlapOnly = string.Equals(request.DateScope?.Trim(), "overlap", StringComparison.OrdinalIgnoreCase);
        PeriodRange windowA = fullA, windowB = fullB;
        if (overlapOnly)
        {
            var common = Overlap(fullA, fullB)
                ?? throw new InvalidOperationException("İki dosyanın ortak günü yok. 'Tüm tarihler' seçeneğiyle karşılaştırın.");
            windowA = windowB = common;
        }

        var curA = SingleCurrency(pair.A.Report);
        var curB = SingleCurrency(pair.B.Report);
        var sameCurrency = curA is not null && string.Equals(curA, curB, StringComparison.OrdinalIgnoreCase);
        double? rate = !sameCurrency && request.Rate is { } r && double.IsFinite(r) && r > 0 ? r : null;
        var moneyComparable = sameCurrency || rate is not null;
        var displayA = curA ?? "";
        var displayB = rate is not null ? displayA : curB ?? "";

        var allStatuses = pair.A.Lines.Concat(pair.B.Lines).Select(l => l.Status).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var statusSet = request.Statuses is { Length: > 0 }
            ? new HashSet<string>(request.Statuses, StringComparer.OrdinalIgnoreCase)
            : SalesMetrics.DefaultSalesStatuses(allStatuses);
        if (statusSet.Count == 0)
            throw new InvalidOperationException("Satış sayılacak en az bir statü seçin.");

        var linesA = pair.A.Lines.Where(l => In(l, windowA)).ToList();
        var linesB = pair.B.Lines.Where(l => In(l, windowB))
            .Select(l => rate is { } x ? Convert(l, x) : l)
            .ToList();

        var options = new SalesAnalysisOptions(windowA.From, windowA.To, windowB.From, windowB.To, statusSet, 1.0, OutlierFilter.None);
        var ctx = new SalesAnalysisContext(options, linesA, linesB);

        var sideA = new CountrySide(nameA, Tr(windowA.From), Tr(windowA.To), Days(windowA), ctx.MetricsA.Orders, linesA.Count, curA, displayA);
        var sideB = new CountrySide(nameB, Tr(windowB.From), Tr(windowB.To), Days(windowB), ctx.MetricsB.Orders, linesB.Count, curB, displayB);

        var warnings = new List<string>();
        var notComparable = new List<string>();
        CurrencyNotes(pair, nameA, nameB, curA, curB, sameCurrency, rate, warnings, notComparable);
        DateNotes(nameA, nameB, fullA, fullB, windowA, windowB, overlapOnly, warnings);
        PartialDayNotes(pair.A.Lines, windowA, nameA, warnings);
        PartialDayNotes(pair.B.Lines, windowB, nameB, warnings);

        if (ctx.MetricsA.Orders == 0 || ctx.MetricsB.Orders == 0)
            warnings.Add($"{(ctx.MetricsA.Orders == 0 ? nameA : nameB)} için seçili statülerde hiç satış yok; karşılaştırma anlamlı değil.");
        else if (ctx.MetricsA.Orders < SalesAnalysisService.MinOrdersForConfidence || ctx.MetricsB.Orders < SalesAnalysisService.MinOrdersForConfidence)
            warnings.Add($"Ülkelerden en az birinde {SalesAnalysisService.MinOrdersForConfidence} siparişten az veri var " +
                         $"({nameA}: {ctx.MetricsA.Orders}, {nameB}: {ctx.MetricsB.Orders}). Bu kadar az veriyle çıkan farklar yanıltıcı olabilir.");

        var onlyA = StatusesOnlyIn(pair.A.Lines, pair.B.Lines);
        var onlyB = StatusesOnlyIn(pair.B.Lines, pair.A.Lines);
        if (onlyA.Count > 0)
            warnings.Add($"Şu statüler yalnızca {nameA} dosyasında var: {string.Join(", ", onlyA.Select(SalesStatuses.Label))}.");
        if (onlyB.Count > 0)
            warnings.Add($"Şu statüler yalnızca {nameB} dosyasında var: {string.Join(", ", onlyB.Select(SalesStatuses.Label))}.");

        var kpis = Kpis(ctx, pair, Days(windowA), Days(windowB), moneyComparable, curA, curB, nameA, nameB);
        foreach (var k in kpis.Where(k => !k.Comparable && k.Note is not null && !k.Note.StartsWith("Para birimleri", StringComparison.Ordinal)))
            notComparable.Add($"{k.Label}: {k.Note}");

        var hasCategory = HasColumn(pair.A, SalesColumnMap.CategoryLabel) && HasColumn(pair.B, SalesColumnMap.CategoryLabel);
        var hasBrand = HasColumn(pair.A, SalesColumnMap.Brand) && HasColumn(pair.B, SalesColumnMap.Brand);
        var category = Dimension(ctx, l => l.CategoryLabel.Trim().ToUpperInvariant(), SalesTextTr.TitleCase, hasCategory,
            "kategori", "Kategori", nameA, nameB, foldSmall: true);
        var brand = Dimension(ctx, l => l.Brand.Trim().ToUpperInvariant(), SalesTextTr.TitleCase, hasBrand,
            "marka", "Marka", nameA, nameB, foldSmall: false);
        if (category.Note is not null) notComparable.Add(category.Note);
        if (brand.Note is not null) notComparable.Add(brand.Note);

        var products = Products(ctx, nameA, nameB);
        if (products.Note is not null) notComparable.Add(products.Note);

        var behaviour = Behaviour(ctx, pair, windowA, windowB, nameA, nameB);
        if (!behaviour.WeekdaysComparable)
            notComparable.Add("Haftanın günü dağılımı: iki dosyanın da en az 7 günlük veri içermesi gerekir; bu yüzden gün bazında kıyas yapılmadı.");

        var composed = CountryInsightComposer.Compose(new CountryInsightInput(
            sideA, sideB, moneyComparable, kpis, category, brand, products, behaviour));

        return new CountryComparisonResponse(
            sideA, sideB, moneyComparable, rate,
            warnings, notComparable,
            composed.Insights, composed.StrengthsA, composed.StrengthsB,
            kpis, category, brand, products, behaviour);
    }

    // -------------------------------------------------------------------------------------------
    // KPIs
    // -------------------------------------------------------------------------------------------

    static List<CountryKpi> Kpis(SalesAnalysisContext ctx, CountryPair pair, int daysA, int daysB,
        bool moneyComparable, string? curA, string? curB, string nameA, string nameB)
    {
        var a = ctx.MetricsA;
        var b = ctx.MetricsB;
        var moneyNote = moneyComparable ? null
            : $"Para birimleri farklı ya da bilinmiyor ({curA ?? "?"} / {curB ?? "?"}) ve kur girilmedi; tutar farkı hesaplanmadı.";

        string? Missing(string column)
        {
            var inA = HasColumn(pair.A, column);
            var inB = HasColumn(pair.B, column);
            if (inA && inB) return null;
            var where = !inA && !inB ? "iki dosyada da" : !inA ? $"{nameA} dosyasında" : $"{nameB} dosyasında";
            return $"'{column}' kolonu {where} yok.";
        }

        var commissionMissing = Missing(SalesColumnMap.Commission);
        var canceledMissing = Missing(SalesColumnMap.CanceledAmount);
        var shippingMissing = Missing(SalesColumnMap.ShippingPrice);

        CountryKpi Money(string key, string label, string? goodWhen, double? va, double? vb, string? missing = null) =>
            missing is not null ? Kpi(key, label, "money", goodWhen, va, vb, false, missing)
            : Kpi(key, label, "money", goodWhen, va, vb, moneyComparable, moneyNote);

        var unitsPerOrderA = SalesMetrics.Ratio(a.Units, a.Orders);
        var unitsPerOrderB = SalesMetrics.Ratio(b.Units, b.Orders);

        return
        [
            Money("gross", "Satış (brüt)", "up", a.GrossSales, b.GrossSales),
            Kpi("orders", "Sipariş sayısı", "count", "up", a.Orders, b.Orders),
            Kpi("units", "Satılan ürün adedi", "count", "up", a.Units, b.Units),
            Money("dailySales", "Günlük ort. satış", "up", SalesMetrics.Ratio(a.GrossSales, daysA), SalesMetrics.Ratio(b.GrossSales, daysB)),
            Kpi("dailyOrders", "Günlük ort. sipariş", "number", "up", SalesMetrics.Ratio(a.Orders, daysA), SalesMetrics.Ratio(b.Orders, daysB)),
            Money("aov", "Sipariş başına ort. tutar", "up", a.AverageOrderValue, b.AverageOrderValue),
            Money("avgPrice", "Ürün başına ort. fiyat", null, a.AvgUnitPrice, b.AvgUnitPrice),
            Kpi("unitsPerOrder", "Sipariş başına ürün adedi", "number", "up", unitsPerOrderA, unitsPerOrderB),
            Kpi("multiItemShare", "Birden çok ürünlü sipariş payı", "rate", "up", MultiItemShare(ctx.SalesA), MultiItemShare(ctx.SalesB)),
            Kpi("cancelRate", "İptal oranı (satır)", "rate", "down", a.CancelRateLines, b.CancelRateLines),
            canceledMissing is not null
                ? Kpi("cancelAmountRate", "İptal oranı (tutar)", "rate", "down", a.CancelRateAmount, b.CancelRateAmount, false, canceledMissing)
                : Kpi("cancelAmountRate", "İptal oranı (tutar)", "rate", "down", a.CancelRateAmount, b.CancelRateAmount),
            Kpi("rejectedRate", "Satıcının reddettiği satır oranı", "rate", "down",
                SalesMetrics.Ratio(a.RejectedLines, a.Lines), SalesMetrics.Ratio(b.RejectedLines, b.Lines)),
            commissionMissing is not null
                ? Kpi("commissionRate", "Komisyon oranı", "rate", null, a.CommissionRate, b.CommissionRate, false, commissionMissing)
                : Kpi("commissionRate", "Komisyon oranı", "rate", null, a.CommissionRate, b.CommissionRate),
            Money("shippingPerOrder", "Sipariş başına kargo ücreti", null,
                SalesMetrics.Ratio(a.ShippingRevenue, a.Orders), SalesMetrics.Ratio(b.ShippingRevenue, b.Orders), shippingMissing),
            Kpi("products", "Satılan farklı ürün", "count", null, Distinct(ctx.SalesA, l => l.ProductKey), Distinct(ctx.SalesB, l => l.ProductKey)),
            Kpi("sellers", "Satış yapan satıcı", "count", null, Distinct(ctx.SalesA, l => l.Seller), Distinct(ctx.SalesB, l => l.Seller),
                Missing(SalesColumnMap.Seller) is null, Missing(SalesColumnMap.Seller)),
        ];
    }

    internal static CountryKpi Kpi(string key, string label, string kind, string? goodWhen, double? a, double? b,
        bool comparable = true, string? note = null)
    {
        if (!comparable)
            return new CountryKpi(key, label, kind, goodWhen, a, b, null, null, null, null, false, note);

        double? abs = a is not null && b is not null ? a - b : null;
        double? pct = kind != "rate" && abs is not null && b is not null && b.Value != 0 ? abs / Math.Abs(b.Value) : null;
        var higher = Higher(a, b, kind);
        string? leader = higher switch
        {
            null => null,
            "tie" => goodWhen is null ? null : "tie",
            _ when goodWhen is null => null,
            _ => (goodWhen == "up") == (higher == "a") ? "a" : "b",
        };
        return new CountryKpi(key, label, kind, goodWhen, a, b, abs, pct, higher, leader, true, note);
    }

    /// <summary>
    /// "a", "b" or "tie". Two values within half a percent of each other (a tenth of a point for a
    /// rate) are a tie: calling a 0,2% gap a lead would claim more than the data supports.
    /// </summary>
    internal static string? Higher(double? a, double? b, string kind)
    {
        if (a is null || b is null)
            return null;
        var diff = a.Value - b.Value;
        var tie = kind == "rate"
            ? Math.Abs(diff) < 0.001
            : Math.Abs(diff) <= 0.005 * Math.Max(Math.Abs(a.Value), Math.Abs(b.Value));
        return tie ? "tie" : diff > 0 ? "a" : "b";
    }

    static double? MultiItemShare(IReadOnlyList<SalesLine> sales)
    {
        var orders = sales.GroupBy(l => l.OrderNumber, StringComparer.Ordinal).Select(g => g.Sum(l => l.Quantity)).ToList();
        return SalesMetrics.Ratio(orders.Count(u => u > 1), orders.Count);
    }

    static double Distinct(IEnumerable<SalesLine> lines, Func<SalesLine, string> key) =>
        lines.Select(key).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    // -------------------------------------------------------------------------------------------
    // Category / brand
    // -------------------------------------------------------------------------------------------

    internal static CountryDimension Dimension(SalesAnalysisContext ctx, Func<SalesLine, string> key, Func<string, string> label,
        bool available, string noun, string title, string nameA, string nameB, bool foldSmall)
    {
        if (!available)
        {
            return new CountryDimension(false, [], [], [], 0, 0, 0, null, null, null, null, [],
                $"{title} karşılaştırması yapılamadı: '{(noun == "kategori" ? SalesColumnMap.CategoryLabel : SalesColumnMap.Brand)}' kolonu dosyalardan en az birinde yok.");
        }

        var rows = DimensionBreakdown.Build(ctx.SalesA, ctx.SalesB, key, label);
        var totalA = rows.Sum(r => r.SalesA);
        var totalB = rows.Sum(r => r.SalesB);

        bool Listed(CountryDimensionRow r) => Math.Max(r.ShareA ?? 0, r.ShareB ?? 0) >= MinShare && r.Key.Length > 0;
        var mapped = rows.Select(Map).ToList();
        var strongA = mapped.Where(r => Listed(r) && r.Gap >= MinShareGap).OrderByDescending(r => r.Gap).Take(TopStrong).ToList();
        var strongB = mapped.Where(r => Listed(r) && r.Gap <= -MinShareGap).OrderBy(r => r.Gap).Take(TopStrong).ToList();

        var inA = rows.Where(r => r.OrdersA > 0).ToList();
        var inB = rows.Where(r => r.OrdersB > 0).ToList();
        var common = rows.Where(r => r.OrdersA > 0 && r.OrdersB > 0).ToList();
        var commonShareA = SalesMetrics.Ratio(common.Sum(r => r.SalesA), totalA);
        var commonShareB = SalesMetrics.Ratio(common.Sum(r => r.SalesB), totalB);
        double? top5A = rows.Count > 5 ? SalesMetrics.Ratio(rows.OrderByDescending(r => r.SalesA).Take(5).Sum(r => r.SalesA), totalA) : null;
        double? top5B = rows.Count > 5 ? SalesMetrics.Ratio(rows.OrderByDescending(r => r.SalesB).Take(5).Sum(r => r.SalesB), totalB) : null;

        var table = foldSmall ? DimensionBreakdown.GroupSmall(rows, 1.0) : rows;
        table = DimensionBreakdown.FoldTail(table, MaxDimensionRows);

        var summary = new List<string>
        {
            $"{nameA} tarafında {inA.Count} {noun}, {nameB} tarafında {inB.Count} {noun} satış yaptı; {common.Count} {noun} iki ülkede de satıldı.",
        };
        if (top5A is { } t5a && top5B is { } t5b)
            summary.Add($"En büyük 5 {noun} toplam satışın {nameA} için {SalesTextTr.Pct(t5a)}, {nameB} için {SalesTextTr.Pct(t5b)} kadarını oluşturuyor.");

        string? note = null;
        if (commonShareA is { } ca && commonShareB is { } cb && (ca < 0.5 || cb < 0.5))
        {
            note = $"{title} payları sınırlı karşılaştırılabilir: satışların yalnızca {nameA} için {SalesTextTr.Pct(ca)}, " +
                   $"{nameB} için {SalesTextTr.Pct(cb)} kadarı iki ülkede ortak olan {noun} adlarından geliyor. " +
                   $"{title} adları ülkeler arasında farklı yazılmış (ör. farklı dilde) olabilir.";
        }

        return new CountryDimension(true, [.. table.Select(Map)], strongA, strongB, inA.Count, inB.Count, common.Count,
            commonShareA, commonShareB, top5A, top5B, summary, note);

        static CountryDimensionRow Map(DimensionRow r) => new(
            r.Key, r.Label, r.SalesA, r.SalesB, r.ShareA, r.ShareB,
            r.ShareA is not null && r.ShareB is not null ? r.ShareA - r.ShareB : null,
            r.QtyA, r.QtyB, r.OrdersA, r.OrdersB, r.PriceA, r.PriceB);
    }

    // -------------------------------------------------------------------------------------------
    // Products
    // -------------------------------------------------------------------------------------------

    readonly record struct ProductAgg(string Title, string Brand, string Category, double Qty, double Sales);

    static Dictionary<string, ProductAgg> AggregateProducts(IEnumerable<SalesLine> lines) =>
        lines.Where(l => l.ProductKey.Length > 0)
            .GroupBy(l => l.ProductKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var first = g.First();
                return new ProductAgg(first.Title, SalesTextTr.TitleCase(first.Brand), SalesTextTr.TitleCase(first.CategoryLabel),
                    g.Sum(l => l.Quantity), g.Sum(l => l.Amount));
            }, StringComparer.OrdinalIgnoreCase);

    internal static CountryProducts Products(SalesAnalysisContext ctx, string nameA, string nameB)
    {
        var a = AggregateProducts(ctx.SalesA);
        var b = AggregateProducts(ctx.SalesB);
        var totalA = a.Values.Sum(p => p.Sales);
        var totalB = b.Values.Sum(p => p.Sales);
        var common = a.Keys.Where(b.ContainsKey).ToList();
        var commonShareA = SalesMetrics.Ratio(common.Sum(k => a[k].Sales), totalA);
        var commonShareB = SalesMetrics.Ratio(common.Sum(k => b[k].Sales), totalB);

        static List<CountryProduct> Top(Dictionary<string, ProductAgg> side, Dictionary<string, ProductAgg> other, double total,
            Func<ProductAgg, double> by) =>
            [.. side.OrderByDescending(kv => by(kv.Value)).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(TopProducts)
                .Select(kv => new CountryProduct(kv.Key, kv.Value.Title, kv.Value.Brand, kv.Value.Category, kv.Value.Qty, kv.Value.Sales,
                    SalesMetrics.Ratio(kv.Value.Sales, total),
                    other.TryGetValue(kv.Key, out var o) ? o.Qty : 0,
                    other.ContainsKey(kv.Key)))];

        var summary = new List<string>
        {
            $"{nameA} tarafında {SalesTextTr.Number(a.Count)}, {nameB} tarafında {SalesTextTr.Number(b.Count)} farklı ürün satıldı.",
        };
        string? note = null;
        if (common.Count > 0 && commonShareA is { } ca && commonShareB is { } cb)
            summary.Add($"{SalesTextTr.Number(common.Count)} ürün (aynı Product SKU) iki ülkede de satıldı; bu ürünler satışların " +
                        $"{nameA} için {SalesTextTr.Pct(ca)}, {nameB} için {SalesTextTr.Pct(cb)} kadarını oluşturuyor.");
        else if (a.Count > 0 && b.Count > 0)
            note = "Ürün eşleştirmesi yapılamadı: iki dosyada ortak Product SKU yok. Ürün listeleri her ülke için ayrı ayrı gösteriliyor.";

        return new CountryProducts(
            Top(a, b, totalA, p => p.Qty), Top(b, a, totalB, p => p.Qty),
            Top(a, b, totalA, p => p.Sales), Top(b, a, totalB, p => p.Sales),
            a.Count, b.Count, common.Count, commonShareA, commonShareB, summary, note);
    }

    // -------------------------------------------------------------------------------------------
    // Behaviour and timing
    // -------------------------------------------------------------------------------------------

    internal static CountryBehaviour Behaviour(SalesAnalysisContext ctx, CountryPair? pair, PeriodRange windowA, PeriodRange windowB,
        string nameA, string nameB)
    {
        // One timestamp and one unit count per order: the time its first line was created.
        static List<(DateTime Created, double Units)> Orders(IEnumerable<SalesLine> sales) =>
            [.. sales.GroupBy(l => l.OrderNumber, StringComparer.Ordinal).Select(g => (g.Min(l => l.Created), g.Sum(l => l.Quantity)))];

        var ordersA = Orders(ctx.SalesA);
        var ordersB = Orders(ctx.SalesB);

        static int WeekdayIndex(DateTime d) => ((int)d.DayOfWeek + 6) % 7;

        var weekdays = Shares(Enumerable.Range(0, 7).Select(i => (i.ToString(CultureInfo.InvariantCulture), WeekdayNames[i])),
            ordersA.Select(o => WeekdayIndex(o.Created).ToString(CultureInfo.InvariantCulture)),
            ordersB.Select(o => WeekdayIndex(o.Created).ToString(CultureInfo.InvariantCulture)));
        var weekdaysComparable = Days(windowA) >= 7 && Days(windowB) >= 7;

        var hours = Shares(Enumerable.Range(0, 24).Select(h => (h.ToString(CultureInfo.InvariantCulture), $"{h:00}:00")),
            ordersA.Select(o => o.Created.Hour.ToString(CultureInfo.InvariantCulture)),
            ordersB.Select(o => o.Created.Hour.ToString(CultureInfo.InvariantCulture)));

        static string Bucket(double units) => units <= 1 ? "1" : units <= 2 ? "2" : units <= 4 ? "3-4" : "5+";
        var baskets = Shares([("1", "1 adet"), ("2", "2 adet"), ("3-4", "3–4 adet"), ("5+", "5 ve üzeri")],
            ordersA.Select(o => Bucket(o.Units)), ordersB.Select(o => Bucket(o.Units)));

        var statusKeys = ctx.AllA.Concat(ctx.AllB).Select(l => l.Status).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(s => (s, SalesStatuses.Label(s))).ToList();
        var statuses = Shares(statusKeys, ctx.AllA.Select(l => l.Status), ctx.AllB.Select(l => l.Status))
            .OrderByDescending(s => s.A + s.B).ToList();

        // Daily series, aligned by position so two different date ranges still line up day 1 with day 1.
        var dayOrdersA = ordersA.GroupBy(o => DateOnly.FromDateTime(o.Created)).ToDictionary(g => g.Key, g => g.Count());
        var dayOrdersB = ordersB.GroupBy(o => DateOnly.FromDateTime(o.Created)).ToDictionary(g => g.Key, g => g.Count());
        var daySalesA = ctx.SalesA.GroupBy(l => l.Day).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        var daySalesB = ctx.SalesB.GroupBy(l => l.Day).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        var daysA = Days(windowA);
        var daysB = Days(windowB);
        var daily = Enumerable.Range(0, Math.Max(daysA, daysB)).Select(i =>
        {
            var da = windowA.From.AddDays(i);
            var db = windowB.From.AddDays(i);
            return new DailyPoint(i,
                i < daysA ? Tr(da) : null,
                i < daysB ? Tr(db) : null,
                i < daysA ? dayOrdersA.GetValueOrDefault(da) : 0,
                i < daysB ? dayOrdersB.GetValueOrDefault(db) : 0,
                i < daysA ? daySalesA.GetValueOrDefault(da) : 0,
                i < daysB ? daySalesB.GetValueOrDefault(db) : 0);
        }).ToList();

        var partialA = pair is null ? new HashSet<DateOnly>() : PartialEdgeDays(pair.A.Lines);
        var partialB = pair is null ? new HashSet<DateOnly>() : PartialEdgeDays(pair.B.Lines);
        var trendA = Trend(windowA, dayOrdersA, partialA);
        var trendB = Trend(windowB, dayOrdersB, partialB);

        var summary = new List<string>();
        var peakHourA = Peak(hours, true);
        var peakHourB = Peak(hours, false);
        if (peakHourA is not null && peakHourB is not null)
            summary.Add($"En yoğun sipariş saati: {nameA} {peakHourA.Label} ({SalesTextTr.Pct(peakHourA.ShareA ?? 0)}), " +
                        $"{nameB} {peakHourB.Label} ({SalesTextTr.Pct(peakHourB.ShareB ?? 0)}).");
        if (weekdaysComparable)
        {
            var peakDayA = Peak(weekdays, true);
            var peakDayB = Peak(weekdays, false);
            if (peakDayA is not null && peakDayB is not null)
                summary.Add($"En yoğun gün: {nameA} {peakDayA.Label} ({SalesTextTr.Pct(peakDayA.ShareA ?? 0)}), " +
                            $"{nameB} {peakDayB.Label} ({SalesTextTr.Pct(peakDayB.ShareB ?? 0)}).");
        }
        var single = baskets[0];
        if (single.ShareA is { } sa && single.ShareB is { } sb)
            summary.Add($"Tek ürünlük siparişlerin payı: {nameA} {SalesTextTr.Pct(sa)}, {nameB} {SalesTextTr.Pct(sb)}.");
        if (trendA is not null || trendB is not null)
            summary.Add("Dönem içi eğilim (ikinci yarının günlük ortalama siparişi, ilk yarıya göre): " +
                        $"{nameA} {TrendText(trendA)}, {nameB} {TrendText(trendB)}.");

        return new CountryBehaviour(weekdaysComparable ? weekdays : [], weekdaysComparable, hours, baskets, statuses, daily, trendA, trendB, summary);
    }

    static string TrendText(double? trend) =>
        trend is { } t ? (Math.Abs(t) < 0.005 ? "yatay" : $"{SalesTextTr.Pct(t)} {(t > 0 ? "artış" : "düşüş")}") : "hesaplanamadı (en az 4 tam gün gerekir)";

    internal static ShareRow? Peak(IReadOnlyList<ShareRow> rows, bool sideA) =>
        rows.Where(r => (sideA ? r.A : r.B) > 0).OrderByDescending(r => sideA ? r.A : r.B).FirstOrDefault();

    static List<ShareRow> Shares(IEnumerable<(string Key, string Label)> keys, IEnumerable<string> a, IEnumerable<string> b)
    {
        var ca = a.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var cb = b.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var totalA = ca.Values.Sum();
        var totalB = cb.Values.Sum();
        return [.. keys.Select(k =>
        {
            var va = ca.GetValueOrDefault(k.Key);
            var vb = cb.GetValueOrDefault(k.Key);
            return new ShareRow(k.Key, k.Label, va, vb, SalesMetrics.Ratio(va, totalA), SalesMetrics.Ratio(vb, totalB));
        })];
    }

    /// <summary>
    /// Average daily orders of the window's second half against its first half. Partial edge days
    /// (an export cut mid-day) are left out — they would read as a collapse that never happened.
    /// </summary>
    internal static double? Trend(PeriodRange window, IReadOnlyDictionary<DateOnly, int> perDay, ISet<DateOnly> partial)
    {
        var days = Enumerable.Range(0, Days(window)).Select(i => window.From.AddDays(i)).Where(d => !partial.Contains(d)).ToList();
        if (days.Count < 4)
            return null;
        var half = days.Count / 2;
        var first = days.Take(half).Average(d => perDay.GetValueOrDefault(d));
        var second = days.Skip(days.Count - half).Average(d => perDay.GetValueOrDefault(d));
        return first > 0 ? (second - first) / first : null;
    }

    // -------------------------------------------------------------------------------------------
    // Notes and warnings
    // -------------------------------------------------------------------------------------------

    static void CurrencyNotes(CountryPair pair, string nameA, string nameB, string? curA, string? curB, bool same, double? rate,
        List<string> warnings, List<string> notComparable)
    {
        void Mixed(SalesDataset d, string name)
        {
            if (d.Report.Currencies.Count > 1)
                warnings.Add($"{name} dosyasında birden fazla para birimi var ({string.Join(", ", d.Report.Currencies)}); bu dosyanın tutarları tek para birimine çevrilmeden toplandı.");
            else if (d.Report.Currencies.Count == 0)
                warnings.Add($"{name} dosyasında para birimi bilgisi yok ('{SalesColumnMap.Currency}' kolonu boş ya da eksik).");
        }
        Mixed(pair.A, nameA);
        Mixed(pair.B, nameB);

        if (same)
            return;
        if (rate is { } r)
        {
            warnings.Add($"{nameB} tutarları girilen kurla {curA ?? "A'nın para birimi"} cinsine çevrildi: " +
                         $"1 {curB ?? "?"} = {r.ToString("0.####", SalesTextTr.Tr)} {curA ?? "?"}. Tutar farkları bu kura göre hesaplandı.");
        }
        else
        {
            notComparable.Add($"Tutarlar: para birimleri farklı ya da bilinmiyor ({nameA}: {curA ?? "?"}, {nameB}: {curB ?? "?"}). " +
                              "Kur girilmediği için tutar farkları hesaplanmadı; tutarlar kendi para biriminde yan yana gösteriliyor. " +
                              "Sipariş, adet, oran ve pay karşılaştırmaları para biriminden bağımsızdır.");
        }
    }

    static void DateNotes(string nameA, string nameB, PeriodRange fullA, PeriodRange fullB, PeriodRange windowA, PeriodRange windowB,
        bool overlapOnly, List<string> warnings)
    {
        if (overlapOnly)
        {
            if (fullA != windowA || fullB != windowB)
                warnings.Add($"Yalnızca iki dosyanın ortak günleri ({Tr(windowA.From)} – {Tr(windowA.To)}, {Days(windowA)} gün) karşılaştırıldı; dışında kalan günler hesaba katılmadı.");
            return;
        }
        if (windowA == windowB)
            return;

        var text = $"Dosyalar farklı tarihleri kapsıyor ({nameA}: {Tr(windowA.From)} – {Tr(windowA.To)}, {Days(windowA)} gün; " +
                   $"{nameB}: {Tr(windowB.From)} – {Tr(windowB.To)}, {Days(windowB)} gün).";
        text += Days(windowA) != Days(windowB)
            ? " Süreler farklı olduğu için toplamlar yerine günlük ortalamalar esas alındı."
            : " Tarihler örtüşmediği için dönemsel farklar (kampanya, tatil vb.) sonuçları etkileyebilir.";
        if (Overlap(fullA, fullB) is not null)
            text += " Yalnızca ortak günleri karşılaştırmak için tarih kapsamını 'Sadece ortak günler' yapın.";
        warnings.Add(text);
    }

    static void PartialDayNotes(IReadOnlyList<SalesLine> lines, PeriodRange window, string name, List<string> warnings)
    {
        foreach (var day in PartialEdgeDays(lines).Where(d => d >= window.From && d <= window.To).Order())
        {
            warnings.Add($"{name} dosyasında {Tr(day)} günü eksik görünüyor (diğer günlerin yarısından az satır var); " +
                         "dosya muhtemelen gün bitmeden alınmış, bu gün o ülkenin satışını olduğundan düşük gösterir.");
        }
    }

    /// <summary>
    /// The first/last day of an export when it holds under half the median line count of the other
    /// days — the same rule as <see cref="SalesAnalysisService.PartialDayWarnings"/>.
    /// </summary>
    internal static HashSet<DateOnly> PartialEdgeDays(IReadOnlyList<SalesLine> lines)
    {
        var result = new HashSet<DateOnly>();
        var perDay = lines.GroupBy(l => l.Day).ToDictionary(g => g.Key, g => g.Count());
        if (perDay.Count < 3)
            return result;

        foreach (var edge in new[] { perDay.Keys.Min(), perDay.Keys.Max() }.Distinct())
        {
            var others = perDay.Where(kv => kv.Key != edge).Select(kv => (double)kv.Value).Order().ToArray();
            if (perDay[edge] < OutlierFilter.Quantile(others, 0.5) * 0.5)
                result.Add(edge);
        }
        return result;
    }

    static List<string> StatusesOnlyIn(IEnumerable<SalesLine> side, IEnumerable<SalesLine> other)
    {
        var otherSet = other.Select(l => l.Status).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. side.Select(l => l.Status).Where(s => s.Length > 0 && !otherSet.Contains(s))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    // -------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------

    /// <summary>A line with every money field multiplied by <paramref name="rate"/>.</summary>
    internal static SalesLine Convert(SalesLine l, double rate) => l with
    {
        Amount = l.Amount * rate,
        UnitPrice = l.UnitPrice * rate,
        ShippingPrice = l.ShippingPrice * rate,
        OrderTotalExclTaxes = l.OrderTotalExclTaxes * rate,
        OrderTotalInclVat = l.OrderTotalInclVat * rate,
        Commission = l.Commission * rate,
        TransferredToSeller = l.TransferredToSeller * rate,
        CanceledAmount = l.CanceledAmount * rate,
    };

    internal static string? SingleCurrency(ImportReport report) =>
        report.Currencies.Count == 1 ? report.Currencies[0].Trim().ToUpperInvariant() : null;

    static bool HasColumn(SalesDataset d, string column) =>
        !d.Report.MissingOptionalColumns.Contains(column, StringComparer.OrdinalIgnoreCase);

    static string CleanName(string? requested, string fallback)
    {
        var name = (requested ?? "").Trim();
        if (name.Length == 0)
            return fallback;
        return name.Length > 40 ? name[..40].TrimEnd() : name;
    }

    static PeriodRange Range(IReadOnlyList<SalesLine> lines) => new(lines.Min(l => l.Day), lines.Max(l => l.Day));

    internal static PeriodRange? Overlap(PeriodRange a, PeriodRange b)
    {
        var from = a.From > b.From ? a.From : b.From;
        var to = a.To < b.To ? a.To : b.To;
        return from <= to ? new PeriodRange(from, to) : null;
    }

    static bool In(SalesLine l, PeriodRange r) => l.Day >= r.From && l.Day <= r.To;

    static int Days(PeriodRange r) => r.To.DayNumber - r.From.DayNumber + 1;

    static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static string Tr(DateOnly d) => d.ToString("dd.MM.yyyy", SalesTextTr.Tr);
}
