using System.Globalization;
using System.Text.RegularExpressions;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Renders one seller's stockout products into the message that gets posted in their WhatsApp
/// group. Same two-template split as <see cref="LateOrderMessageBuilder"/>: the envelope is
/// rendered once, the product-line template once per eligible product.
///
/// <para>The operator can edit both templates, but every product line must keep
/// <see cref="RequiredProductLinePlaceholders"/> — GTIN and sold-items-accepted are the two pieces
/// of information this module exists to deliver, so a template missing either is not silently
/// accepted; <see cref="Render"/> reports it and the panel surfaces the warning.</para>
/// </summary>
public static partial class StockoutWarningMessageBuilder
{
    /// <summary>Turkish because the recipients are Turkish sellers; the UI chrome around it stays
    /// English like the rest of the app. No emoticon sequences — WhatsApp's composer converts them
    /// to emoji as typed, which would fail the runner's read-back verification.</summary>
    public const string DefaultTemplate =
        """
        Selamlar,

        Aşağıdaki {productCount} ürününüz stokta görünmüyor ve satış potansiyeli yüksek (toplam GMV: {totalGmv}):

        {products}{truncationNote}

        Stok durumunuzu en kısa sürede güncellemenizi rica ederiz. Desteğinizi bekleriz.
        """;

    public const string DefaultProductLineTemplate = "• {productName} — GTIN: {gtin} — Satılan adet: {soldItemsAccepted}";

    public static readonly string[] EnvelopePlaceholders =
    [
        "{seller}", "{productCount}", "{totalGmv}", "{products}", "{referenceTime}", "{truncationNote}",
    ];

    public static readonly string[] ProductLinePlaceholders =
    [
        "{gtin}", "{productName}", "{brand}", "{category}", "{offerCondition}", "{gmv}", "{soldItemsAccepted}",
    ];

    /// <summary>Both sets, for the panel's placeholder reference table.</summary>
    public static readonly string[] KnownPlaceholders = [.. EnvelopePlaceholders, .. ProductLinePlaceholders];

    /// <summary>The two placeholders every product line must keep — see the class summary.</summary>
    public static readonly string[] RequiredProductLinePlaceholders = ["{gtin}", "{soldItemsAccepted}"];

    const string AccountHeadingSuffix = ":";
    const string NameSeparator = " / ";

    [GeneratedRegex(@"\{[A-Za-z][A-Za-z0-9]*\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>One seller, one group — the ordinary case.</summary>
    public static RenderedMessage Render(
        StockoutWarningSeller seller,
        string referenceTime,
        string? template,
        string? productLineTemplate)
    {
        ArgumentNullException.ThrowIfNull(seller);
        return Render([seller], referenceTime, template, productLineTemplate);
    }

    /// <summary>
    /// Every seller name that resolved to one WhatsApp group, rendered as the single message that
    /// group receives. Merging happens here, not at send time, so the preview cards, the Excel
    /// export and the typed keystrokes are the same bytes — same reasoning as
    /// <see cref="LateOrderMessageBuilder"/>'s multi-seller <c>Render</c> overload.
    /// </summary>
    public static RenderedMessage Render(
        IReadOnlyList<StockoutWarningSeller> sellers,
        string referenceTime,
        string? template,
        string? productLineTemplate)
    {
        ArgumentNullException.ThrowIfNull(sellers);
        if (sellers.Count == 0)
            throw new ArgumentException("A message needs at least one seller.", nameof(sellers));

        var envelope = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        var lineTemplate = string.IsNullOrWhiteSpace(productLineTemplate) ? DefaultProductLineTemplate : productLineTemplate;

        var merged = sellers.Count > 1;
        var totalProducts = sellers.Sum(s => s.Products.Count);

        var remaining = StockoutWarningBuilder.MaxProductLinesPerMessage;
        var sections = new List<string>(sellers.Count);
        var shownCount = 0;

        foreach (var seller in sellers)
        {
            if (remaining <= 0) break;

            var shown = seller.Products.Take(remaining).ToList();
            if (shown.Count == 0) continue;

            remaining -= shown.Count;
            shownCount += shown.Count;

            var lines = string.Join("\n", shown.Select(product => RenderProductLine(lineTemplate, product)));
            sections.Add(merged ? $"{seller.SellerName}{AccountHeadingSuffix}\n{lines}" : lines);
        }

        var products = string.Join("\n\n", sections);
        var hidden = totalProducts - shownCount;

        var truncationNote = hidden > 0
            ? $"\n…ve {hidden:N0} ürün daha — tam liste ekte."
            : "";

        var names = sellers
            .Select(s => s.SellerName)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var sellerName = string.Join(NameSeparator, names);
        var productCount = sellers.Sum(s => s.ProductCount);
        var totalGmv = sellers.Sum(s => s.TotalGmv);

        var body = envelope
            .Replace("{seller}", sellerName)
            .Replace("{productCount}", productCount.ToString("N0", CultureInfo.InvariantCulture))
            .Replace("{totalGmv}", totalGmv.ToString("N0", CultureInfo.InvariantCulture))
            .Replace("{referenceTime}", referenceTime)
            .Replace("{truncationNote}", truncationNote)
            // Substituted LAST, after every other envelope placeholder — a product name containing a
            // literal "{seller}" would otherwise be re-substituted.
            .Replace("{products}", products);

        return new RenderedMessage(
            GroupName: (sellers[0].GroupName ?? "").Trim(),
            SellerId: "",
            SellerName: sellerName,
            Body: body.Replace("\r\n", "\n").Replace("\r", "\n"),
            OrderCount: productCount,
            Truncated: hidden > 0,
            UnknownPlaceholders: FindUnknown(envelope, lineTemplate),
            AccountCount: sellers.Count);
    }

    static string RenderProductLine(string template, StockoutProductLine product) => template
        .Replace("{gtin}", product.Gtin)
        .Replace("{productName}", product.ProductName ?? "")
        .Replace("{brand}", product.Brand ?? "")
        .Replace("{category}", product.Category ?? "")
        .Replace("{offerCondition}", product.OfferCondition ?? "")
        .Replace("{gmv}", product.Gmv.ToString("N0", CultureInfo.InvariantCulture))
        .Replace("{soldItemsAccepted}", product.SoldItemsAccepted);

    /// <summary>Which of <see cref="RequiredProductLinePlaceholders"/> are missing from the product
    /// line template — sent as a warning, never a block, so one typo cannot stop the whole preview.</summary>
    public static IReadOnlyList<string> FindMissingRequired(string? productLineTemplate)
    {
        var lineTemplate = string.IsNullOrWhiteSpace(productLineTemplate) ? DefaultProductLineTemplate : productLineTemplate;
        return [.. RequiredProductLinePlaceholders.Where(p => !lineTemplate.Contains(p, StringComparison.Ordinal))];
    }

    /// <summary>Placeholders the operator typed that we do not recognise. Left in the output verbatim
    /// rather than thrown away — same reasoning as <see cref="LateOrderMessageBuilder"/>'s equivalent
    /// helper.</summary>
    static IReadOnlyList<string> FindUnknown(string envelope, string lineTemplate)
    {
        var unknown = new List<string>();

        void Scan(string text, string[] known)
        {
            foreach (Match match in PlaceholderPattern().Matches(text))
            {
                if (!known.Contains(match.Value, StringComparer.Ordinal) && !unknown.Contains(match.Value, StringComparer.Ordinal))
                    unknown.Add(match.Value);
            }
        }

        Scan(envelope, EnvelopePlaceholders);
        Scan(lineTemplate, ProductLinePlaceholders);

        return unknown;
    }
}
