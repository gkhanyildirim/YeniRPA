using System.Globalization;
using System.Text.RegularExpressions;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Renders one seller's overdue orders into the message that gets posted in their WhatsApp group.
///
/// <para>Two templates rather than one, because a single template cannot express "repeat this once
/// per order" without inventing a loop syntax. The envelope is rendered once; the order-line template
/// is rendered once per overdue order and the block substituted in.</para>
///
/// <para>The defaults here are what ships and what "Reset to default" restores. The operator's edited
/// versions live in <c>seller-groups.json</c> beside the mappings — see
/// <see cref="SellerGroupStore"/>.</para>
/// </summary>
public static partial class LateOrderMessageBuilder
{
    /// <summary>
    /// The operator's own wording, which they already send by hand. Turkish because the recipients are
    /// Turkish sellers; the UI chrome around it stays English like the rest of the app.
    ///
    /// <para>The original was written for one order ("01259_… numaralı siparişiniz"). Since a message
    /// covers every overdue order the seller has, the number moved into the <c>{orders}</c> block and
    /// the sentence is built on <c>{orderCount}</c> instead — Turkish does not pluralise a noun after a
    /// numeral, so "1 siparişiniz" and "4 siparişiniz" are both correct and one phrasing serves any
    /// count. The closing paragraph is verbatim.</para>
    ///
    /// <para>No emoticon sequences (<c>:)</c> and friends): WhatsApp's composer converts them to emoji
    /// as they are typed, which would fail the runner's read-back verification.</para>
    /// </summary>
    public const string DefaultTemplate =
        """
        Selamlar,

        Aşağıdaki {orderCount} siparişiniz gecikmiş görünüyor, kontrol edebilir misiniz?

        {orders}{truncationNote}

        Eğer çıkışını yaptıysanız sistemin otomatik iptal etmemesi için kargo kodunu panele girmeniz gerekiyor. Giriş yapılmazsa sipariş iptale düşebilir. Desteğinizi rica ederiz.
        """;

    /// <summary>
    /// Just the order number: the operator's message deliberately does not quote deadlines or delay
    /// lengths. <c>{deadline}</c>, <c>{late}</c> and <c>{status}</c> stay available for anyone who
    /// wants the urgency signal back.
    /// </summary>
    public const string DefaultOrderLineTemplate = "• {orderNumber}";

    public static readonly string[] EnvelopePlaceholders =
    [
        "{seller}", "{sellerId}", "{orderCount}", "{maxDaysLate}",
        "{orders}", "{referenceTime}", "{truncationNote}",
    ];

    public static readonly string[] OrderLinePlaceholders =
    [
        "{orderNumber}", "{deadline}", "{deadlineRaw}",
        "{daysLate}", "{hoursLate}", "{late}", "{status}",
    ];

    /// <summary>Both sets, for the panel's placeholder reference table.</summary>
    public static readonly string[] KnownPlaceholders = [.. EnvelopePlaceholders, .. OrderLinePlaceholders];

    [GeneratedRegex(@"\{[A-Za-z][A-Za-z0-9]*\}")]
    private static partial Regex PlaceholderPattern();

    public static RenderedMessage Render(
        LateOrderSeller seller,
        string referenceTime,
        string? template,
        string? orderLineTemplate)
    {
        ArgumentNullException.ThrowIfNull(seller);

        var envelope = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        var lineTemplate = string.IsNullOrWhiteSpace(orderLineTemplate) ? DefaultOrderLineTemplate : orderLineTemplate;

        var shown = seller.Orders.Take(LateOrderBuilder.MaxOrderLinesPerMessage).ToList();
        var hidden = seller.Orders.Count - shown.Count;

        var orders = string.Join("\n", shown.Select(order => RenderOrderLine(lineTemplate, order)));

        // Appended directly under the list, so the blank line before the closing section is the
        // template's and does not disappear when nothing was truncated.
        var truncationNote = hidden > 0
            ? $"\n…ve {hidden:N0} sipariş daha — tam liste ekte."
            : "";

        var body = envelope
            .Replace("{seller}", seller.SellerName)
            .Replace("{sellerId}", seller.SellerId)
            .Replace("{orderCount}", seller.OrderCount.ToString("N0", CultureInfo.InvariantCulture))
            .Replace("{maxDaysLate}", seller.MaxDaysLate.ToString(CultureInfo.InvariantCulture))
            .Replace("{referenceTime}", referenceTime)
            .Replace("{truncationNote}", truncationNote)
            // Substituted LAST, after every other envelope placeholder. An order number containing a
            // literal "{seller}" would otherwise be re-substituted — the classic template-injection
            // foot-gun.
            .Replace("{orders}", orders);

        return new RenderedMessage(
            GroupName: seller.GroupName ?? "",
            SellerId: seller.SellerId,
            SellerName: seller.SellerName,
            // Normalised to \n here so the runner's mandatory split-before-typing cannot leave a stray
            // \r to press. A \r in a WhatsApp composer is not harmless.
            Body: body.Replace("\r\n", "\n").Replace("\r", "\n"),
            OrderCount: seller.OrderCount,
            Truncated: hidden > 0,
            UnknownPlaceholders: FindUnknown(envelope, lineTemplate));
    }

    static string RenderOrderLine(string template, LateOrderLine order) => template
        .Replace("{orderNumber}", order.OrderNumber)
        .Replace("{deadline}", order.DeadlineEffective)
        .Replace("{deadlineRaw}", order.DeadlineRaw)
        .Replace("{daysLate}", order.DaysLate.ToString(CultureInfo.InvariantCulture))
        .Replace("{hoursLate}", order.HoursLate.ToString("0.#", CultureInfo.InvariantCulture))
        .Replace("{late}", ComposeLate(order))
        .Replace("{status}", order.Status);

    /// <summary>
    /// "3 gün" once there is a whole day to report, "12 saat" below that. Flooring alone would print
    /// "0 gün" on an order 23 hours late, in a message headed as a lateness warning.
    /// </summary>
    static string ComposeLate(LateOrderLine order) => order.DaysLate >= 1
        ? $"{order.DaysLate} gün"
        : $"{Math.Max(0, (int)Math.Round(order.HoursLate))} saat";

    /// <summary>
    /// Placeholders the operator typed that we do not recognise. They are left in the output verbatim
    /// rather than thrown away: deleting them would ship "Merhaba ," to a seller, and throwing would
    /// let one typo block the whole preview. The panel points at them instead.
    /// </summary>
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
        Scan(lineTemplate, OrderLinePlaceholders);

        return unknown;
    }
}
