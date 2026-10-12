using System.Globalization;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// SKU Offer &amp; Traffic Report. Two fixed-template exports, joined on the product id:
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
    const string KpiPdp = "Sessions by Product with PDP (30d)";

    static readonly string[] CreatedFormats =
        ["MM/dd/yyyy hh:mm:ss tt", "M/d/yyyy h:mm:ss tt", "MM/dd/yyyy HH:mm:ss"];

    sealed class Acc
    {
        public string Product = "", Brand = "", Category = "";
        public DateTime? First;
        public string FirstOfferSku = "", FirstSeller = "", FirstReason = "";
        public bool FirstActive;
        public int Total, Active;
    }

    sealed record Traffic(double? Pdp, double? AddToCart, double? Impressions, double? Sold, double? Gmv);

    public static SkuOfferReport Build(Stream offers, string offersName, Stream traffic, string trafficName)
    {
        var unreadable = 0;
        var accs = ReadOffers(offers, offersName, ref unreadable);
        var kpis = ReadTraffic(traffic, trafficName, out var hasImpressions);

        var rows = new List<SkuOfferRow>(accs.Count);
        var matched = 0;
        foreach (var (sku, a) in accs)
        {
            kpis.TryGetValue(sku, out var t);
            if (t is not null) matched++;

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
                TotalOffers = a.Total, ActiveOffers = a.Active,
                HasTraffic = t is not null,
                PdpViews = t?.Pdp, AddToCart = t?.AddToCart, Impressions = t?.Impressions,
                AtcRate = t is { Pdp: > 0, AddToCart: { } atc } ? atc / t.Pdp : null,
                Sold30d = t?.Sold, Gmv30d = t?.Gmv,
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
            HasImpressions: hasImpressions,
            TotalImpressions: rows.Sum(r => r.Impressions ?? 0));

        return new SkuOfferReport { Rows = rows, Summary = summary, Charts = BuildCharts(rows) };
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
            a.Total++;
            if (active) a.Active++;

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
            a.FirstSeller = TabularFile.GetCell(row, cSeller).Trim();
            a.FirstReason = TabularFile.GetCell(row, cReason).Trim();
        }

        if (!headerSeen)
            throw new InvalidOperationException($"The offers file '{fileName}' is empty.");

        return accs;
    }

    static Dictionary<string, Traffic> ReadTraffic(Stream stream, string fileName, out bool hasImpressions)
    {
        var map = new Dictionary<string, Traffic>(StringComparer.Ordinal);
        int cId = -1, cPdp = -1;
        int? cAtc = null, cSold = null, cGmv = null, cImpr = null;
        var headerSeen = false;

        foreach (var row in SkuOfferReader.Read(stream, fileName))
        {
            if (!headerSeen)
            {
                headerSeen = true;
                var idx = TabularFile.BuildHeaderIndex(row);
                RequireColumns(idx, "traffic (Offer KPIs)", fileName, KpiProductId, KpiPdp);
                cId = idx[KpiProductId]; cPdp = idx[KpiPdp];
                cAtc = idx.Where(p => p.Key.StartsWith("Sessions by Product with Add to Cart", StringComparison.OrdinalIgnoreCase))
                          .Select(p => (int?)p.Value).FirstOrDefault();
                cSold = idx.Where(p => p.Key.StartsWith("Product - Sold items", StringComparison.OrdinalIgnoreCase))
                           .Select(p => (int?)p.Value).FirstOrDefault();
                cGmv = idx.Where(p => p.Key.StartsWith("Product - GMV", StringComparison.OrdinalIgnoreCase))
                          .Select(p => (int?)p.Value).FirstOrDefault();
                // The current export carries no impression column; one added later is picked up by name.
                cImpr = idx.Where(p => p.Key.Contains("impression", StringComparison.OrdinalIgnoreCase))
                           .Select(p => (int?)p.Value).FirstOrDefault();
                continue;
            }

            var id = NormalizeId(TabularFile.GetCell(row, cId));
            if (id.Length == 0 || map.ContainsKey(id)) continue; // product-level figures repeat per offer row

            map[id] = new Traffic(
                Number(row, cPdp), Number(row, cAtc), Number(row, cImpr), Number(row, cSold), Number(row, cGmv));
        }

        if (!headerSeen)
            throw new InvalidOperationException($"The traffic file '{fileName}' is empty.");

        hasImpressions = cImpr is not null;
        return map;
    }

    static SkuOfferCharts BuildCharts(List<SkuOfferRow> rows)
    {
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

        return new SkuOfferCharts(byMonth, byCategory, topTraffic);
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
            _ => result
        };

        var search = (query.Search ?? "").Trim();
        if (search.Length > 0)
        {
            result = result.Where(r =>
                r.Sku.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.Product.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.Brand.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.Category.Contains(search, StringComparison.OrdinalIgnoreCase));
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
