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
    public IReadOnlyList<CancellationReasonLabel> ReasonLabels { get; init; } = OrderReportBuilder.ReasonLabels;

    public string CanceledStatus { get; init; } = OrderReportBuilder.CanceledStatus;
    public string RefundedStatus { get; init; } = OrderReportBuilder.RefundedStatus;
    public string ReceivedStatus { get; init; } = OrderReportBuilder.ReceivedStatus;
    public string RejectedStatus { get; init; } = OrderReportBuilder.RejectedStatus;
    public string AutoReceivedReason { get; init; } = OrderReportBuilder.AutoReceivedReason;

    public int ReturnSlaDays { get; init; } = ReturnSlaReportBuilder.SlaDays;
    public int ReturnWarningDays { get; init; } = ReturnSlaReportBuilder.WarningDays;

    public int MinSampleSize { get; init; } = OrderReportBuilder.MinSampleSize;
    public int MinLeadTimeSample { get; init; } = OrderReportBuilder.MinLeadTimeSample;

    public string CarrierKeywordList => string.Join(", ", IntegratedCarrierKeywords);
}
