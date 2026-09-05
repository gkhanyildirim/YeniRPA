using System.Text;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using Xunit;

namespace YeniRPA.Tests;

public class IncidentsReportTests
{
    // ---------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------

    [Fact]
    public void ReadsSemicolonCsvAndKeepsTurkishCharacters()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(
            new IncidentFiles.Row(Seller: "Gazioğlu DTM", CustomerName: "Şükrü Şengöz")));

        var row = Assert.Single(data.Rows);
        Assert.Equal("Gazioğlu DTM", row.Seller);
        Assert.Equal("Şükrü Şengöz", row.CustomerName);
    }

    /// <summary>
    /// The closed export writes one closing reason quoted, because it carries a trailing space. It has
    /// to arrive as a value rather than shifting every column after it.
    /// </summary>
    [Fact]
    public void ReadsAQuotedClosingReasonWithoutShiftingTheRow()
    {
        var data = IncidentFiles.Build(null, IncidentFiles.File(
            new IncidentFiles.Row(
                Status: "Closed",
                ClosedBy: "Seller",
                ClosedByUser: "seller@example.com",
                ClosedOn: "09/03/2026 10:00:00 AM",
                ClosingReason: "\"Replacement item sent \"",
                LastAction: "Manual closure")));

        var row = Assert.Single(data.Rows);
        Assert.Equal("Replacement item sent", row.ClosingReason);

        // The columns on the far side of the quoted field: the one straight after it, and the last one
        // on the line. Both landing correctly is what proves the quote closed where it should.
        Assert.Equal("165612306", row.ProductSku);
        Assert.Equal("Manual closure", row.LastAction);
    }

    [Fact]
    public void BothFilesAreReadIntoOneRowSet()
    {
        var data = IncidentFiles.Build(
            IncidentFiles.File(new IncidentFiles.Row(OrderNumber: "01259_1-A")),
            IncidentFiles.File(
                new IncidentFiles.Row(OrderNumber: "01259_2-A", Status: "Closed",
                    ClosedOn: "09/03/2026 10:00:00 AM", ClosingReason: "Refunded"),
                new IncidentFiles.Row(OrderNumber: "01259_3-A", Status: "Closed",
                    ClosedOn: "09/03/2026 11:00:00 AM", ClosingReason: "Refunded")));

        Assert.Equal(1, data.OpenFileRows);
        Assert.Equal(2, data.ClosedFileRows);
        Assert.Equal(3, data.Rows.Count);
        Assert.Equal(IncidentSource.Open, data.Rows[0].Source);
        Assert.Equal(IncidentSource.Closed, data.Rows[1].Source);
    }

    [Fact]
    public void MissingColumnNamesTheColumnAndTheFile()
    {
        var csv = "Order number;Seller;Status\n01259_1-A;Test;Closed";

        var error = Assert.Throws<InvalidOperationException>(() => IncidentFiles.Build(csv));

        Assert.Contains("Order created on", error.Message);
        Assert.Contains("open incidents", error.Message);
    }

    [Fact]
    public void EmptyUploadsAreRejected()
    {
        var header = IncidentFiles.File();

        var error = Assert.Throws<InvalidOperationException>(() => IncidentFiles.Build(header));

        Assert.Contains("No incident rows", error.Message);
    }

    [Fact]
    public void BlankTrailingLinesAreSkippedRatherThanReportedAsIncidents()
    {
        var csv = IncidentFiles.File(new IncidentFiles.Row()) + "\n" + new string(';', 22);

        var data = IncidentFiles.Build(csv);

        Assert.Single(data.Rows);
    }

    // ---------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------

    [Fact]
    public void AClosingDateMakesTheIncidentClosed()
    {
        var data = IncidentFiles.Build(null, IncidentFiles.File(new IncidentFiles.Row(
            Status: "Closed", ClosedOn: "09/03/2026 10:00:00 AM", ClosingReason: "Refunded")));

        Assert.Equal(IncidentLifecycle.Closed, data.Rows[0].Lifecycle);
        Assert.Null(data.Rows[0].AgeDays);
    }

    /// <summary>
    /// "Resolved by seller" arrives with a closing reason and no closing date. It is neither open
    /// backlog nor closed, and collapsing it into either one hides a real state.
    /// </summary>
    [Fact]
    public void AClosingReasonWithoutADateIsResolvedRatherThanClosed()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row(
            Status: "Resolved by seller",
            ClosingReason: "Customer Service handled by the seller",
            LastAction: "Resolution by seller")));

        Assert.Equal(IncidentLifecycle.Resolved, data.Rows[0].Lifecycle);
        Assert.NotNull(data.Rows[0].AgeDays);
    }

    [Fact]
    public void AnythingElseIsOpen()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row(Status: "New incident")));

        Assert.Equal(IncidentLifecycle.Open, data.Rows[0].Lifecycle);
    }

    /// <summary>
    /// Lifecycle must not be read off the status text: Mirakl extends that vocabulary whenever it adds
    /// a state, and a report keyed on today's strings would silently mis-bucket tomorrow's.
    /// </summary>
    [Fact]
    public void AnUnknownStatusStillGetsTheRightLifecycleFromItsDates()
    {
        var data = IncidentFiles.Build(null, IncidentFiles.File(new IncidentFiles.Row(
            Status: "Some status Mirakl has not invented yet",
            ClosedOn: "09/03/2026 10:00:00 AM",
            ClosingReason: "Order delivered")));

        Assert.Equal(IncidentLifecycle.Closed, data.Rows[0].Lifecycle);
    }

    // ---------------------------------------------------------------------------
    // Day arithmetic
    // ---------------------------------------------------------------------------

    [Fact]
    public void AgeIsMeasuredFromTheOpeningDateToNow()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(
            new IncidentFiles.Row(OpenedOn: IncidentFiles.DaysAgo(9), LastActionDate: IncidentFiles.DaysAgo(1))));

        Assert.Equal(9, data.Rows[0].AgeDays!.Value, 1);
    }

    [Fact]
    public void SilenceIsMeasuredFromTheLastActionToNow()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(
            new IncidentFiles.Row(OpenedOn: IncidentFiles.DaysAgo(9), LastActionDate: IncidentFiles.DaysAgo(4))));

        Assert.Equal(4, data.Rows[0].SilenceDays!.Value, 1);
    }

    [Fact]
    public void ResolutionTimeIsOpenedToClosed()
    {
        var data = IncidentFiles.Build(null, IncidentFiles.File(new IncidentFiles.Row(
            Status: "Closed",
            OpenedOn: "09/01/2026 09:00:00 AM",
            ClosedOn: "09/06/2026 09:00:00 PM",
            ClosingReason: "Refunded")));

        Assert.Equal(5.5, data.Rows[0].ResolutionDays!.Value, 1);
    }

    [Fact]
    public void OrderToIncidentLagIsOrderCreatedToOpened()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row(
            OrderCreatedOn: "08/25/2026 09:00:00 AM",
            OpenedOn: "09/01/2026 09:00:00 AM")));

        Assert.Equal(7, data.Rows[0].OrderToIncidentDays!.Value, 1);
    }

    /// <summary>An unreadable date is left blank and flagged, never guessed at.</summary>
    [Fact]
    public void AnUnreadableDateLeavesTheAgeBlankAndRaisesAnIssue()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row(OpenedOn: "not a date")));

        Assert.Null(data.Rows[0].AgeDays);
        Assert.Contains(IncidentsReportBuilder.IssueUnreadableDate, data.Rows[0].Issues);
        Assert.Contains(data.Warnings, w => w.Contains("could not be read"));
    }

    [Fact]
    public void AnAbsentDateIsNotTreatedAsAnUnreadableOne()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row(OrderCreatedOn: "")));

        Assert.Null(data.Rows[0].OrderToIncidentDays);
        Assert.DoesNotContain(IncidentsReportBuilder.IssueUnreadableDate, data.Rows[0].Issues);
    }

    // ---------------------------------------------------------------------------
    // Who owes the next move
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("Customer", IncidentWaitingOn.Seller)]
    [InlineData("Seller", IncidentWaitingOn.Customer)]
    [InlineData("Operator", IncidentWaitingOn.OperatorActed)]
    public void AnOpenIncidentIsOwedByWhoeverDidNotSpeakLast(string lastActionBy, string expected)
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row(LastActionBy: lastActionBy)));

        Assert.Equal(expected, data.Rows[0].WaitingOn);
    }

    /// <summary>
    /// The handover. Until the seller says it is solved the thread is between the customer and the
    /// seller; from that moment the verification and the closure are ours.
    /// </summary>
    [Fact]
    public void SellerMarkingItResolvedHandsTheIncidentToUs()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row(
            Status: "Resolved by seller",
            ClosingReason: "Customer Service handled by the seller",
            LastAction: "Resolution by seller",
            LastActionBy: "Seller")));

        Assert.Equal(IncidentLifecycle.Resolved, data.Rows[0].Lifecycle);
        Assert.Equal(IncidentWaitingOn.Us, data.Rows[0].WaitingOn);
    }

    /// <summary>
    /// The point of the rule: no open incident is ever our queue, whoever typed the last message. An
    /// earlier version read this off "Last action by" alone and called a waiting customer message ours.
    /// </summary>
    [Theory]
    [InlineData("Customer")]
    [InlineData("Seller")]
    [InlineData("Operator")]
    public void AnOpenIncidentIsNeverOurs(string lastActionBy)
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row(
            Status: "Incident in progress", LastActionBy: lastActionBy)));

        Assert.Equal(IncidentLifecycle.Open, data.Rows[0].Lifecycle);
        Assert.NotEqual(IncidentWaitingOn.Us, data.Rows[0].WaitingOn);
    }

    [Fact]
    public void AClosedIncidentOwesNobodyAnything()
    {
        var data = IncidentFiles.Build(null, IncidentFiles.File(new IncidentFiles.Row(
            Status: "Closed", ClosedOn: "09/03/2026 10:00:00 AM",
            ClosingReason: "Refunded", LastActionBy: "Customer")));

        Assert.Equal(IncidentWaitingOn.None, data.Rows[0].WaitingOn);
    }

    // ---------------------------------------------------------------------------
    // Actor classification
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("Operator", "candanz@media-saturn.com", IncidentActorKind.Internal)]
    [InlineData("Operator", "Operator API", IncidentActorKind.Automation)]
    [InlineData("Seller", "info@powertec.com.tr", IncidentActorKind.Seller)]
    [InlineData("Customer", "", IncidentActorKind.Customer)]
    public void TheUserDecidesWhatKindOfAccountActed(string role, string user, string expected)
    {
        var data = IncidentFiles.Build(IncidentFiles.File(
            new IncidentFiles.Row(LastActionBy: role, LastActionByUser: user)));

        Assert.Equal(expected, data.Rows[0].LastActorKind);
    }

    /// <summary>
    /// A seller mailbox that merely contains the operator's domain is still a seller — the check is on
    /// the domain the address ends with, not on a substring anywhere in it.
    /// </summary>
    [Fact]
    public void ALookalikeDomainIsNotTreatedAsInternal()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(
            new IncidentFiles.Row(LastActionBy: "Seller", LastActionByUser: "media-saturn.com@seller.com.tr")));

        Assert.Equal(IncidentActorKind.Seller, data.Rows[0].LastActorKind);
    }

    [Fact]
    public void ClosedByKindIsBlankWhenNobodyClosedTheIncident()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row()));

        Assert.Null(data.Rows[0].ClosedByKind);
    }

    // ---------------------------------------------------------------------------
    // Order identity
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Order value is a property of the order, so the dashboard sums it over distinct order numbers.
    /// That only works if the split parts of one customer order share a core and keep their own suffix.
    /// </summary>
    [Fact]
    public void SplitOrdersShareACoreAndKeepTheirSuffix()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(
            new IncidentFiles.Row(OrderNumber: "01259_326674352-A"),
            new IncidentFiles.Row(OrderNumber: "01259_326674352-B")));

        Assert.Equal("326674352", data.Rows[0].OrderCore);
        Assert.Equal("326674352", data.Rows[1].OrderCore);
        Assert.Equal("A", data.Rows[0].SplitSuffix);
        Assert.Equal("B", data.Rows[1].SplitSuffix);
    }

    [Fact]
    public void AnOrderNumberWithoutASuffixReportsNone()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row(OrderNumber: "01259_326674352")));

        Assert.Equal("", data.Rows[0].SplitSuffix);
    }

    // ---------------------------------------------------------------------------
    // Data quality
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The export's money columns describe the order, not the incident, and this report is a queue
    /// rather than a financial one. A zero total, a zero quantity and a foreign currency are all
    /// simply not looked at — reporting a problem the operator cannot see on the dashboard would be
    /// worse than not reporting it.
    /// </summary>
    [Fact]
    public void MoneyColumnsAreNotReadAndNeverRaiseAnIssue()
    {
        var data = IncidentFiles.Build(null, IncidentFiles.File(new IncidentFiles.Row(
            Status: "Closed", ClosedOn: "09/03/2026 10:00:00 AM",
            ClosingReason: "Order delivered", Quantity: "0", Amount: "0.00", Currency: "USD")));

        Assert.Empty(data.Rows[0].Issues);
    }

    /// <summary>
    /// And a file that omits those columns entirely still builds: the module requires only what it
    /// reads, so a trimmed export is not an error.
    /// </summary>
    [Fact]
    public void AFileWithoutTheMoneyColumnsStillBuilds()
    {
        var header = string.Join(';', IncidentFiles.Header.Where(h =>
            h != "Quantity" && h != "Currency" &&
            h != "Total order amount incl. VAT (including shipping charges)"));
        var row = string.Join(';',
            "01259_7-A", "08/29/2026 02:50:02 PM", "Ada Yılmaz", "Test Seller", "New incident",
            "2", "Customer", "", "09/01/2026 09:00:00 AM", "Defective item",
            "", "", "", "", "SKU1", "Ürün",
            "Customer", "", "09/02/2026 10:00:00 AM", "Message");

        var data = IncidentFiles.Build(header + "\n" + row);

        Assert.Equal("Test Seller", data.Rows[0].Seller);
        Assert.Equal("Ürün", data.Rows[0].Product);
        Assert.Empty(data.Rows[0].Issues);
    }

    [Fact]
    public void ClosedBeforeOpenedIsFlagged()
    {
        var data = IncidentFiles.Build(null, IncidentFiles.File(new IncidentFiles.Row(
            Status: "Closed",
            OpenedOn: "09/03/2026 10:00:00 AM",
            ClosedOn: "09/01/2026 10:00:00 AM",
            ClosingReason: "Refunded")));

        Assert.Contains(IncidentsReportBuilder.IssueClosedBeforeOpened, data.Rows[0].Issues);
    }

    [Fact]
    public void OpenedBeforeTheOrderExistedIsFlagged()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row(
            OrderCreatedOn: "09/05/2026 10:00:00 AM",
            OpenedOn: "09/01/2026 10:00:00 AM")));

        Assert.Contains(IncidentsReportBuilder.IssueOpenedBeforeOrder, data.Rows[0].Issues);
    }

    [Fact]
    public void AMissingSellerOrOrderNumberIsFlagged()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(
            new IncidentFiles.Row(Seller: "", OrderNumber: "")));

        Assert.Contains(IncidentsReportBuilder.IssueMissingSeller, data.Rows[0].Issues);
        Assert.Contains(IncidentsReportBuilder.IssueMissingOrderNumber, data.Rows[0].Issues);
    }

    [Fact]
    public void ACleanRowRaisesNothing()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row()));

        Assert.Empty(data.Rows[0].Issues);
        Assert.DoesNotContain(data.Warnings, w => w.Contains("could not be read"));
    }

    [Fact]
    public void ARowThatContradictsItsFileIsFlagged()
    {
        var data = IncidentFiles.Build(
            IncidentFiles.File(new IncidentFiles.Row(
                Status: "Closed", ClosedOn: "09/03/2026 10:00:00 AM", ClosingReason: "Refunded")),
            IncidentFiles.File(new IncidentFiles.Row(OrderNumber: "01259_999-A", Status: "New incident")));

        Assert.Contains(IncidentsReportBuilder.IssueClosedRowInOpenFile, data.Rows[0].Issues);
        Assert.Contains(IncidentsReportBuilder.IssueOpenRowInClosedFile, data.Rows[1].Issues);
    }

    /// <summary>
    /// The operator downloads the two exports minutes apart, so an incident closed in between lands in
    /// both. It is reported rather than de-duplicated: which copy is the true one is not ours to pick.
    /// </summary>
    [Fact]
    public void AnIncidentInBothUploadsIsFlaggedOnBothRows()
    {
        const string opened = "09/01/2026 09:00:00 AM";

        var data = IncidentFiles.Build(
            IncidentFiles.File(new IncidentFiles.Row(OrderNumber: "01259_5-A", OpenedOn: opened)),
            IncidentFiles.File(new IncidentFiles.Row(
                OrderNumber: "01259_5-A", OpenedOn: opened, Status: "Closed",
                ClosedOn: "09/04/2026 09:00:00 AM", ClosingReason: "Refunded")));

        Assert.All(data.Rows, r => Assert.Contains(IncidentsReportBuilder.IssueInBothFiles, r.Issues));
        Assert.Contains(data.Warnings, w => w.Contains("both uploads"));
    }

    [Fact]
    public void TheSameOrderWithTwoDifferentIncidentsIsNotADuplicate()
    {
        var data = IncidentFiles.Build(
            IncidentFiles.File(new IncidentFiles.Row(OrderNumber: "01259_5-A", OpenedOn: "09/01/2026 09:00:00 AM")),
            IncidentFiles.File(new IncidentFiles.Row(
                OrderNumber: "01259_5-A", OpenedOn: "08/20/2026 09:00:00 AM", Status: "Closed",
                ClosedOn: "08/25/2026 09:00:00 AM", ClosingReason: "Refunded")));

        Assert.All(data.Rows, r => Assert.DoesNotContain(IncidentsReportBuilder.IssueInBothFiles, r.Issues));
    }

    // ---------------------------------------------------------------------------
    // Payload
    // ---------------------------------------------------------------------------

    [Fact]
    public void ThresholdsTravelWithTheDataSoTheDashboardNeverRestatesThem()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row()));

        Assert.Equal(IncidentsReportBuilder.WarningDays, data.WarningDays);
        Assert.Equal(IncidentsReportBuilder.BreachDays, data.BreachDays);
        Assert.Equal(IncidentsReportBuilder.StaleDays, data.StaleDays);
        Assert.Equal(IncidentsReportBuilder.MinSampleSize, data.MinSampleSize);
        Assert.Equal("2026-09-02", data.ClosedFrom);
    }

    [Fact]
    public void DataAsOfIsTheNewestActionAnywhereInTheUploads()
    {
        var data = IncidentFiles.Build(
            IncidentFiles.File(new IncidentFiles.Row(LastActionDate: "09/01/2026 09:00:00 AM")),
            IncidentFiles.File(new IncidentFiles.Row(
                OrderNumber: "01259_9-A", Status: "Closed",
                ClosedOn: "09/03/2026 04:30:00 PM", ClosingReason: "Refunded",
                LastActionDate: "09/03/2026 04:30:00 PM")));

        Assert.Equal("2026-09-03 16:30", data.DataAsOf);
    }

    [Fact]
    public void DaysAreSentRoundedAndDatesInASortableShape()
    {
        var data = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row(
            OpenedOn: "09/01/2026 09:00:00 AM", LastActionDate: "09/02/2026 10:00:00 AM")));

        var row = data.Rows[0];
        Assert.Equal("2026-09-01 09:00", row.OpenedOn);
        Assert.Equal("2026-09-01", row.OpenedDay);
        Assert.Equal(row.AgeDays, Math.Round(row.AgeDays!.Value, 1));
    }

    // ---------------------------------------------------------------------------
    // Controller-level guard
    // ---------------------------------------------------------------------------

    [Fact]
    public void EitherFileOnItsOwnIsEnough()
    {
        var openOnly = IncidentFiles.Build(IncidentFiles.File(new IncidentFiles.Row()));
        var closedOnly = IncidentFiles.Build(null, IncidentFiles.File(new IncidentFiles.Row(
            Status: "Closed", ClosedOn: "09/03/2026 10:00:00 AM", ClosingReason: "Refunded")));

        Assert.Single(openOnly.Rows);
        Assert.Equal(0, openOnly.ClosedFileRows);
        Assert.Single(closedOnly.Rows);
        Assert.Equal(0, closedOnly.OpenFileRows);
    }

    [Fact]
    public void AnXlsxUploadGoesThroughTheSameReader()
    {
        // The builder picks its reader from the extension, so a file named .xlsx must not be parsed
        // as CSV — the guard here is that the wrong extension fails loudly rather than silently.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(IncidentFiles.File(new IncidentFiles.Row())));

        Assert.ThrowsAny<Exception>(() =>
            IncidentsReportBuilder.BuildData(stream, "incidents.xlsx", null, null));
    }
}
