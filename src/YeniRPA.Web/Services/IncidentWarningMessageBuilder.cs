using System.Globalization;
using System.Text.RegularExpressions;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Renders one seller's unanswered incidents into the message that gets posted in their WhatsApp group.
///
/// <para>The sibling of <see cref="LateOrderMessageBuilder"/> and deliberately a separate class rather
/// than a shared generic one: the two modules differ in the only thing a template renderer is —
/// the placeholder vocabulary. A merged renderer would have to accept <c>{deadline}</c> in an incident
/// message and <c>{reason}</c> in a late-order one, and "unknown placeholder" — the check that stops
/// "Merhaba ," reaching a seller — would stop meaning anything.</para>
///
/// <para>Two templates rather than one, for the same reason as the late-order pair: a single template
/// cannot express "repeat this once per incident" without inventing a loop syntax. The envelope is
/// rendered once; the line template is rendered once per incident and the block substituted in.</para>
///
/// <para>The defaults here are what ships and what "Reset to default" restores. The operator's edited
/// versions live in the same LiteDB document as the seller mapping — see
/// <see cref="SellerGroupStore.SaveIncidentSettings"/>.</para>
/// </summary>
public static partial class IncidentWarningMessageBuilder
{
    /// <summary>
    /// Turkish, because the recipients are Turkish sellers; the UI chrome around it stays English like
    /// the rest of the app.
    ///
    /// <para>The wording says the customer is waiting rather than that the seller is late, because that
    /// is the state the eligibility rule actually establishes: an incident is only chased here when it
    /// is open and the customer spoke last. Turkish does not pluralise a noun after a numeral, so
    /// "1 talebiniz" and "4 talebiniz" are both correct and one phrasing serves any count.</para>
    ///
    /// <para>No emoticon sequences (<c>:)</c> and friends): WhatsApp's composer converts them to emoji
    /// as they are typed, which would fail the runner's read-back verification.</para>
    /// </summary>
    public const string DefaultTemplate =
        """
        Selamlar,

        Aşağıdaki {incidentCount} talebiniz {maxAgeDays} gündür açık ve müşteri yanıt bekliyor, kontrol edebilir misiniz?

        {incidents}{truncationNote}

        Talebin panelden yanıtlanması gerekiyor; yanıtlanmayan talepler müşteri şikâyetine dönüşebiliyor. Desteğinizi rica ederiz.
        """;

    /// <summary>
    /// Carries the reason and the age, unlike the late-order line's bare order number. Two reasons: one
    /// order can raise several incidents, so the number alone would print the same value twice in one
    /// list with nothing to tell the entries apart; and the seller has to find the right thread in their
    /// panel, which the reason is what identifies.
    /// </summary>
    public const string DefaultIncidentLineTemplate = "• {orderNumber} — {reason} ({age})";

    public static readonly string[] EnvelopePlaceholders =
    [
        "{seller}", "{incidentCount}", "{maxAgeDays}",
        "{incidents}", "{referenceTime}", "{truncationNote}",
    ];

    public static readonly string[] IncidentLinePlaceholders =
    [
        "{orderNumber}", "{openedOn}", "{ageDays}", "{age}", "{reason}", "{status}",
    ];

    /// <summary>Both sets, for the panel's placeholder reference line.</summary>
    public static readonly string[] KnownPlaceholders = [.. EnvelopePlaceholders, .. IncidentLinePlaceholders];

    [GeneratedRegex(@"\{[A-Za-z][A-Za-z0-9]*\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>One seller, one group.</summary>
    public static RenderedMessage Render(
        IncidentWarningSeller seller,
        string referenceTime,
        string? template,
        string? lineTemplate)
    {
        ArgumentNullException.ThrowIfNull(seller);
        return Render([seller], referenceTime, template, lineTemplate);
    }

    /// <summary>
    /// Every seller account that resolved to one WhatsApp group, rendered as the single message that
    /// group receives.
    ///
    /// <para>More than one is rarer here than in Late Order Warnings — that module merges two Mirakl
    /// ids trading as one company, and the incident export has no id at all, so accounts can only
    /// collide when two distinct seller names are mapped to the same group by hand. It still has to be
    /// handled: two messages in one chat is not what the operator wants.</para>
    ///
    /// <para>Returns the same <see cref="RenderedMessage"/> the late-order path produces, so the send
    /// endpoint, the preview cards and the character-cap check are one implementation rather than two.
    /// <c>SellerId</c> is always empty here — the export carries no such column.</para>
    /// </summary>
    public static RenderedMessage Render(
        IReadOnlyList<IncidentWarningSeller> sellers,
        string referenceTime,
        string? template,
        string? lineTemplate)
    {
        ArgumentNullException.ThrowIfNull(sellers);
        if (sellers.Count == 0)
            throw new ArgumentException("A message needs at least one seller.", nameof(sellers));

        var envelope = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        var line = string.IsNullOrWhiteSpace(lineTemplate) ? DefaultIncidentLineTemplate : lineTemplate;

        var merged = sellers.Count > 1;
        var totalIncidents = sellers.Sum(s => s.Incidents.Count);

        // The line cap is spent across the accounts in the order given — already oldest-first from
        // IncidentWarningBuilder.GroupSellers — so what gets dropped is the least aged.
        var remaining = IncidentWarningBuilder.MaxIncidentLinesPerMessage;
        var sections = new List<string>(sellers.Count);
        var shownCount = 0;

        foreach (var seller in sellers)
        {
            if (remaining <= 0) break;

            var shown = seller.Incidents.Take(remaining).ToList();
            if (shown.Count == 0) continue;

            remaining -= shown.Count;
            shownCount += shown.Count;

            var lines = string.Join("\n", shown.Select(incident => RenderIncidentLine(line, incident)));
            sections.Add(merged ? $"{seller.SellerName}:\n{lines}" : lines);
        }

        var incidents = string.Join("\n\n", sections);
        var hidden = totalIncidents - shownCount;

        // Appended directly under the list, so the blank line before the closing paragraph is the
        // template's and does not disappear when nothing was truncated.
        var truncationNote = hidden > 0
            ? $"\n…ve {hidden:N0} talep daha — tam liste ekte."
            : "";

        var names = sellers
            .Select(s => s.SellerName)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var sellerName = string.Join(" / ", names);
        var incidentCount = sellers.Sum(s => s.IncidentCount);
        var maxAgeDays = sellers.Max(s => s.MaxAgeDays);

        var body = envelope
            .Replace("{seller}", sellerName)
            .Replace("{incidentCount}", incidentCount.ToString("N0", CultureInfo.InvariantCulture))
            .Replace("{maxAgeDays}", FormatDays(maxAgeDays))
            .Replace("{referenceTime}", referenceTime)
            .Replace("{truncationNote}", truncationNote)
            // Substituted LAST, after every other envelope placeholder. An order number or a reason
            // containing a literal "{seller}" would otherwise be re-substituted — the classic
            // template-injection foot-gun, pinned by a test in both this builder and its sibling.
            .Replace("{incidents}", incidents);

        return new RenderedMessage(
            // Trimmed to match the send path, which compares group names ordinally after trimming.
            GroupName: (sellers[0].GroupName ?? "").Trim(),
            SellerId: "",
            SellerName: sellerName,
            // Normalised to \n so the runner's mandatory split-before-typing cannot leave a stray \r
            // to press. A \r in a WhatsApp composer is not harmless.
            Body: body.Replace("\r\n", "\n").Replace("\r", "\n"),
            OrderCount: incidentCount,
            Truncated: hidden > 0,
            UnknownPlaceholders: FindUnknown(envelope, line),
            AccountCount: sellers.Count);
    }

    static string RenderIncidentLine(string template, IncidentWarningLine incident) => template
        .Replace("{orderNumber}", incident.OrderNumber)
        .Replace("{openedOn}", incident.OpenedOn)
        .Replace("{ageDays}", incident.AgeDays.ToString("0.#", CultureInfo.InvariantCulture))
        .Replace("{age}", ComposeAge(incident.AgeDays))
        .Replace("{reason}", incident.Reason)
        .Replace("{status}", incident.Status);

    /// <summary>
    /// "3 gün" once there is a whole day to report, "18 saat" below that. Floored, never rounded up:
    /// announcing an incident 40 minutes old as "1 gün" is a number the seller can disprove, after
    /// which every figure from this channel is suspect.
    /// </summary>
    static string ComposeAge(double ageDays)
    {
        var wholeDays = (int)Math.Floor(ageDays);
        return wholeDays >= 1
            ? $"{wholeDays} gün"
            : $"{Math.Max(0, (int)Math.Round(ageDays * 24))} saat";
    }

    /// <summary>Whole days for the envelope's headline figure — "2 gündür açık", not "2,4 gündür".</summary>
    static string FormatDays(double ageDays) =>
        Math.Max(0, (int)Math.Floor(ageDays)).ToString(CultureInfo.InvariantCulture);

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
        Scan(lineTemplate, IncidentLinePlaceholders);

        return unknown;
    }
}
