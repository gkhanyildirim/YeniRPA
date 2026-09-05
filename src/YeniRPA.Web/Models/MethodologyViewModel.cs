using YeniRPA.Web.Services;

namespace YeniRPA.Web.Models;

/// <summary>
/// Backs the Methodology page. Every value here is read from the code that actually performs the
/// calculation rather than retyped into the view, so the documentation cannot quietly drift away
/// from the report. When a rule changes in a builder, this page changes with it.
/// </summary>
public sealed class MethodologyViewModel
{
    public IReadOnlyList<string> RequiredColumns { get; init; } = OrderReportBuilder.RequiredColumns;
    public IReadOnlyList<string> OptionalColumns { get; init; } = OrderReportBuilder.OptionalColumns;
    public IReadOnlyList<string> IntegratedCarrierKeywords { get; init; } = OrderReportBuilder.IntegratedCarrierKeywords;

    /// <summary>The carrier names free-text shipping companies are folded onto, in catalogue order.</summary>
    public IReadOnlyList<string> CanonicalCarriers { get; init; } = [.. CarrierNames.Catalog.Select(c => c.Name)];
    public IReadOnlyList<CancellationReasonLabel> ReasonLabels { get; init; } = OrderReportBuilder.ReasonLabels;

    public string CanceledStatus { get; init; } = OrderReportBuilder.CanceledStatus;
    public string RefundedStatus { get; init; } = OrderReportBuilder.RefundedStatus;
    public string ReceivedStatus { get; init; } = OrderReportBuilder.ReceivedStatus;
    public string RejectedStatus { get; init; } = OrderReportBuilder.RejectedStatus;
    public string AutoReceivedReason { get; init; } = OrderReportBuilder.AutoReceivedReason;

    public int ReturnSlaDays { get; init; } = ReturnSlaReportBuilder.SlaDays;
    public int ReturnWarningDays { get; init; } = ReturnSlaReportBuilder.WarningDays;

    public IReadOnlyList<string> IncidentColumns { get; init; } = IncidentsReportBuilder.RequiredColumns;
    public int IncidentWarningDays { get; init; } = IncidentsReportBuilder.WarningDays;
    public int IncidentBreachDays { get; init; } = IncidentsReportBuilder.BreachDays;
    public int IncidentStaleDays { get; init; } = IncidentsReportBuilder.StaleDays;
    public int IncidentHotThreadMessages { get; init; } = IncidentsReportBuilder.HotThreadMessages;
    public int IncidentMinSampleSize { get; init; } = IncidentsReportBuilder.MinSampleSize;

    /// <summary>The date the closed incident export is reported from, as the dashboard pre-fills it.</summary>
    public string IncidentClosedFrom { get; init; } =
        IncidentsReportBuilder.ClosedFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The mailbox domain that marks an incident action as the operator's rather than a seller's.</summary>
    public string IncidentOperatorMailDomain { get; init; } = IncidentsReportBuilder.OperatorMailDomain;

    public int MinSampleSize { get; init; } = OrderReportBuilder.MinSampleSize;
    public int MinLeadTimeSample { get; init; } = OrderReportBuilder.MinLeadTimeSample;

    public string CarrierKeywordList => string.Join(", ", IntegratedCarrierKeywords);
    public string CanonicalCarrierList => string.Join(", ", CanonicalCarriers);
}
