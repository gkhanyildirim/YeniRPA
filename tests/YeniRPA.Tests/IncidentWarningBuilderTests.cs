using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// Who gets chased about an unanswered incident, and who deliberately does not.
///
/// <para>The rule has two halves that are easy to get backwards, and both are pinned here: the clock
/// is the <em>incident's</em> open date rather than the order's, and a <c>resolved</c> incident is one
/// <b>we</b> owe rather than the seller — messaging its seller warns the wrong party.</para>
/// </summary>
public class IncidentWarningBuilderTests
{
    /// <summary>The format IncidentsReportBuilder writes and the browser posts back.</summary>
    const string Display = "yyyy-MM-dd HH:mm";

    static string DaysAgo(double days) => DateTime.Now.AddDays(-days).ToString(Display);

    static IncidentWarningInputRow Row(
        string seller = "Prodesk",
        string orderNumber = "01259_326674352-A",
        double openedDaysAgo = 5,
        string lifecycle = IncidentLifecycle.Open,
        string waitingOn = IncidentWaitingOn.Seller,
        string reason = "Defective item",
        string status = "Incident in progress",
        string? openedOn = null) => new(
        Seller: seller,
        OrderNumber: orderNumber,
        OpenedOn: openedOn ?? DaysAgo(openedDaysAgo),
        Lifecycle: lifecycle,
        WaitingOn: waitingOn,
        Reason: reason,
        Status: status);

    static SellerGroupMap Map(params (string Name, string Group)[] entries) =>
        SellerGroupMap.FromEntries(entries.Select(e => new SellerGroupEntry("", e.Name, e.Group)));

    static IncidentWarningData Build(
        IReadOnlyList<IncidentWarningInputRow> rows, int threshold = 2, SellerGroupMap? map = null) =>
        IncidentWarningBuilder.Build(rows, threshold, map ?? Map(("Prodesk", "MediaMarkt - Prodesk")));

    // -----------------------------------------------------------------
    // The eligibility rule
    // -----------------------------------------------------------------

    [Fact]
    public void AnOpenIncidentWaitingOnTheSellerAndPastTheThresholdIsChased()
    {
        var data = Build([Row(openedDaysAgo: 5)]);

        var seller = Assert.Single(data.Sellers);
        Assert.Equal("Prodesk", seller.SellerName);
        Assert.Equal("MediaMarkt - Prodesk", seller.GroupName);
        Assert.Equal(1, seller.IncidentCount);
        Assert.Equal(1, data.Funnel.Eligible);
    }

    /// <summary>
    /// The single most important case in this file. A resolved incident is one the seller has already
    /// answered and written a closing reason for; the verification and the closure are ours. Chasing
    /// its seller warns the party that is not holding anything up.
    /// </summary>
    [Fact]
    public void AResolvedIncidentIsNeverChased()
    {
        var data = Build([Row(lifecycle: IncidentLifecycle.Resolved, waitingOn: IncidentWaitingOn.Us, openedDaysAgo: 40)]);

        Assert.Empty(data.Sellers);
        Assert.Equal(1, data.Funnel.ResolvedAwaitingUs);
        Assert.Equal(0, data.Funnel.Eligible);
    }

    [Fact]
    public void AClosedIncidentIsNeverChased()
    {
        var data = Build([Row(lifecycle: IncidentLifecycle.Closed, waitingOn: IncidentWaitingOn.None, openedDaysAgo: 40)]);

        Assert.Empty(data.Sellers);
        Assert.Equal(1, data.Funnel.Closed);
    }

    /// <summary>On an open incident, only "the customer spoke last" makes the seller the one who owes
    /// a reply. The other states are the seller's turn already taken, or ours.</summary>
    [Theory]
    [InlineData(IncidentWaitingOn.Customer)]
    [InlineData(IncidentWaitingOn.OperatorActed)]
    [InlineData(IncidentWaitingOn.None)]
    [InlineData(IncidentWaitingOn.Us)]
    public void AnOpenIncidentWaitingOnAnyoneButTheSellerIsNotChased(string waitingOn)
    {
        var data = Build([Row(waitingOn: waitingOn, openedDaysAgo: 40)]);

        Assert.Empty(data.Sellers);
        Assert.Equal(1, data.Funnel.WaitingOnOther);
    }

    /// <summary>An allow-list, not a deny-list: a lifecycle or a waiting-on value this app has never
    /// seen must not default into "message the seller".</summary>
    [Theory]
    [InlineData("something-new", IncidentWaitingOn.Seller)]
    [InlineData("", IncidentWaitingOn.Seller)]
    [InlineData(IncidentLifecycle.Open, "someone-else")]
    [InlineData(IncidentLifecycle.Open, "")]
    public void AnUnrecognisedStateIsNotChased(string lifecycle, string waitingOn)
    {
        var data = Build([Row(lifecycle: lifecycle, waitingOn: waitingOn, openedDaysAgo: 40)]);
        Assert.Empty(data.Sellers);
    }

    // -----------------------------------------------------------------
    // The threshold
    // -----------------------------------------------------------------

    [Fact]
    public void AnIncidentYoungerThanTheThresholdIsNotChased()
    {
        var data = Build([Row(openedDaysAgo: 0.5)], threshold: 2);

        Assert.Empty(data.Sellers);
        Assert.Equal(1, data.Funnel.BelowThreshold);
    }

    /// <summary>The comparison is <c>&gt;=</c>: an incident that has just reached the threshold is in,
    /// not one render away from it.</summary>
    [Fact]
    public void AnIncidentExactlyAtTheThresholdIsChased()
    {
        var data = Build([Row(openedDaysAgo: 2.01)], threshold: 2);
        Assert.Single(data.Sellers);
    }

    [Fact]
    public void RaisingTheThresholdShrinksTheList()
    {
        IncidentWarningInputRow[] rows =
        [
            Row(orderNumber: "A-1", openedDaysAgo: 3),
            Row(orderNumber: "A-2", openedDaysAgo: 10),
        ];

        Assert.Equal(2, Build(rows, threshold: 2).Funnel.Eligible);
        Assert.Equal(1, Build(rows, threshold: 7).Funnel.Eligible);
        Assert.Equal(0, Build(rows, threshold: 30).Funnel.Eligible);
    }

    [Theory]
    [InlineData(0, IncidentWarningBuilder.MinThresholdDays)]
    [InlineData(-5, IncidentWarningBuilder.MinThresholdDays)]
    [InlineData(9999, IncidentWarningBuilder.MaxThresholdDays)]
    [InlineData(7, 7)]
    public void TheThresholdIsClampedAndReportedAsApplied(int asked, int expected)
    {
        Assert.Equal(expected, Build([Row()], threshold: asked).ThresholdDays);
    }

    /// <summary>
    /// The whole point of the module: the clock is the incident's, not the order's. A complaint raised
    /// this morning about a year-old purchase is not something to chase, and the builder never sees an
    /// order date at all — this pins that the input shape carries no way to get it wrong.
    /// </summary>
    [Fact]
    public void AFreshIncidentOnAnOldOrderIsNotChased()
    {
        var data = Build([Row(openedDaysAgo: 0.1)], threshold: 2);
        Assert.Empty(data.Sellers);
    }

    // -----------------------------------------------------------------
    // Unreadable dates
    // -----------------------------------------------------------------

    /// <summary>Never fall through to "assume it is old". Telling a seller they sat on a complaint for
    /// four days off a date we could not read is what ends this channel's credibility.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void AnUnreadableOpenedOnIsSetAsideForReviewRatherThanChased(string openedOn)
    {
        var data = Build([Row(openedOn: openedOn)]);

        Assert.Empty(data.Sellers);
        Assert.Equal(1, data.Funnel.NoOpenedDate);
        Assert.Single(data.Review);
    }

    // -----------------------------------------------------------------
    // The funnel
    // -----------------------------------------------------------------

    /// <summary>Every bucket is terminal, so they account for the whole input exactly once. A row that
    /// slipped out of every count would be a seller quietly never chased.</summary>
    [Fact]
    public void TheFunnelBucketsSumToTheRowsThatCameIn()
    {
        IncidentWarningInputRow[] rows =
        [
            Row(orderNumber: "A-1", openedDaysAgo: 5),
            Row(orderNumber: "A-2", openedDaysAgo: 0.2),
            Row(orderNumber: "A-3", lifecycle: IncidentLifecycle.Closed, waitingOn: IncidentWaitingOn.None),
            Row(orderNumber: "A-4", lifecycle: IncidentLifecycle.Resolved, waitingOn: IncidentWaitingOn.Us),
            Row(orderNumber: "A-5", waitingOn: IncidentWaitingOn.Customer),
            Row(orderNumber: "A-6", openedOn: "not a date"),
        ];

        var f = Build(rows).Funnel;

        Assert.Equal(6, f.RowsIn);
        Assert.Equal(
            f.RowsIn,
            f.Closed + f.ResolvedAwaitingUs + f.WaitingOnOther + f.NoOpenedDate + f.BelowThreshold + f.Eligible);
    }

    // -----------------------------------------------------------------
    // Grouping and mapping
    // -----------------------------------------------------------------

    /// <summary>One order can raise several incidents; each is a separate complaint to answer. This is
    /// where the module diverges from Late Order Warnings, which collapses its rows by order.</summary>
    [Fact]
    public void TwoIncidentsOnOneOrderStayTwoLines()
    {
        var data = Build([
            Row(orderNumber: "A-1", reason: "Defective item", openedDaysAgo: 5),
            Row(orderNumber: "A-1", reason: "Missing accessory", openedDaysAgo: 4),
        ]);

        var seller = Assert.Single(data.Sellers);
        Assert.Equal(2, seller.IncidentCount);
        Assert.Equal(2, seller.Incidents.Count);
    }

    [Fact]
    public void IncidentsAreListedOldestFirst()
    {
        var data = Build([
            Row(orderNumber: "NEW", openedDaysAgo: 3),
            Row(orderNumber: "OLD", openedDaysAgo: 20),
        ]);

        var seller = Assert.Single(data.Sellers);
        Assert.Equal("OLD", seller.Incidents[0].OrderNumber);
        Assert.True(seller.MaxAgeDays > 19);
    }

    /// <summary>A mid-period rebrand, or a dotted/dotless i, must not split one seller into two
    /// messages to the same group.</summary>
    [Fact]
    public void TwoSpellingsOfOneNameAreOneSeller()
    {
        var data = Build(
            [Row(seller: "FırsatKurdu", orderNumber: "A-1"), Row(seller: "FIRSATKURDU", orderNumber: "A-2")],
            map: Map(("FırsatKurdu", "MediaMarkt - Fırsat")));

        var seller = Assert.Single(data.Sellers);
        Assert.Equal(2, seller.IncidentCount);
        Assert.Equal("MediaMarkt - Fırsat", seller.GroupName);
        Assert.Contains(data.Warnings, w => w.Contains("more than one name", StringComparison.Ordinal));
    }

    /// <summary>An unmapped seller stays in the list so the panel can show them, with no group and a
    /// reason. There is deliberately no separate collection for them.</summary>
    [Fact]
    public void AnUnmappedSellerIsListedWithAProblemAndNoGroup()
    {
        var data = Build([Row(seller: "Nobody")], map: Map(("Prodesk", "MediaMarkt - Prodesk")));

        var seller = Assert.Single(data.Sellers);
        Assert.Null(seller.GroupName);
        Assert.NotNull(seller.MappingProblem);
        Assert.False(seller.MappingConflict);
        Assert.Equal(1, data.Funnel.UnmappedSellers);
        Assert.Equal(0, data.Funnel.NameConflictSellers);
    }

    /// <summary>
    /// The failure this module's operator will actually hit. The incident export carries no seller-id
    /// column, so a name mapped twice cannot be disambiguated the way Late Order Warnings would — and
    /// it is counted apart from "never mapped" because the fix is the opposite one: remove a row.
    /// </summary>
    [Fact]
    public void ANameMappedTwiceIsReportedAsAConflictNotAsUnmapped()
    {
        var data = Build(
            [Row(seller: "Prodesk")],
            map: Map(("Prodesk", "Group A"), ("Prodesk", "Group B")));

        var seller = Assert.Single(data.Sellers);
        Assert.Null(seller.GroupName);
        Assert.True(seller.MappingConflict);
        Assert.Contains("more than once", seller.MappingProblem!, StringComparison.Ordinal);
        Assert.Equal(1, data.Funnel.NameConflictSellers);
    }

    /// <summary>Mapping rows entered by seller id alone cannot match an incident, because the export
    /// has no id to match them on. It must read as unmapped rather than resolve to something.</summary>
    [Fact]
    public void AMappingRowWithOnlyASellerIdDoesNotMatchAnIncident()
    {
        var map = SellerGroupMap.FromEntries([new SellerGroupEntry("11835", "", "MediaMarkt - Prodesk")]);
        var data = Build([Row(seller: "Prodesk")], map: map);

        Assert.Null(Assert.Single(data.Sellers).GroupName);
    }

    [Fact]
    public void SellersAreSortedByTheirOldestIncidentFirst()
    {
        var data = Build(
            [Row(seller: "Prodesk", openedDaysAgo: 3), Row(seller: "Fressi", openedDaysAgo: 30)],
            map: Map(("Prodesk", "G1"), ("Fressi", "G2")));

        Assert.Equal("Fressi", data.Sellers[0].SellerName);
    }

    [Fact]
    public void AnEmptyInputProducesAnEmptyReportRatherThanThrowing()
    {
        var data = Build([]);

        Assert.Empty(data.Sellers);
        Assert.Equal(0, data.Funnel.RowsIn);
    }
}
