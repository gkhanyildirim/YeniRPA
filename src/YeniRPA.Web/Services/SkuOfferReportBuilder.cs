using System.Globalization;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// SKU Offer &amp; Traffic Report. Fixed-template exports, joined on the product id:
/// <list type="bullet">
/// <item><b>Offers export</b> (<c>Product SKU</c>, <c>Offer SKU</c>, <c>Activated</c>, <c>Creation date</c>…) —
/// one row per offer, so a product's offer history is the group of rows sharing a Product SKU.</item>
/// <item><b>Offer KPIs export</b> (<c>Product id</c>, <c>Sessions by Product with PDP (30d)</c>…) — also one row per
/// offer, but the product-level figures repeat on every row, so it is read once per product.</item>
/// </list>
///
/// <para>The first offer of a product is the one with the oldest <c>Creation date</c> (ties: lowest
/// Offer SKU); it counts as live when <c>Activated</c> is "yes". A product whose first offer is no longer
/// live gets the warning sentence the team asked for. A product missing from the traffic file has
/// <em>no</em> traffic figures — unknown, which is not the same as zero views.</para>
/// </summary>
public static class SkuOfferReportBuilder
{
    const string OffersSku = "Product SKU";
    const string OffersOfferSku = "Offer SKU";
    const string OffersActivated = "Activated";
    const string OffersCreated = "Creation date";
    const string KpiProductId = "Product id";
    const string KpiPdp = "Sessions by Product with PDP";
    const string ApprovalCode = "Ürün Kodu";
    const string ApprovalDate = "Tarih";
    const string ApprovalBy = "Sorumlu";
    const string ApprovalStatus = "Durum";

    static readonly string[] CreatedFormats =
        ["MM/dd/yyyy hh:mm:ss tt", "M/d/yyyy h:mm:ss tt", "MM/dd/yyyy HH:mm:ss"];

    sealed class Acc
    {
        public string Product = "", Brand = "", Category = "";
        public DateTime? First;
        public string FirstOfferSku = "", FirstSeller = "", FirstReason = "";
        public bool FirstActive;
        public int Total, Active;
        public List<string>? ActiveSellers;
    }

    /// <summary>One product's row from the combined sales file. <c>null</c> = the cell was blank (unknown), never 0.</summary>
    sealed record Traffic(
        double? Impr, double? Clicks, double? Pdp, double? AddToCart, double? Checkout, double? Orders,
        double? Remove, double? Sold, double? Gmv, double? AvgPrice, double? Aov,
        double?[]? PeriodQty, double?[]? PeriodGmv);

    sealed record SalesData(Dictionary<string, Traffic> Map, string[] Periods, string Window, bool HasImpressions);

    sealed record Approval(DateTime? Date, string By);

    /// <param name="today">The date product age is measured to; defaults to the server's today. A parameter so tests are deterministic.</param>
    public static SkuOfferReport Build(
        Stream offers, string offersName, Stream traffic, string trafficName,
        Stream approvals, string approvalsName, DateTime? today = null,
        Stream? stockout = null, string? stockoutName = null, double eurTryRate = 1)
    {
        var asOf = (today ?? DateTime.Today).Date;
        var unreadable = 0;
        var accs = ReadOffers(offers, offersName, ref unreadable);
        var sales = ReadSales(traffic, trafficName, eurTryRate);
        var kpis = sales.Map;
        var approved = ReadApprovals(approvals, approvalsName);

        var rows = new List<SkuOfferRow>(accs.Count);
        var matched = 0;
        var approvedMatched = 0;
        foreach (var (sku, a) in accs)
        {
            kpis.TryGetValue(sku, out var t);
            if (t is not null) matched++;

            approved.TryGetValue(sku, out var ap);
            if (ap is not null) approvedMatched++;

            string? warning = null, severity = null;
            if (a.First is { } first && !a.FirstActive)
            {
                warning = $"Bu ürün için ilk teklif {first:dd.MM.yyyy} tarihinde oluşturulmuş, " +
                          "ancak mevcut durumda teklif kapalıdır.";
                severity = a.Active == 0 ? "critical" : "warning";
            }

            rows.Add(new SkuOfferRow
            {
                Sku = sku, Product = a.Product, Brand = a.Brand, Category = a.Category,
                FirstOfferDate = a.First, FirstOfferSeller = a.FirstSeller,
                FirstOfferActive = a.FirstActive, FirstOfferReason = a.FirstReason,
                TotalOffers = a.Total, ActiveOffers = a.Active, ActiveSellers = a.ActiveSellers ?? [],
                HasTraffic = t is not null,
                Impressions = t?.Impr, Clicks = t?.Clicks, Ctr = Ratio(t?.Clicks, t?.Impr),
                PdpViews = t?.Pdp, AddToCart = t?.AddToCart, AtcRate = Ratio(t?.AddToCart, t?.Pdp),
                CheckoutSessions = t?.Checkout, OrderSessions = t?.Orders,
                CrPdp = Ratio(t?.Orders, t?.Pdp), CrCheckout = Ratio(t?.Orders, t?.Checkout),
                RemoveFromCart = t?.Remove,
                SoldQty = t?.Sold, Gmv = t?.Gmv, AvgPrice = t?.AvgPrice, Aov = t?.Aov,
                PeriodQty = t?.PeriodQty, PeriodGmv = t?.PeriodGmv,
                HasApproval = ap is not null, ApprovalDate = ap?.Date, ApprovedBy = ap?.By ?? "",
                AgeGroup = SkuAgeGroups.For(a.First, asOf),
                AgeDays = a.First is { } created ? Math.Max(0, (asOf - created.Date).Days) : null,
                Warning = warning, Severity = severity
            });
        }

        rows.Sort((x, y) => string.CompareOrdinal(x.Sku, y.Sku));

        var summary = new SkuOfferSummary(
            TotalSkus: rows.Count,
            TotalOffers: rows.Sum(r => r.TotalOffers),
            ActiveOffers: rows.Sum(r => r.ActiveOffers),
            FirstOfferClosedSkus: rows.Count(r => r.Warning is not null),
            NoActiveOfferSkus: rows.Count(r => r.ActiveOffers == 0),
            SkusWithTraffic: matched,
            SkusWithoutTraffic: rows.Count - matched,
            TrafficProductsNotInOffers: kpis.Count - matched,
            UnreadableDates: unreadable,
            TotalPdpViews: rows.Sum(r => r.PdpViews ?? 0),
            TotalAddToCart: rows.Sum(r => r.AddToCart ?? 0),
            HasImpressions: sales.HasImpressions,
            TotalImpressions: rows.Sum(r => r.Impressions ?? 0),
            ApprovedSkus: approvedMatched,
            NotApprovedSkus: rows.Count - approvedMatched,
            ApprovalProductsNotInOffers: approved.Count - approvedMatched,
            SalesWindow: sales.Window,
            UniqueSkusSold: kpis.Values.Count(k => k.Sold > 0),
            UniqueSkusSoldInOffers: rows.Count(r => r.SoldQty > 0),
            TotalSoldQty: kpis.Values.Sum(k => k.Sold ?? 0),
            TotalGmv: kpis.Values.Sum(k => k.Gmv ?? 0),
            OverallCtr: Ratio(kpis.Values.Sum(k => k.Clicks ?? 0), kpis.Values.Sum(k => k.Impr ?? 0)) ?? 0,
            OverallAtcRate: Ratio(kpis.Values.Sum(k => k.AddToCart ?? 0), kpis.Values.Sum(k => k.Pdp ?? 0)) ?? 0,
            OverallCrPdp: Ratio(kpis.Values.Sum(k => k.Orders ?? 0), kpis.Values.Sum(k => k.Pdp ?? 0)) ?? 0,
            LostTrafficSkus: rows.Count(r => r.ActiveOffers == 0 && r.PdpViews > 0),
            LostTrafficPdp: rows.Where(r => r.ActiveOffers == 0).Sum(r => r.PdpViews ?? 0),
            PdpButNeverSoldSkus: rows.Count(r => r.PdpViews > 0 && !(r.SoldQty > 0)),
            EurTryRate: eurTryRate);

        var stockoutReport = stockout is null
            ? null
            : BuildStockout(ReadStockout(stockout, stockoutName ?? "stockout"), rows, kpis);

        return new SkuOfferReport
        {
            Rows = rows, Summary = summary, Charts = BuildCharts(rows, sales), Stockout = stockoutReport
        };
    }

    sealed class StockAcc
    {
        public string Product = "", Brand = "", Category = "";
        public readonly List<string> Sellers = [];
        public bool? RetailBuyable;
        public double Sold, Gmv;
    }

    /// <summary>
    /// Products Unavailable export: one row per product, but a product repeated per seller is summed
    /// rather than dropped (the figures are last-7-day sales). Only <c>Product id</c> is required.
    /// </summary>
    static Dictionary<string, StockAcc> ReadStockout(Stream stream, string fileName)
    {
        var map = new Dictionary<string, StockAcc>(StringComparer.Ordinal);
        var cId = -1;
        int? cProduct = null, cBrand = null, cCategory = null, cSeller = null, cRetail = null, cGmv = null, cSold = null;
        var headerSeen = false;

        foreach (var row in SkuOfferReader.Read(stream, fileName))
        {
            if (!headerSeen)
            {
                headerSeen = true;
                var idx = TabularFile.BuildHeaderIndex(row);
                RequireColumns(idx, "stockout (Products Unavailable)", fileName, KpiProductId);
                cId = idx[KpiProductId];
                cProduct = Optional(idx, "Product Name"); cBrand = Optional(idx, "Brand");
                cCategory = Optional(idx, "Focus Category Name"); cSeller = Optional(idx, "Seller Name");
                cRetail = Optional(idx, "Retail buyable");
                cGmv = Optional(idx, "GMV with shipping accepted");
                cSold = Optional(idx, "Sold items accepted");
                continue;
            }

            var id = NormalizeId(TabularFile.GetCell(row, cId));
            if (id.Length == 0) continue;

            if (!map.TryGetValue(id, out var acc))
            {
                acc = new StockAcc
                {
                    Product = TabularFile.GetCell(row, cProduct).Trim(),
                    Brand = TabularFile.GetCell(row, cBrand).Trim(),
                    Category = TabularFile.GetCell(row, cCategory).Trim()
                };
                map[id] = acc;
            }

            var seller = TabularFile.GetCell(row, cSeller).Trim();
            if (seller.Length > 0 && !acc.Sellers.Contains(seller)) acc.Sellers.Add(seller);

            var retail = TabularFile.GetCell(row, cRetail).Trim();
            if (retail.Equals("true", StringComparison.OrdinalIgnoreCase)) acc.RetailBuyable = true;
            else if (retail.Equals("false", StringComparison.OrdinalIgnoreCase)) acc.RetailBuyable ??= false;

            acc.Gmv += Number(row, cGmv) ?? 0;
            acc.Sold += Number(row, cSold) ?? 0;
        }

        if (!headerSeen)
            throw new InvalidOperationException($"The stockout file '{fileName}' is empty.");

        return map;
    }

    static StockoutReport BuildStockout(
        Dictionary<string, StockAcc> stock, List<SkuOfferRow> offerRows, Dictionary<string, Traffic> kpis)
    {
        var bySku = offerRows.ToDictionary(r => r.Sku, StringComparer.Ordinal);
        var rows = new List<StockoutRow>(stock.Count);

        foreach (var (id, s) in stock)
        {
            kpis.TryGetValue(id, out var t);
            bySku.TryGetValue(id, out var offer);

            rows.Add(new StockoutRow
            {
                ProductId = id,
                Product = s.Product.Length > 0 ? s.Product : offer?.Product ?? "",
                Brand = s.Brand.Length > 0 ? s.Brand : offer?.Brand ?? "",
                Category = s.Category.Length > 0 ? s.Category : offer?.Category ?? "",
                Sellers = string.Join(", ", s.Sellers),
                RetailBuyable = s.RetailBuyable,
                Sold7d = s.Sold, Gmv7d = s.Gmv,
                HasTraffic = t is not null,
                PdpViews = t?.Pdp, AddToCart = t?.AddToCart,
                AtcRate = Ratio(t?.AddToCart, t?.Pdp),
                Impressions = t?.Impr, CrPdp = Ratio(t?.Orders, t?.Pdp),
                SoldQty = t?.Sold, Gmv = t?.Gmv,
                InOffers = offer is not null,
                TotalOffers = offer?.TotalOffers ?? 0, ActiveOffers = offer?.ActiveOffers ?? 0,
                FirstOfferDate = offer?.FirstOfferDate,
                FirstOfferClosed = offer?.Warning is not null,
                AgeGroup = offer?.AgeGroup,
                ApprovalDate = offer?.ApprovalDate, ApprovedBy = offer?.ApprovedBy ?? ""
            });
        }

        rows.Sort((x, y) => (y.PdpViews ?? -1).CompareTo(x.PdpViews ?? -1));

        var allPdp = kpis.Values.Sum(k => k.Pdp ?? 0);
        var stockPdp = rows.Sum(r => r.PdpViews ?? 0);
        var withTraffic = rows.Count(r => r.HasTraffic);

        var summary = new StockoutSummary(
            Products: rows.Count,
            WithTraffic: withTraffic,
            WithoutTraffic: rows.Count - withTraffic,
            NotInOffers: rows.Count(r => !r.InOffers),
            TotalImpressions: rows.Sum(r => r.Impressions ?? 0),
            TotalPdpViews: stockPdp,
            SharePctOfAllPdp: allPdp > 0 ? stockPdp / allPdp * 100 : 0,
            TotalAddToCart: rows.Sum(r => r.AddToCart ?? 0),
            Gmv7d: rows.Sum(r => r.Gmv7d),
            Sold7d: rows.Sum(r => r.Sold7d));

        var top = rows.Where(r => r.PdpViews > 0).Take(10)
            .Select(r => new StockoutTopPoint(r.ProductId, r.Product, r.PdpViews!.Value))
            .ToList();

        return new StockoutReport { Rows = rows, Summary = summary, TopPdp = top };
    }

    static Dictionary<string, Acc> ReadOffers(Stream stream, string fileName, ref int unreadable)
    {
        var accs = new Dictionary<string, Acc>(StringComparer.Ordinal);
        int cSku = -1, cOffer = -1, cActive = -1, cCreated = -1;
        int? cSeller = null, cProduct = null, cBrand = null, cCategory = null, cReason = null;
        var headerSeen = false;

        foreach (var row in SkuOfferReader.Read(stream, fileName))
        {
            if (!headerSeen)
            {
                headerSeen = true;
                var idx = TabularFile.BuildHeaderIndex(row);
                RequireColumns(idx, "offers", fileName, OffersSku, OffersOfferSku, OffersActivated, OffersCreated);
                cSku = idx[OffersSku]; cOffer = idx[OffersOfferSku];
                cActive = idx[OffersActivated]; cCreated = idx[OffersCreated];
                cSeller = Optional(idx, "Seller"); cProduct = Optional(idx, "Product");
                cBrand = Optional(idx, "Brand"); cCategory = Optional(idx, "Category Label");
                cReason = Optional(idx, "Inactivity reason");
                continue;
            }

            var sku = NormalizeId(TabularFile.GetCell(row, cSku));
            if (sku.Length == 0) continue;

            if (!accs.TryGetValue(sku, out var a))
            {
                a = new Acc
                {
                    Product = TabularFile.GetCell(row, cProduct).Trim(),
                    Brand = TabularFile.GetCell(row, cBrand).Trim(),
                    Category = TabularFile.GetCell(row, cCategory).Trim()
                };
                accs[sku] = a;
            }

            var active = IsYes(TabularFile.GetCell(row, cActive));
            var seller = TabularFile.GetCell(row, cSeller).Trim();
            a.Total++;
            if (active)
            {
                a.Active++;
                if (seller.Length > 0)
                {
                    a.ActiveSellers ??= [];
                    if (!a.ActiveSellers.Contains(seller)) a.ActiveSellers.Add(seller);
                }
            }

            var created = ParseCreated(TabularFile.GetCell(row, cCreated));
            if (created is null)
            {
                unreadable++;
                continue;
            }

            var offerSku = TabularFile.GetCell(row, cOffer).Trim();
            var older = a.First is null || created < a.First ||
                        (created == a.First && string.CompareOrdinal(offerSku, a.FirstOfferSku) < 0);
            if (!older) continue;

            a.First = created;
            a.FirstOfferSku = offerSku;
            a.FirstActive = active;
            a.FirstSeller = seller;
            a.FirstReason = TabularFile.GetCell(row, cReason).Trim();
        }

        if (!headerSeen)
            throw new InvalidOperationException($"The offers file '{fileName}' is empty.");

        return accs;
    }

    /// <summary>
    /// The combined sales file: one row per product, every figure summed over the file's whole period
    /// (the real file spans 1 Sep to 14 Oct, not 30 days). Headers are matched by their stable prefix
    /// because the export decorates them ("GMV w/o Shipping  [Tracked]", "... - Summary"). The period
    /// breakdown is discovered from the <c>GMV | label</c> / <c>Adet | label</c> column pairs, so a template
    /// with different periods still reads. Only <c>Product id</c> and the PDP sessions column are required.
    /// </summary>
    static SalesData ReadSales(Stream stream, string fileName, double rate)
    {
        var map = new Dictionary<string, Traffic>(StringComparer.Ordinal);
        int cId = -1, cPdp = -1;
        int? cImpr = null, cClicks = null, cAtc = null, cCheckout = null, cOrders = null, cRemove = null;
        int? cSold = null, cGmv = null, cPrice = null, cAov = null;
        var periods = new List<(string Label, int? Gmv, int? Qty)>();
        var headerSeen = false;

        foreach (var row in SkuOfferReader.Read(stream, fileName))
        {
            if (!headerSeen)
            {
                headerSeen = true;
                var idx = TabularFile.BuildHeaderIndex(row);
                RequireColumns(idx, "combined sales", fileName, KpiProductId, KpiPdp);
                cId = idx[KpiProductId]; cPdp = idx[KpiPdp];

                cImpr = FindByPrefix(idx, "Sessions by Product with Product Impr");
                cClicks = FindByPrefix(idx, "Sessions by Product with Product Clicks");
                cAtc = FindByPrefix(idx, "Sessions by Product with Add to Cart");
                cCheckout = FindByPrefix(idx, "Sessions by Product in Checkout");
                cOrders = FindByPrefix(idx, "Sessions by Product with Orders");
                cRemove = FindByPrefix(idx, "Sessions by Product with Remove from Cart");
                cSold = FindByPrefix(idx, "Sold Qty");
                cGmv = FindByPrefix(idx, "GMV w/o Shipping");
                cPrice = FindByPrefix(idx, "Ø Product Price");
                cAov = Optional(idx, "AOV");

                foreach (var (name, col) in idx.OrderBy(p => p.Value))
                {
                    if (!name.StartsWith("GMV | ", StringComparison.OrdinalIgnoreCase)) continue;
                    var label = name["GMV | ".Length..].Trim();
                    periods.Add((label, col, idx.TryGetValue("Adet | " + label, out var q) ? q : null));
                }
                continue;
            }

            var id = NormalizeId(TabularFile.GetCell(row, cId));
            if (id.Length == 0 || map.ContainsKey(id)) continue;

            double?[]? pq = null, pg = null;
            if (periods.Count > 0)
            {
                pq = periods.Select(p => Number(row, p.Qty)).ToArray();
                pg = periods.Select(p => Money(row, p.Gmv, rate)).ToArray();
                if (pq.All(v => v is null) && pg.All(v => v is null)) pq = pg = null;
            }

            map[id] = new Traffic(
                Number(row, cImpr), Number(row, cClicks), Number(row, cPdp), Number(row, cAtc),
                Number(row, cCheckout), Number(row, cOrders), Number(row, cRemove),
                Number(row, cSold), Money(row, cGmv, rate), Money(row, cPrice, rate), Money(row, cAov, rate), pq, pg);
        }

        if (!headerSeen)
            throw new InvalidOperationException($"The combined sales file '{fileName}' is empty.");

        var labels = periods.Select(p => p.Label).ToArray();
        return new SalesData(map, labels, WindowOf(labels), cImpr is not null);
    }

    /// <summary>"1 Eyl - 30 Kas" ... "31 Agu - 14 Eki" -> "1 Eyl - 14 Eki": start of the first period to end of the last.</summary>
    static string WindowOf(string[] labels)
    {
        if (labels.Length == 0) return "";
        var first = labels[0].Split(" - ", 2, StringSplitOptions.TrimEntries);
        var last = labels[^1].Split(" - ", 2, StringSplitOptions.TrimEntries);
        return first.Length == 2 && last.Length == 2 ? $"{first[0]} - {last[1]}" : string.Join(", ", labels);
    }

    static int? FindByPrefix(Dictionary<string, int> idx, string prefix) =>
        idx.Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
           .OrderBy(p => p.Value).Select(p => (int?)p.Value).FirstOrDefault();

    /// <summary>
    /// A money cell of the combined sales file. The export is in euro; converting here, once, means every
    /// figure built from it (rows, totals, period charts, stockout join, Excel) is already in TRY and no
    /// screen can mix the two. Counts and quantities never go through this.
    /// </summary>
    static double? Money(List<string> row, int? col, double rate) => Number(row, col) * rate;

    static double? Ratio(double? numerator, double? denominator) =>
        numerator is { } n && denominator is > 0 ? n / denominator : null;
    /// <summary>
    /// Product code -> earliest approval (date, approver) across every sheet that carries the three
    /// columns. The workbook keeps one tab per year, so a product can show up on more than one; the
    /// earliest approval is the one that made it live. A tab without the columns is skipped, but a
    /// workbook where no tab has them is an error that names what is missing.
    /// </summary>
    static Dictionary<string, Approval> ReadApprovals(Stream stream, string fileName)
    {
        var map = new Dictionary<string, Approval>(StringComparer.Ordinal);
        var sawSheet = false;
        var usable = false;
        string[]? missingOnFirst = null;

        foreach (var sheet in SkuOfferReader.ReadSheets(stream, fileName))
        {
            int cCode = -1, cDate = -1, cBy = -1;
            int? cStatus = null;
            var headerSeen = false;
            var skip = false;

            foreach (var row in sheet)
            {
                if (!headerSeen)
                {
                    headerSeen = true;
                    sawSheet = true;
                    var idx = TabularFile.BuildHeaderIndex(row);
                    var missing = new[] { ApprovalCode, ApprovalDate, ApprovalBy }.Where(c => !idx.ContainsKey(c)).ToArray();
                    if (missing.Length > 0)
                    {
                        missingOnFirst ??= missing;
                        skip = true;
                        break; // not an approvals tab (a notes or pivot sheet, say)
                    }

                    usable = true;
                    cCode = idx[ApprovalCode]; cDate = idx[ApprovalDate]; cBy = idx[ApprovalBy];
                    cStatus = Optional(idx, ApprovalStatus);
                    continue;
                }

                if (!IsApprovedStatus(TabularFile.GetCell(row, cStatus)))
                    continue;

                var code = NormalizeId(TabularFile.GetCell(row, cCode));
                if (code.Length == 0) continue;

                var date = ParseApprovalDate(TabularFile.GetCell(row, cDate));
                var by = NormalizePerson(TabularFile.GetCell(row, cBy));

                if (!map.TryGetValue(code, out var existing) ||
                    (date is not null && (existing.Date is null || date < existing.Date)))
                {
                    map[code] = new Approval(date, by);
                }
            }

            if (skip) continue;
        }

        if (!sawSheet)
            throw new InvalidOperationException($"The approvals file '{fileName}' is empty.");
        if (!usable)
        {
            throw new InvalidOperationException(
                $"The approvals file '{fileName}' has no " +
                string.Join(", ", (missingOnFirst ?? []).Select(m => $"'{m}'")) +
                " column. Check the files were uploaded in the right boxes.");
        }

        return map;
    }

    /// <summary>
    /// Whether an approvals row counts. The file is a list of approved products, so a blank status is
    /// taken as approved; a status that is filled in must be one of the approved spellings.
    /// </summary>
    static bool IsApprovedStatus(string status)
    {
        var s = status.Trim();
        return s.Length == 0 ||
               s.Equals("ACCEPTED", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("APPROVED", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("ONAYLI", StringComparison.OrdinalIgnoreCase);
    }

    static readonly CultureInfo Turkish = new("tr-TR");

    /// <summary>
    /// The approver column is typed by hand: the real file spells the same person "zeynep" and "Zeynep",
    /// "hamdi", "Hamdi" and "Hamdi ", "ALİ" and "Ali". Trimmed and title-cased in Turkish rules (so "ALİ"
    /// becomes "Ali", not "Ali" with a dotted-I mismatch) they collapse to one name for filtering and search.
    /// </summary>
    static string NormalizePerson(string raw)
    {
        var name = raw.Trim();
        return name.Length == 0 ? "" : Turkish.TextInfo.ToTitleCase(name.ToLower(Turkish));
    }

    /// <summary>The approvals tabs store dates as Excel serial numbers ("46055"); a text date is read day-first.</summary>
    static DateTime? ParseApprovalDate(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) &&
            serial is > 20000 and < 80000)
            return DateTime.FromOADate(serial);

        return TabularFile.ParseDayFirstDate(text);
    }

    static SkuOfferCharts BuildCharts(List<SkuOfferRow> rows, SalesData sales)
    {
        var periods = sales.Periods
            .Select((label, i) => new SkuPeriodPoint(
                label,
                sales.Map.Values.Count(t => t.PeriodQty is { } q && q[i] > 0),
                sales.Map.Values.Sum(t => t.PeriodQty?[i] ?? 0),
                sales.Map.Values.Sum(t => t.PeriodGmv?[i] ?? 0)))
            .ToList();

        var all = sales.Map.Values;
        var funnel = new SkuFunnel(
            all.Sum(t => t.Impr ?? 0), all.Sum(t => t.Clicks ?? 0), all.Sum(t => t.Pdp ?? 0),
            all.Sum(t => t.AddToCart ?? 0), all.Sum(t => t.Checkout ?? 0), all.Sum(t => t.Orders ?? 0));

        var byAge = SkuAgeGroups.All
            .Select(g => new SkuAgePoint(
                g.Key, g.Label,
                rows.Count(r => r.AgeGroup == g.Key),
                rows.Count(r => r.AgeGroup == g.Key && r.Warning is not null)))
            .ToList();

        var byMonth = rows
            .Where(r => r.FirstOfferDate is not null)
            .GroupBy(r => r.FirstOfferDate!.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SkuMonthPoint(g.Key, g.Count(), g.Count(r => r.Warning is not null)))
            .ToList();

        var byCategory = rows
            .Where(r => r.Category.Length > 0 && r.FirstOfferDate is not null)
            .GroupBy(r => r.Category)
            .Select(g => new SkuCategoryPoint(
                g.Key, g.Count(r => r.Warning is null), g.Count(r => r.Warning is not null)))
            .OrderByDescending(p => p.Closed).ThenByDescending(p => p.Active)
            .Take(10)
            .ToList();

        var topTraffic = rows
            .Where(r => r.PdpViews > 0)
            .OrderByDescending(r => r.PdpViews)
            .Take(10)
            .Select(r => new SkuTopTrafficPoint(r.Sku, r.Product, r.PdpViews!.Value, r.Warning is not null))
            .ToList();

        return new SkuOfferCharts(byMonth, byCategory, topTraffic, byAge, periods, funnel);
    }

    public static IReadOnlyList<SkuOfferRow> Query(IReadOnlyList<SkuOfferRow> rows, SkuOfferQuery query)
    {
        IEnumerable<SkuOfferRow> result = rows;

        result = (query.Filter ?? "all") switch
        {
            "firstClosed" => result.Where(r => r.Warning is not null),
            "noActive" => result.Where(r => r.ActiveOffers == 0),
            "noTraffic" => result.Where(r => !r.HasTraffic),
            "hasTraffic" => result.Where(r => r.HasTraffic),
            "noApproval" => result.Where(r => !r.HasApproval),
            "sold" => result.Where(r => r.SoldQty > 0),
            "neverSold" => result.Where(r => r.PdpViews > 0 && !(r.SoldQty > 0)),
            "lostTraffic" => result.Where(r => r.ActiveOffers == 0 && r.PdpViews > 0),
            _ => result
        };

        var age = (query.AgeGroup ?? "").Trim();
        if (age.Length > 0 && !age.Equals("all", StringComparison.OrdinalIgnoreCase))
            result = result.Where(r => string.Equals(r.AgeGroup, age, StringComparison.OrdinalIgnoreCase));

        var search = (query.Search ?? "").Trim();
        if (search.Length > 0)
        {
            result = result.Where(r =>
                r.Sku.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.Product.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.Brand.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.ActiveSellers.Any(s => s.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                r.FirstOfferSeller.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.ApprovedBy.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var d = query.Descending;
        result = (query.Sort ?? "") switch
        {
            "product" => Order(result, r => r.Product, d),
            "firstDate" => Order(result, r => r.FirstOfferDate ?? DateTime.MaxValue, d),
            "totalOffers" => Order(result, r => r.TotalOffers, d),
            "activeOffers" => Order(result, r => r.ActiveOffers, d),
            "pdp" => Order(result, r => r.PdpViews ?? -1, d),
            "atc" => Order(result, r => r.AddToCart ?? -1, d),
            "atcRate" => Order(result, r => r.AtcRate ?? -1, d),
            "impressions" => Order(result, r => r.Impressions ?? -1, d),
            "ctr" => Order(result, r => r.Ctr ?? -1, d),
            "crPdp" => Order(result, r => r.CrPdp ?? -1, d),
            "sold" => Order(result, r => r.SoldQty ?? -1, d),
            "gmv" => Order(result, r => r.Gmv ?? -1, d),
            "aov" => Order(result, r => r.Aov ?? -1, d),
            "approvalDate" => Order(result, r => r.ApprovalDate ?? DateTime.MaxValue, d),
            "approvedBy" => Order(result, r => r.ApprovedBy, d),
            "age" => Order(result, r => r.AgeDays ?? -1, d),
            "sku" => Order(result, r => r.Sku, d),
            _ => result
        };

        return result.ToList();
    }

    static IEnumerable<SkuOfferRow> Order<TKey>(IEnumerable<SkuOfferRow> rows, Func<SkuOfferRow, TKey> key, bool descending)
    {
        var comparer = typeof(TKey) == typeof(string) ? (IComparer<TKey>)(object)StringComparer.OrdinalIgnoreCase : null;
        return descending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);
    }

    static void RequireColumns(Dictionary<string, int> idx, string which, string fileName, params string[] required)
    {
        var missing = required.Where(c => !idx.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"The {which} file '{fileName}' has no " +
                string.Join(", ", missing.Select(m => $"'{m}'")) +
                " column. Check the files were uploaded in the right boxes.");
        }
    }

    static int? Optional(Dictionary<string, int> idx, string name) => idx.TryGetValue(name, out var i) ? i : null;

    static double? Number(List<string> row, int? col)
    {
        if (col is null) return null;
        var text = TabularFile.GetCell(row, col).Trim();
        return text.Length == 0 ? null : TabularFile.ParseNumber(text);
    }

    static bool IsYes(string value)
    {
        var v = value.Trim();
        return v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    /// <summary>Ids come out of Excel as floats ("169847081.0"); both files must reduce to the same key.</summary>
    static string NormalizeId(string raw) => TabularFile.NormalizeSellerId(raw);

    static DateTime? ParseCreated(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (DateTime.TryParseExact(text, CreatedFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact;
        return TabularFile.ParseDate(text);
    }
}
