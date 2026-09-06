using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The text that reaches a seller's WhatsApp group about their unanswered incidents. The sibling of
/// <see cref="LateOrderMessageBuilderTests"/>, pinning the same three things that matter for anything
/// this app types into a real chat: the line cap, the substitution order, and that a typo in the
/// template is reported rather than silently shipped.
/// </summary>
public class IncidentWarningMessageBuilderTests
{
    static IncidentWarningLine Incident(string orderNumber, double ageDays = 3, string reason = "Defective item") => new(
        OrderNumber: orderNumber,
        OpenedOn: "2026-09-01 09:00",
        AgeDays: ageDays,
        Reason: reason,
        Status: "Incident in progress");

    static IncidentWarningSeller Seller(string name, string? group, params IncidentWarningLine[] incidents) => new(
        SellerName: name,
        GroupName: group,
        MappingProblem: null,
        MappingConflict: false,
        IncidentCount: incidents.Length,
        MaxAgeDays: incidents.Length > 0 ? incidents.Max(i => i.AgeDays) : 0,
        Incidents: incidents);

    static RenderedMessage Render(
        IReadOnlyList<IncidentWarningSeller> sellers,
        string? template = null,
        string? lineTemplate = null) =>
        IncidentWarningMessageBuilder.Render(sellers, "2026-09-06 09:00", template, lineTemplate);

    /// <summary>The rendered incident block, without the envelope around it.</summary>
    static string ExtractIncidents(string body, string lineTemplate = "• ")
    {
        var lines = body.Split('\n').Where(l => l.StartsWith(lineTemplate, StringComparison.Ordinal));
        return string.Join("\n", lines);
    }

    // -----------------------------------------------------------------
    // The ordinary case
    // -----------------------------------------------------------------

    [Fact]
    public void OneSellerGetsAFlatListWithNoHeading()
    {
        var message = Render([Seller("Prodesk", "MediaMarkt - Prodesk", Incident("A-1"), Incident("A-2"))]);

        Assert.DoesNotContain("Prodesk:", message.Body, StringComparison.Ordinal);
        Assert.Equal("MediaMarkt - Prodesk", message.GroupName);
        Assert.Equal("Prodesk", message.SellerName);
        Assert.Equal(2, message.OrderCount);
        Assert.Equal(1, message.AccountCount);
        Assert.False(message.Truncated);
    }

    /// <summary>The incident export has no seller-id column, so there is never one to carry. The field
    /// exists only because the send path and the Excel export are shared with Late Order Warnings.</summary>
    [Fact]
    public void TheSellerIdIsAlwaysEmpty()
    {
        Assert.Equal("", Render([Seller("Prodesk", "G", Incident("A-1"))]).SellerId);
    }

    /// <summary>The panel and the send path compare group names ordinally after trimming; the rendered
    /// message has to already be on the trimmed side of that comparison.</summary>
    [Fact]
    public void TheGroupNameIsTrimmed()
    {
        Assert.Equal("MediaMarkt - Prodesk",
            Render([Seller("Prodesk", "  MediaMarkt - Prodesk  ", Incident("A-1"))]).GroupName);
    }

    /// <summary>The default line carries the reason precisely so two incidents on one order do not
    /// print as the same value twice with nothing to tell them apart.</summary>
    [Fact]
    public void TwoIncidentsOnOneOrderAreDistinguishableInTheDefaultLine()
    {
        var message = Render([Seller("Prodesk", "G",
            Incident("A-1", reason: "Defective item"),
            Incident("A-1", reason: "Missing accessory"))]);

        Assert.Contains("Defective item", message.Body, StringComparison.Ordinal);
        Assert.Contains("Missing accessory", message.Body, StringComparison.Ordinal);
    }

    /// <summary>Floored, never rounded up: an incident open 40 minutes announced as "1 gün" is a number
    /// the seller can disprove, after which every figure from this channel is suspect.</summary>
    [Theory]
    [InlineData(3.9, "3 gün")]
    [InlineData(1.0, "1 gün")]
    [InlineData(0.5, "12 saat")]
    public void TheAgePlaceholderReadsInDaysOrHours(double ageDays, string expected)
    {
        var message = Render(
            [Seller("Prodesk", "G", Incident("A-1", ageDays: ageDays))],
            lineTemplate: "• {age}");

        Assert.Contains(expected, message.Body, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // The line cap
    // -----------------------------------------------------------------

    /// <summary>
    /// Lower than the late-order cap on purpose: an incident line carries the reason and the age, so
    /// sixty of them would run past the runner's character limit and the send endpoint would refuse a
    /// message the operator had already approved.
    /// </summary>
    [Fact]
    public void TheListIsCappedAndTheRestAreCountedInANote()
    {
        var incidents = Enumerable
            .Range(1, IncidentWarningBuilder.MaxIncidentLinesPerMessage + 5)
            .Select(i => Incident($"A-{i}"))
            .ToArray();

        var message = Render([Seller("Prodesk", "G", incidents)]);

        Assert.True(message.Truncated);
        Assert.Equal(incidents.Length, message.OrderCount);
        Assert.Equal(IncidentWarningBuilder.MaxIncidentLinesPerMessage, ExtractIncidents(message.Body).Split('\n').Length);
        Assert.Contains("5 talep daha", message.Body, StringComparison.Ordinal);
    }

    /// <summary>A message that fits leaves no dangling note and keeps the template's own blank line
    /// before the closing paragraph.</summary>
    [Fact]
    public void NothingIsAppendedWhenNothingWasTruncated()
    {
        var message = Render([Seller("Prodesk", "G", Incident("A-1"))]);

        Assert.False(message.Truncated);
        Assert.DoesNotContain("talep daha", message.Body, StringComparison.Ordinal);
    }

    /// <summary>Even at the cap the default wording stays inside the runner's character limit — the
    /// whole reason the cap is 30 rather than 60.</summary>
    [Fact]
    public void AFullMessageWithTheDefaultTemplateStaysUnderTheCharacterLimit()
    {
        var incidents = Enumerable
            .Range(1, IncidentWarningBuilder.MaxIncidentLinesPerMessage)
            .Select(i => Incident($"01259_32667435{i}-A", reason: "Ürün kutusundan farklı çıktı ve iade talep ediliyor"))
            .ToArray();

        var body = Render([Seller("Prodesk", "G", incidents)]).Body;

        Assert.True(body.Length < YeniRPA.Web.Services.Automation.WhatsAppMessageRunner.MaxMessageChars,
            $"A full default message is {body.Length} characters, which the send endpoint would refuse.");
    }

    // -----------------------------------------------------------------
    // Two sellers, one group
    // -----------------------------------------------------------------

    [Fact]
    public void TwoSellersSharingAGroupGetOneMessageWithHeadings()
    {
        var message = Render([
            Seller("Prodesk", "Shared group", Incident("A-1")),
            Seller("Prodesk Teknoloji", "Shared group", Incident("B-1")),
        ]);

        Assert.Equal(2, message.AccountCount);
        Assert.Equal("Prodesk / Prodesk Teknoloji", message.SellerName);
        Assert.Contains("Prodesk:", message.Body, StringComparison.Ordinal);
        Assert.Contains("Prodesk Teknoloji:", message.Body, StringComparison.Ordinal);
    }

    /// <summary>Two rows can carry the same trading name; without the distinct the greeting would read
    /// "Prodesk / Prodesk".</summary>
    [Fact]
    public void ARepeatedNameIsNotJoinedTwice()
    {
        var message = Render([
            Seller("Prodesk", "Shared group", Incident("A-1")),
            Seller("Prodesk", "Shared group", Incident("B-1")),
        ]);

        Assert.Equal("Prodesk", message.SellerName);
    }

    /// <summary>The cap is spent across the accounts, and an account whose whole share was cut leaves
    /// no empty heading behind.</summary>
    [Fact]
    public void AnAccountCutEntirelyLeavesNoEmptyHeading()
    {
        var first = Enumerable
            .Range(1, IncidentWarningBuilder.MaxIncidentLinesPerMessage)
            .Select(i => Incident($"A-{i}"))
            .ToArray();

        var message = Render([
            Seller("First", "Shared group", first),
            Seller("Second", "Shared group", Incident("B-1")),
        ]);

        Assert.DoesNotContain("Second:", message.Body, StringComparison.Ordinal);
        Assert.True(message.Truncated);
    }

    // -----------------------------------------------------------------
    // Templates
    // -----------------------------------------------------------------

    /// <summary>
    /// The template-injection guard. An order number or a reason containing a literal placeholder must
    /// land in the message verbatim, never be substituted a second time — which is why
    /// <c>{incidents}</c> is replaced after every other envelope placeholder.
    /// </summary>
    [Fact]
    public void AReasonContainingAPlaceholderIsNotResubstituted()
    {
        var message = Render([Seller("Prodesk", "G", Incident("A-1", reason: "{seller} said no"))]);

        Assert.Contains("{seller} said no", message.Body, StringComparison.Ordinal);
    }

    /// <summary>Left in the output verbatim rather than deleted — dropping them would ship "Merhaba ,"
    /// to a seller — and reported so the panel can point at the typo.</summary>
    [Fact]
    public void AnUnknownPlaceholderIsReportedAndLeftInPlace()
    {
        var message = Render(
            [Seller("Prodesk", "G", Incident("A-1"))],
            template: "Merhaba {sellerName}, {incidents}");

        Assert.Contains("{sellerName}", Assert.Single(message.UnknownPlaceholders), StringComparison.Ordinal);
        Assert.Contains("{sellerName}", message.Body, StringComparison.Ordinal);
    }

    /// <summary>A late-order placeholder is not an incident one. Sharing a vocabulary between the two
    /// modules would make this check meaningless.</summary>
    [Fact]
    public void ALateOrderPlaceholderIsUnknownHere()
    {
        var message = Render(
            [Seller("Prodesk", "G", Incident("A-1"))],
            lineTemplate: "• {orderNumber} {deadline}");

        Assert.Contains("{deadline}", message.UnknownPlaceholders);
    }

    [Fact]
    public void ABlankTemplateFallsBackToTheDefault()
    {
        var withBlank = Render([Seller("Prodesk", "G", Incident("A-1"))], template: "   ").Body;
        var withNull = Render([Seller("Prodesk", "G", Incident("A-1"))]).Body;

        Assert.Equal(withNull, withBlank);
    }

    /// <summary>A \r in a WhatsApp composer is not harmless: the runner splits on \n before typing and
    /// a stray carriage return would be pressed as a key.</summary>
    [Fact]
    public void TheBodyIsNormalisedToUnixNewlines()
    {
        var message = Render(
            [Seller("Prodesk", "G", Incident("A-1"))],
            template: "Line one\r\nLine two\r{incidents}");

        Assert.DoesNotContain('\r', message.Body);
    }

    [Fact]
    public void RenderingNoSellersAtAllIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Render([]));
    }
}
