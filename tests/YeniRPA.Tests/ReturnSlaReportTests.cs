using YeniRPA.Web.Services;
using static YeniRPA.Tests.ReturnFiles;

namespace YeniRPA.Tests;

/// <summary>
/// The join between the return templates and the orders export, and the SLA verdict that rests on
/// it. These are the rules that decide whether an operator chases a seller, so they are pinned here
/// rather than checked by eye on a dashboard.
/// </summary>
public class ReturnSlaReportTests
{
    // -----------------------------------------------------------------
    // Matching the bare template number to the full Mirakl order number
    // -----------------------------------------------------------------

    [Fact]
    public void BareOrderNumberResolvesToTheFullMiraklNumber()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received", Seller: "Altınkoza Teknolojim")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(20))));

        var row = Assert.Single(data.Rows);
        Assert.Equal("01259_321097726-A", row.OrderNumber);
        Assert.Equal("321097726", row.SourceOrderNumber);
        Assert.Equal("Altınkoza Teknolojim", row.Seller);
        Assert.Equal(ReturnSlaReportBuilder.MatchedState, row.MatchState);
    }

    /// <summary>
    /// The case that started this: order 321097726 is Canceled in the orders export, so its return
    /// is done — the report used to list it as an SLA breach because the join never matched.
    /// </summary>
    [Fact]
    public void CanceledOrderCountsAsACompletedReturnAndIsNotABreach()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Canceled")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(20))));

        var row = Assert.Single(data.Rows);
        Assert.True(row.IsConfirmedReturn);
        Assert.False(row.SlaMissed);
        Assert.False(row.PastWarning);
        Assert.Equal("Canceled", row.Status);
    }

    [Theory]
    [InlineData("Refunded")]
    [InlineData("Refused")]
    [InlineData("Rejected")]
    public void EveryClosingStatusCountsAsACompletedReturn(string status)
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", status)),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(30))));

        Assert.True(Assert.Single(data.Rows).IsConfirmedReturn);
    }

    [Fact]
    public void OpenOrderPastFifteenDaysIsABreach()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(20))));

        var row = Assert.Single(data.Rows);
        Assert.False(row.IsConfirmedReturn);
        Assert.True(row.SlaMissed);
        Assert.False(row.PastWarning);
        Assert.True(row.ElapsedDays >= 20);
    }

    [Fact]
    public void OpenOrderBetweenTenAndFifteenDaysIsAWarningNotABreach()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(12))));

        var row = Assert.Single(data.Rows);
        Assert.True(row.PastWarning);
        Assert.False(row.SlaMissed);
    }

    /// <summary>A finished return is not "at risk at 12 days", so it stays out of the warning list.</summary>
    [Fact]
    public void CompletedReturnIsNotInTheWarningList()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Canceled")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(12))));

        Assert.False(Assert.Single(data.Rows).PastWarning);
    }

    [Fact]
    public void OrderMissingFromTheExportIsReportedForReviewAndNeverAsABreach()
    {
        var data = Build(
            Orders(new OrderRow("01259_999999999-A", "Received")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(40))));

        var row = Assert.Single(data.Rows);
        Assert.Equal(ReturnSlaReportBuilder.NotFoundState, row.MatchState);
        Assert.Equal(ReturnSlaReportBuilder.UnmatchedStatus, row.Status);
        Assert.False(row.SlaMissed);
        Assert.False(row.PastWarning);
        // The number is still shown, so the row can be looked up by hand.
        Assert.Equal("321097726", row.OrderNumber);
    }

    // -----------------------------------------------------------------
    // One customer order split across sellers: …-A / …-B
    // -----------------------------------------------------------------

    [Fact]
    public void SellerIdPicksTheRightHalfOfASplitOrder()
    {
        var data = Build(
            Orders(
                new OrderRow("01259_321097726-A", "Received", Seller: "Seller A", SellerId: "11111.0"),
                new OrderRow("01259_321097726-B", "Canceled", Seller: "Seller B", SellerId: "22222.0")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(20), SellerId: "22222")));

        var row = Assert.Single(data.Rows);
        Assert.Equal("01259_321097726-B", row.OrderNumber);
        Assert.Equal("Seller B", row.Seller);
        Assert.True(row.IsConfirmedReturn);
        Assert.False(row.SlaMissed);
    }

    [Fact]
    public void AgreeingStatusesDecideASplitOrderTheSellerIdCannot()
    {
        var data = Build(
            Orders(
                new OrderRow("01259_321097726-A", "Canceled", SellerId: "11111.0"),
                new OrderRow("01259_321097726-B", "Refunded", SellerId: "22222.0")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(20))));

        var row = Assert.Single(data.Rows);
        Assert.Equal(ReturnSlaReportBuilder.MatchedByStatusState, row.MatchState);
        Assert.Equal(2, row.MatchCount);
        Assert.True(row.IsConfirmedReturn);
        Assert.False(row.SlaMissed);
    }

    [Fact]
    public void ConflictingStatusesLeaveTheRowAmbiguousAndOutOfTheBreachList()
    {
        var data = Build(
            Orders(
                new OrderRow("01259_321097726-A", "Received", SellerId: "11111.0"),
                new OrderRow("01259_321097726-B", "Canceled", SellerId: "22222.0")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(40))));

        var row = Assert.Single(data.Rows);
        Assert.Equal(ReturnSlaReportBuilder.AmbiguousState, row.MatchState);
        Assert.Equal(ReturnSlaReportBuilder.AmbiguousStatus, row.Status);
        Assert.False(row.SlaMissed);
        Assert.Contains("01259_321097726-A", row.OrderNumber);
        Assert.Contains("01259_321097726-B", row.OrderNumber);
    }

    /// <summary>
    /// The export is one row per order line, so an order can hold several statuses. The return is
    /// against the order, and one canceled line is a return that happened.
    /// </summary>
    [Fact]
    public void OneCanceledLineMakesTheWholeOrderACompletedReturn()
    {
        var data = Build(
            Orders(
                new OrderRow("01259_321097726-A", "Received"),
                new OrderRow("01259_321097726-A", "Canceled")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(20))));

        var row = Assert.Single(data.Rows);
        Assert.Equal(ReturnSlaReportBuilder.MatchedState, row.MatchState);
        Assert.True(row.IsConfirmedReturn);
        Assert.Equal("Received / Canceled", row.Status);
    }

    // -----------------------------------------------------------------
    // Template reading
    // -----------------------------------------------------------------

    /// <summary>
    /// The MP export writes the literal text "NULL" into the tracking column on most rows. Those
    /// returns were never shipped back, so no SLA clock has started on them.
    /// </summary>
    [Fact]
    public void NullTrackingCodeIsNotAShipment()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received")),
            templateB: TemplateB(
                new TemplateBRow("321097726", TrackingCode: "NULL", ShipDate: DaysAgo(30)),
                new TemplateBRow("321097726", TrackingCode: "", ShipDate: DaysAgo(30))));

        Assert.Empty(data.Rows);
    }

    [Fact]
    public void TemplateBUsesItsOwnFullOrderNumberWhenItHasOne()
    {
        var data = Build(
            Orders(
                new OrderRow("01259_321097726-A", "Received", SellerId: "11111.0"),
                new OrderRow("01259_321097726-B", "Canceled", SellerId: "22222.0")),
            templateB: TemplateB(new TemplateBRow("321097726",
                MarketPlaceId: "01259_321097726-B", ShipDate: DaysAgo(20))));

        var row = Assert.Single(data.Rows);
        Assert.Equal("01259_321097726-B", row.OrderNumber);
        Assert.True(row.IsConfirmedReturn);
    }

    /// <summary>
    /// The templates write the day first. Read month-first, 12.08 becomes 8 December — a date in the
    /// future, and an elapsed-days figure that is nonsense.
    /// </summary>
    [Fact]
    public void TemplateDatesAreReadDayFirst()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received")),
            TemplateA(new TemplateARow("321097726", RequestDate: "12.08.2026")));

        Assert.Equal("2026-08-12", Assert.Single(data.Rows).ShippedToSellerDate);
    }

    [Fact]
    public void RowWithoutAShipDateHasNoSlaVerdict()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received")),
            TemplateA(new TemplateARow("321097726", RequestDate: "")));

        var row = Assert.Single(data.Rows);
        Assert.Null(row.ElapsedDays);
        Assert.False(row.SlaMissed);
        Assert.False(row.PastWarning);
    }

    // -----------------------------------------------------------------
    // Refund times
    // -----------------------------------------------------------------

    [Fact]
    public void RefundTimeIsMeasuredPerOrderLineFromTheOrdersFileAlone()
    {
        var data = Build(
            Orders(
                new OrderRow("01259_321097726-A", "Canceled",
                    DateCreated: "2026-07-01 10:00:00", DebitDate: "2026-07-08 10:00:00"),
                new OrderRow("01259_321097727-A", "Received",
                    DateCreated: "2026-07-01 10:00:00", DebitDate: "2026-07-03 10:00:00")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(3))));

        var payment = Assert.Single(data.Payments);
        Assert.Equal("01259_321097726-A", payment.OrderNumber);
        Assert.Equal(7, payment.PaymentDays);
    }

    // -----------------------------------------------------------------
    // Consistency of the whole set
    // -----------------------------------------------------------------

    [Fact]
    public void EveryRowLandsInExactlyOneBucket()
    {
        var data = Build(
            Orders(
                new OrderRow("01259_100000001-A", "Received"),
                new OrderRow("01259_100000002-A", "Canceled"),
                new OrderRow("01259_100000003-A", "Received")),
            TemplateA(
                new TemplateARow("100000001", RequestDate: DaysAgo(20)),   // breach
                new TemplateARow("100000002", RequestDate: DaysAgo(20)),   // completed
                new TemplateARow("100000003", RequestDate: DaysAgo(12)),   // warning
                new TemplateARow("999999999", RequestDate: DaysAgo(20)))); // not found

        Assert.Equal(4, data.Rows.Count);
        Assert.Single(data.Rows, r => r.SlaMissed);
        Assert.Single(data.Rows, r => r.PastWarning);
        Assert.Single(data.Rows, r => r.IsConfirmedReturn);
        Assert.Single(data.Rows, r => r.MatchState == ReturnSlaReportBuilder.NotFoundState);

        // A row is never counted as both overdue and merely warned about.
        Assert.DoesNotContain(data.Rows, r => r.SlaMissed && r.PastWarning);
        // Nor is a completed return ever flagged.
        Assert.DoesNotContain(data.Rows, r => r.IsConfirmedReturn && (r.SlaMissed || r.PastWarning));
    }

    [Fact]
    public void BothTemplatesCanBeReadTogetherAndEitherMayBeLeftOut()
    {
        var orders = Orders(
            new OrderRow("01259_100000001-A", "Received"),
            new OrderRow("01259_100000002-A", "Received"));

        var a = TemplateA(new TemplateARow("100000001", RequestDate: DaysAgo(20)));
        var b = TemplateB(new TemplateBRow("100000002", ShipDate: DaysAgo(20)));

        Assert.Equal(2, Build(orders, a, b).Rows.Count);
        Assert.Single(Build(orders, a).Rows);
        Assert.Single(Build(orders, templateB: b).Rows);
    }
}
