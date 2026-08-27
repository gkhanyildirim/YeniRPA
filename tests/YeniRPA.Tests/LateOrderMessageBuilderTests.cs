using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The text that reaches a seller's WhatsApp group. Two things are pinned down here: that one company
/// trading under two Mirakl ids gets a single readable message instead of two messages in one chat,
/// and that a seller with one account still receives exactly what they received before merging
/// existed.
/// </summary>
public class LateOrderMessageBuilderTests
{
    static LateOrderLine Order(string number, int daysLate = 2) => new(
        OrderNumber: number,
        Status: "Awaiting shipment",
        DeadlineRaw: "2026-08-20 10:00",
        DeadlineEffective: "2026-08-20 10:00",
        DaysLate: daysLate,
        HoursLate: daysLate * 24,
        DateCreated: null,
        AcceptanceDate: null,
        ShippingCompany: null,
        LineCount: 1);

    static LateOrderSeller Seller(
        string id, string name, string? group, params LateOrderLine[] orders) => new(
        SellerId: id,
        SellerName: name,
        GroupName: group,
        MappingProblem: null,
        OrderCount: orders.Length,
        MaxDaysLate: orders.Length > 0 ? orders.Max(o => o.DaysLate) : 0,
        Orders: orders);

    static RenderedMessage Render(
        IReadOnlyList<LateOrderSeller> sellers,
        string? template = null,
        string? lineTemplate = null) =>
        LateOrderMessageBuilder.Render(sellers, "2026-08-26 09:00", template, lineTemplate);

    // -----------------------------------------------------------------
    // One account — the ordinary case, unchanged by merging
    // -----------------------------------------------------------------

    [Fact]
    public void ASingleAccountGetsAFlatListWithNoHeading()
    {
        var message = Render([Seller("11835", "Prodesk", "MediaMarkt - Prodesk", Order("A-1"), Order("A-2"))]);

        Assert.Equal("• A-1\n• A-2", ExtractOrders(message.Body));
        Assert.DoesNotContain("Prodesk:", message.Body, StringComparison.Ordinal);
        Assert.Equal(1, message.AccountCount);
        Assert.Equal("Prodesk", message.SellerName);
        Assert.Equal("11835", message.SellerId);
        Assert.Equal(2, message.OrderCount);
        Assert.False(message.Truncated);
    }

    /// <summary>The panel and the send path compare group names ordinally after trimming; the rendered
    /// message has to already be on the trimmed side of that comparison.</summary>
    [Fact]
    public void TheGroupNameIsTrimmed()
    {
        var message = Render([Seller("11835", "Prodesk", "  MediaMarkt - Prodesk  ", Order("A-1"))]);
        Assert.Equal("MediaMarkt - Prodesk", message.GroupName);
    }

    // -----------------------------------------------------------------
    // Two accounts, one group
    // -----------------------------------------------------------------

    [Fact]
    public void TwoAccountsSharingAGroupBecomeOneMessageWithAHeadingEach()
    {
        var message = Render([
            Seller("11835", "Fressi Home", "MediaMarkt - Fressi Home / Wexta", Order("A-1", 5), Order("A-2", 3)),
            Seller("22461", "Wexta", "MediaMarkt - Fressi Home / Wexta", Order("B-1", 2)),
        ]);

        Assert.Equal(
            "Fressi Home:\n• A-1\n• A-2\n\nWexta:\n• B-1",
            ExtractOrders(message.Body));

        Assert.Equal(2, message.AccountCount);
        Assert.Equal("Fressi Home / Wexta", message.SellerName);
        Assert.Equal("11835 / 22461", message.SellerId);
        Assert.Equal(3, message.OrderCount);
        Assert.Contains("Aşağıdaki 3 siparişiniz", message.Body, StringComparison.Ordinal);
        Assert.False(message.Truncated);
    }

    /// <summary><c>{maxDaysLate}</c> has to speak for the whole message, not for whichever account
    /// happened to be listed first.</summary>
    [Fact]
    public void MaxDaysLateIsTakenAcrossEveryAccount()
    {
        var message = Render(
            [
                Seller("11835", "Fressi Home", "shared", Order("A-1", 2)),
                Seller("22461", "Wexta", "shared", Order("B-1", 9)),
            ],
            template: "{maxDaysLate}");

        Assert.Equal("9", message.Body);
    }

    /// <summary>One company can run two ids under the same trading name; joining blindly would greet
    /// "Fressi Home / Fressi Home".</summary>
    [Fact]
    public void ARepeatedAccountNameIsNotJoinedTwice()
    {
        var message = Render([
            Seller("11835", "Fressi Home", "shared", Order("A-1")),
            Seller("22461", "Fressi Home", "shared", Order("B-1")),
        ]);

        Assert.Equal("Fressi Home", message.SellerName);
        Assert.Equal("11835 / 22461", message.SellerId);
    }

    // -----------------------------------------------------------------
    // Truncation
    // -----------------------------------------------------------------

    /// <summary>The cap is on the message, not on each account, and it is spent most-overdue first —
    /// so the orders that fall off the end are the least late ones.</summary>
    [Fact]
    public void TheLineCapIsSpentAcrossAccountsInOrder()
    {
        var cap = LateOrderBuilder.MaxOrderLinesPerMessage;

        var first = Enumerable.Range(0, cap - 1).Select(i => Order($"A-{i}", 9)).ToArray();
        var second = Enumerable.Range(0, 5).Select(i => Order($"B-{i}", 1)).ToArray();

        var message = Render([
            Seller("11835", "Fressi Home", "shared", first),
            Seller("22461", "Wexta", "shared", second),
        ]);

        Assert.True(message.Truncated);
        Assert.Equal(cap + 4, message.OrderCount);
        Assert.Contains("…ve 4 sipariş daha", message.Body, StringComparison.Ordinal);

        // The second account keeps its heading because one of its orders still fit.
        Assert.Contains("Wexta:\n• B-0", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("• B-1", message.Body, StringComparison.Ordinal);
    }

    /// <summary>An account whose whole share was cut must not leave a heading with nothing under it.</summary>
    [Fact]
    public void AnAccountWithNoRoomLeftLeavesNoEmptyHeading()
    {
        var cap = LateOrderBuilder.MaxOrderLinesPerMessage;
        var first = Enumerable.Range(0, cap).Select(i => Order($"A-{i}", 9)).ToArray();

        var message = Render([
            Seller("11835", "Fressi Home", "shared", first),
            Seller("22461", "Wexta", "shared", Order("B-1", 1)),
        ]);

        Assert.True(message.Truncated);
        Assert.DoesNotContain("Wexta:", message.Body, StringComparison.Ordinal);
        Assert.Contains("…ve 1 sipariş daha", message.Body, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // Templates
    // -----------------------------------------------------------------

    /// <summary>
    /// The classic template-injection foot-gun, and in a merged message it would put one account's
    /// name into the other account's order list. <c>{orders}</c> is substituted last, so nothing an
    /// order number contains is ever re-substituted.
    /// </summary>
    [Fact]
    public void AnOrderNumberContainingAPlaceholderIsNotResubstituted()
    {
        var message = Render(
            [
                Seller("11835", "Fressi Home", "shared", Order("{seller}")),
                Seller("22461", "Wexta", "shared", Order("B-1")),
            ],
            template: "{orders}");

        Assert.Equal("Fressi Home:\n• {seller}\n\nWexta:\n• B-1", message.Body);
    }

    [Fact]
    public void UnknownPlaceholdersAreStillReportedForAMergedMessage()
    {
        var message = Render(
            [
                Seller("11835", "Fressi Home", "shared", Order("A-1")),
                Seller("22461", "Wexta", "shared", Order("B-1")),
            ],
            template: "{sellerr} {orders}");

        Assert.Equal(["{sellerr}"], message.UnknownPlaceholders);
        Assert.Contains("{sellerr}", message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderingNoSellerAtAllIsRefused() =>
        Assert.Throws<ArgumentException>(() => Render([]));

    // -----------------------------------------------------------------

    /// <summary>The order block out of the default envelope, so a test can assert on the list alone.</summary>
    static string ExtractOrders(string body)
    {
        var start = body.IndexOf("• ", StringComparison.Ordinal);
        if (start < 0) return "";

        // The heading of the first account sits on the line above its first bullet.
        var headingStart = body.LastIndexOf("\n\n", start, StringComparison.Ordinal);
        start = headingStart >= 0 ? headingStart + 2 : start;

        var end = body.IndexOf("\n\nEğer", start, StringComparison.Ordinal);
        return end < 0 ? body[start..] : body[start..end];
    }
}
