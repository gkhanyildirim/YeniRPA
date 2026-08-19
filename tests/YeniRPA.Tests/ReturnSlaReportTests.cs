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
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(25))));

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
    public void OpenOrderPastTwentyDaysIsABreach()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(25))));

        var row = Assert.Single(data.Rows);
        Assert.False(row.IsConfirmedReturn);
        Assert.True(row.SlaMissed);
        Assert.False(row.PastWarning);
        Assert.True(row.ElapsedDays >= 25);
    }

    [Fact]
    public void OpenOrderBetweenFifteenAndTwentyDaysIsAWarningNotABreach()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(18))));

        var row = Assert.Single(data.Rows);
        Assert.True(row.PastWarning);
        Assert.False(row.SlaMissed);
    }

    /// <summary>
    /// The thresholds moved from 10/15 to 15/20, so the days on either side of each one are pinned.
    ///
    /// <para>A template date has no time of day, so <c>DaysAgo(n)</c> is midnight and the elapsed
    /// figure lands somewhere in <c>[n, n+1)</c>. The days chosen here are the ones whose whole
    /// interval falls on one side of a threshold — 15 and 20 themselves straddle one and would only
    /// hold at exactly midnight.</para>
    /// </summary>
    [Theory]
    [InlineData(5, false, false)]    // comfortably inside the window
    [InlineData(14, false, false)]   // still inside: elapsed < 15
    [InlineData(16, true, false)]    // past the warning
    [InlineData(19, true, false)]    // last stretch before the breach
    [InlineData(21, false, true)]    // past the SLA
    [InlineData(40, false, true)]
    public void WarningAndBreachThresholdsSitAtFifteenAndTwentyDays(
        int daysAgo, bool expectWarning, bool expectBreach)
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(daysAgo))));

        var row = Assert.Single(data.Rows);
        Assert.Equal(expectWarning, row.PastWarning);
        Assert.Equal(expectBreach, row.SlaMissed);
    }

    /// <summary>
    /// The dashboard labels its buckets from these, rather than repeating the numbers in JavaScript
    /// where they would quietly drift the day the SLA changes again.
    /// </summary>
    [Fact]
    public void EveryRowCarriesTheThresholdsItWasJudgedAgainst()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(5))));

        var row = Assert.Single(data.Rows);
        Assert.Equal(20, row.SlaDays);
        Assert.Equal(15, row.WarningDays);
        Assert.Equal(ReturnSlaReportBuilder.SlaDays, row.SlaDays);
        Assert.Equal(ReturnSlaReportBuilder.WarningDays, row.WarningDays);
    }

    /// <summary>A finished return is not "at risk at 18 days", so it stays out of the warning list.</summary>
    [Fact]
    public void CompletedReturnIsNotInTheWarningList()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Canceled")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(18))));

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
                new TemplateARow("100000001", RequestDate: DaysAgo(25)),   // breach
                new TemplateARow("100000002", RequestDate: DaysAgo(25)),   // completed
                new TemplateARow("100000003", RequestDate: DaysAgo(18)),   // warning
                new TemplateARow("999999999", RequestDate: DaysAgo(25)))); // not found

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

    /// <summary>
    /// The line the dashboard prints under the KPI grid — "N return records = … completed + …
    /// breached + … warned + … open within SLA + … not yet shipped back + … to review" — is a claim
    /// that these six buckets partition the record set. It is checked here on the data the dashboard
    /// is handed, over an upload that populates every one of them.
    /// </summary>
    [Fact]
    public void TheSixDashboardBucketsPartitionTheRecordSet()
    {
        var data = Build(
            Orders(
                new OrderRow("01259_100000001-A", "Received"),
                new OrderRow("01259_100000002-A", "Canceled"),
                new OrderRow("01259_100000003-A", "Received"),
                new OrderRow("01259_100000004-A", "Received"),
                new OrderRow("01259_100000005-A", "Received"),
                new OrderRow("01259_100000006-A", "Received", SellerId: "11111.0"),
                new OrderRow("01259_100000006-B", "Canceled", SellerId: "22222.0")),
            TemplateA(
                new TemplateARow("100000001", RequestDate: DaysAgo(25)),   // breached
                new TemplateARow("100000002", RequestDate: DaysAgo(25)),   // completed
                new TemplateARow("100000003", RequestDate: DaysAgo(18)),   // warned
                new TemplateARow("100000004", RequestDate: DaysAgo(5)),    // open within SLA
                new TemplateARow("100000005", RequestDate: ""),            // not yet shipped back
                new TemplateARow("100000006", RequestDate: DaysAgo(25)),   // ambiguous → review
                new TemplateARow("999999999", RequestDate: DaysAgo(25)))); // not found → review

        var resolved = data.Rows.Where(r =>
            r.MatchState is ReturnSlaReportBuilder.MatchedState
                         or ReturnSlaReportBuilder.MatchedByStatusState).ToList();
        var open = resolved.Where(r => !r.IsConfirmedReturn).ToList();

        var completed = data.Rows.Count(r => r.IsConfirmedReturn);
        var breached = data.Rows.Count(r => r.SlaMissed);
        var warned = data.Rows.Count(r => r.PastWarning);
        var openWithinSla = open.Count(r => r.ElapsedDays is not null && !r.SlaMissed && !r.PastWarning);
        var notStarted = open.Count(r => r.ElapsedDays is null);
        var review = data.Rows.Count - resolved.Count;

        Assert.Equal(7, data.Rows.Count);
        Assert.Equal(1, completed);
        Assert.Equal(1, breached);
        Assert.Equal(1, warned);
        Assert.Equal(1, openWithinSla);
        Assert.Equal(1, notStarted);
        Assert.Equal(2, review);

        // The claim the dashboard makes on screen.
        Assert.Equal(data.Rows.Count,
            completed + breached + warned + openWithinSla + notStarted + review);
    }

    /// <summary>
    /// The buckets have to stay disjoint whatever is uploaded, not only on the tidy fixture above.
    /// A completed return that is also counted as breached would be double-counted on screen and the
    /// consistency line would read MISMATCH.
    /// </summary>
    [Theory]
    [InlineData("Received", 25)]
    [InlineData("Received", 18)]
    [InlineData("Received", 5)]
    [InlineData("Canceled", 25)]
    [InlineData("Refunded", 18)]
    [InlineData("Shipped", 40)]
    public void NoRowIsEverCountedInTwoBucketsAtOnce(string status, int daysAgo)
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", status)),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(daysAgo))));

        var row = Assert.Single(data.Rows);

        Assert.False(row.SlaMissed && row.PastWarning);
        Assert.False(row.IsConfirmedReturn && (row.SlaMissed || row.PastWarning));
    }

    /// <summary>
    /// An unresolved row never carries an SLA verdict, whichever way it failed to resolve: without a
    /// matched order there is no status to be late against, which is the whole reason those rows are
    /// listed for review instead of counted as breaches.
    /// </summary>
    [Fact]
    public void UnresolvedRowsNeverCarryAnSlaVerdict()
    {
        var data = Build(
            Orders(
                new OrderRow("01259_100000001-A", "Received", SellerId: "11111.0"),
                new OrderRow("01259_100000001-B", "Canceled", SellerId: "22222.0")),
            TemplateA(
                new TemplateARow("100000001", RequestDate: DaysAgo(40)),    // ambiguous
                new TemplateARow("999999999", RequestDate: DaysAgo(40))));  // not found

        Assert.Equal(2, data.Rows.Count);
        Assert.All(data.Rows, row =>
        {
            Assert.False(row.SlaMissed);
            Assert.False(row.PastWarning);
            Assert.False(row.IsConfirmedReturn);
        });
    }

    /// <summary>
    /// A row with no ship-back date is open, but its SLA clock has not started — it belongs in its
    /// own bucket rather than being reported as "open within SLA", which would claim it is inside a
    /// window that never began.
    /// </summary>
    [Fact]
    public void RowWithoutAShipDateIsOpenButHasNotStartedTheClock()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received")),
            TemplateA(new TemplateARow("321097726", RequestDate: "")));

        var row = Assert.Single(data.Rows);
        Assert.Equal(ReturnSlaReportBuilder.MatchedState, row.MatchState);
        Assert.False(row.IsConfirmedReturn);
        Assert.Null(row.ElapsedDays);
        Assert.False(row.SlaMissed);
        Assert.False(row.PastWarning);
    }

    /// <summary>
    /// Elapsed days are what every verdict rests on, so they are measured from the ship-back date and
    /// not from anything else on the row — the order's creation date in particular.
    /// </summary>
    [Fact]
    public void ElapsedDaysAreMeasuredFromTheShipBackDate()
    {
        var data = Build(
            Orders(new OrderRow("01259_321097726-A", "Received", DateCreated: "2020-01-01 10:00:00")),
            TemplateA(new TemplateARow("321097726", RequestDate: DaysAgo(7))));

        var elapsed = Assert.Single(data.Rows).ElapsedDays;
        Assert.NotNull(elapsed);
        Assert.InRange(elapsed!.Value, 7, 8);
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
