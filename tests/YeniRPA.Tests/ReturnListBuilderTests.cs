using System.Text;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using static YeniRPA.Tests.ReturnFiles;

namespace YeniRPA.Tests;

/// <summary>
/// The list the Create Return automation runs on. Every row it holds becomes a return filed on the
/// marketplace, so the rules that keep a row off it are pinned here — in particular the two
/// cancelled checks, which exist so the run does not open a page it can never submit.
/// </summary>
public class ReturnListBuilderTests
{
    // -----------------------------------------------------------------
    // Cancelled requests and cancelled orders
    // -----------------------------------------------------------------

    /// <summary>
    /// Template B's State column is the marketplace's own view of the request. A cancelled one needs
    /// no return, and filing it would only cost a page load and a screenshot.
    /// </summary>
    [Fact]
    public void CancelledRequestStateIsDroppedAndListed()
    {
        var data = Build(
            orders: Orders(new OrderRow("01259_321097726-A", "Received")),
            templateB: TemplateB(new TemplateBRow(
                "321097726", MarketPlaceId: "01259_321097726-A", ShipDate: DaysAgo(10), State: "CANCELLED")));

        Assert.Empty(data.Rows);

        var dropped = Assert.Single(data.Excluded);
        Assert.Equal("321097726", dropped.SourceOrderNo);
        Assert.Equal("Request state is CANCELLED", dropped.Reason);
    }

    /// <summary>
    /// Template A carries a request type, never a state, so the orders export is the only place a
    /// Marketplace row's order status can be read.
    /// </summary>
    [Fact]
    public void CanceledOrderInTheOrdersExportIsDroppedAndListed()
    {
        var data = Build(
            orders: Orders(new OrderRow("01259_321097726-A", "Canceled")),
            templateA: TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(10))));

        Assert.Empty(data.Rows);

        var dropped = Assert.Single(data.Excluded);
        Assert.Equal("Order is canceled in the orders export", dropped.Reason);
    }

    /// <summary>The same check reaches template B rows, whose own state says nothing about the order.</summary>
    [Fact]
    public void CanceledOrderIsDroppedEvenWhenTheRequestStateIsOpen()
    {
        var data = Build(
            orders: Orders(new OrderRow("01259_321097726-A", "Canceled")),
            templateB: TemplateB(new TemplateBRow(
                "321097726", MarketPlaceId: "01259_321097726-A", ShipDate: DaysAgo(10), State: "SHIPPED")));

        Assert.Empty(data.Rows);
        Assert.Equal("Order is canceled in the orders export", Assert.Single(data.Excluded).Reason);
    }

    /// <summary>
    /// The counterpart every filter needs: a live order with an open request still reaches the list.
    /// Both checks match positively, so an unfamiliar state must not be mistaken for a cancellation.
    /// </summary>
    [Theory]
    [InlineData("SHIPPED")]
    [InlineData("RECEIVED")]
    [InlineData("Something nobody has seen before")]
    public void AnOpenRequestOnALiveOrderStaysOnTheList(string state)
    {
        var data = Build(
            orders: Orders(new OrderRow("01259_321097726-A", "Received")),
            templateB: TemplateB(new TemplateBRow(
                "321097726", MarketPlaceId: "01259_321097726-A", ShipDate: DaysAgo(10), State: state)));

        var row = Assert.Single(data.Rows);
        Assert.Equal("01259_321097726-A", row.OrderNumber);
        Assert.Equal("1234567890", row.TrackingNumber);
        Assert.Empty(data.Excluded);
    }

    /// <summary>An orders export without the Status column still produces a list; only the check goes quiet.</summary>
    [Fact]
    public void OrdersExportWithoutAStatusColumnStillProducesTheList()
    {
        const string ordersWithoutStatus =
            "Order number;Seller ID;Seller\n" +
            "01259_321097726-A;11616.0;Test Seller";

        var data = Build(
            orders: ordersWithoutStatus,
            templateA: TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(10))));

        Assert.Equal("01259_321097726-A", Assert.Single(data.Rows).OrderNumber);
    }

    // -----------------------------------------------------------------
    // The funnel
    // -----------------------------------------------------------------

    /// <summary>
    /// The funnel promises that every drop is the gap between two of its columns, which only holds
    /// while the cancelled-state step has a column of its own.
    /// </summary>
    [Fact]
    public void TheCancelledStateDropShowsUpOnTheFunnel()
    {
        var data = Build(
            orders: Orders(
                new OrderRow("01259_321097726-A", "Received"),
                new OrderRow("01259_321097727-A", "Received")),
            templateB: TemplateB(
                new TemplateBRow("321097726", MarketPlaceId: "01259_321097726-A", ShipDate: DaysAgo(10), State: "SHIPPED"),
                new TemplateBRow("321097727", MarketPlaceId: "01259_321097727-A", ShipDate: DaysAgo(10), State: "CANCELLED")));

        var funnel = Assert.Single(data.Funnels, f => f.Source == ReturnListBuilder.TemplateBSource);
        Assert.Equal(2, funnel.AfterRequestType);
        Assert.Equal(1, funnel.AfterState);
        Assert.Equal(1, funnel.Ready);
    }

    // -----------------------------------------------------------------

    /// <summary>
    /// CSV in memory, the same way <see cref="ReturnFiles"/> feeds the SLA report — the reader is
    /// picked from the file name, so a whole export fits in a few lines of text.
    /// </summary>
    static ReturnListData Build(
        string orders,
        string? templateA = null,
        string? templateB = null,
        string? returns = null,
        DateTime? from = null,
        DateTime? to = null,
        bool returnsOnly = false)
    {
        using var templateAStream = Stream(templateA ?? TemplateA());
        using var templateBStream = Stream(templateB ?? TemplateB());
        using var returnsStream = Stream(returns ?? "Order number");
        using var ordersStream = Stream(orders);

        return ReturnListBuilder.Build(
            templateAStream, "template-a.csv",
            templateBStream, "template-b.csv",
            returnsStream, "returns.csv",
            ordersStream, "orders.csv",
            new ReturnListOptions(from, to, returnsOnly));
    }

    static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));
}
