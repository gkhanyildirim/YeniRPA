using YeniRPA.Web.Services.SalesAnalysis;
using static YeniRPA.Tests.SalesAnalysisFixtures;

namespace YeniRPA.Tests;

/// <summary>
/// <see cref="SalesMetrics"/> against a dataset small enough to add up by hand. Every ratio whose
/// denominator is zero must come back null — never 0, NaN or infinity.
/// </summary>
public class SalesMetricsTests
{
    static PeriodMetrics Compute(IReadOnlyList<SalesLine> all)
    {
        var statuses = DefaultStatuses();
        return SalesMetrics.Compute(all, [.. all.Where(l => statuses.Contains(l.Status))]);
    }

    static readonly SalesLine[] Sample =
    [
        // O1: two lines, order-level shipping 10 repeated on both.
        Line("O1", "A", 2, 200, shipping: 10, commission: 20),
        Line("O1", "B", 1, 50, shipping: 10, commission: 5),
        Line("O2", "A", 1, 100, status: "Shipped", shipping: 5, commission: 10),
        Line("O3", "A", 0, 0, status: "Canceled", canceledAmount: 300),
        Line("O4", "C", 1, 80, status: "Rejected"),
    ];

    [Fact]
    public void Hand_calculated_figures_match()
    {
        var m = Compute(Sample);

        Assert.Equal(350, m.GrossSales);
        Assert.Equal(2, m.Orders);
        Assert.Equal(4, m.Units);
        Assert.Equal(87.5, m.AvgUnitPrice);
        Assert.Equal(175, m.AverageOrderValue);
        Assert.Equal(5, m.Lines);
        Assert.Equal(1, m.CanceledLines);
        Assert.Equal(0.2, m.CancelRateLines);
        Assert.Equal(300, m.CanceledAmount);
        Assert.Equal(300.0 / 730.0, m.CancelRateAmount!.Value, 10);
        Assert.Equal(1, m.RejectedLines);
        Assert.Equal(80, m.RejectedAmount);
        Assert.Equal(35, m.Commission);
        Assert.Equal(0.1, m.CommissionRate!.Value, 10);
    }

    [Fact]
    public void Order_level_shipping_is_counted_once_per_order_not_per_line()
    {
        Assert.Equal(15, Compute(Sample).ShippingRevenue);
    }

    [Fact]
    public void An_empty_period_has_no_ratios()
    {
        var m = Compute([]);

        Assert.Equal(0, m.GrossSales);
        Assert.Equal(0, m.Orders);
        Assert.Null(m.AvgUnitPrice);
        Assert.Null(m.AverageOrderValue);
        Assert.Null(m.CancelRateLines);
        Assert.Null(m.CancelRateAmount);
        Assert.Null(m.CommissionRate);
    }

    [Fact]
    public void A_period_where_every_line_is_canceled_has_no_price_and_a_full_cancel_rate()
    {
        var m = Compute([
            Line("O1", "A", 0, 0, status: "Canceled", canceledAmount: 100),
            Line("O2", "B", 0, 0, status: "Canceled", canceledAmount: 50),
        ]);

        Assert.Equal(0, m.GrossSales);
        Assert.Null(m.AvgUnitPrice);
        Assert.Null(m.AverageOrderValue);
        Assert.Equal(1, m.CancelRateLines);
        Assert.Equal(1, m.CancelRateAmount);
    }

    [Fact]
    public void Zero_quantity_lines_do_not_produce_an_infinite_price()
    {
        var m = Compute([Line("O1", "A", 0, 100)]);

        Assert.Equal(100, m.GrossSales);
        Assert.Null(m.AvgUnitPrice);
        Assert.Equal(100, m.AverageOrderValue);
    }

    [Fact]
    public void A_single_day_period_is_computed_like_any_other()
    {
        var m = Compute([Line("O1", "A", 1, 10, hour: 0), Line("O2", "A", 1, 30, hour: 23)]);

        Assert.Equal(40, m.GrossSales);
        Assert.Equal(20, m.AverageOrderValue);
    }

    [Fact]
    public void Status_filter_decides_what_counts_as_sales_but_cancellations_ignore_it()
    {
        var onlyReceived = new HashSet<string>(["Received"], StringComparer.OrdinalIgnoreCase);
        var m = SalesMetrics.Compute(Sample, [.. Sample.Where(l => onlyReceived.Contains(l.Status))]);

        Assert.Equal(250, m.GrossSales);
        Assert.Equal(1, m.Orders);
        Assert.Equal(1, m.CanceledLines);
    }

    [Theory]
    [InlineData(0.0, 10.0, null)]
    [InlineData(100.0, 120.0, 0.2)]
    [InlineData(-100.0, -50.0, 0.5)]
    public void Change_has_no_percent_when_the_base_is_zero(double a, double b, double? pct)
    {
        var change = MetricChange.Of(a, b);

        Assert.Equal(b - a, change.Abs);
        if (pct is null) Assert.Null(change.Pct);
        else Assert.Equal(pct.Value, change.Pct!.Value, 10);
    }

    [Theory]
    [InlineData(0.33, "%33'ü")]
    [InlineData(0.26, "%26'sı")]
    [InlineData(0.62, "%62'si")]
    [InlineData(0.40, "%40'ı")]
    [InlineData(0.10, "%10'u")]
    [InlineData(1.00, "%100'ü")]
    [InlineData(0.05, "%5'i")]
    public void Percent_takes_the_turkish_suffix_of_its_last_spoken_number(double ratio, string expected)
    {
        Assert.Equal(expected, SalesTextTr.PctOf(ratio));
    }

    [Theory]
    [InlineData(0.301, "%30,1'i")]
    [InlineData(0.05, "%5,0'ı")]
    [InlineData(0.009, "%0,9'u")]
    public void A_decimal_percent_takes_the_suffix_of_its_decimal_part(double ratio, string expected)
    {
        Assert.Equal(expected, SalesTextTr.PctOf(ratio, 1));
    }

    [Theory]
    [InlineData(7_556_305, "7,6 milyon TL")]
    [InlineData(422_910, "423 bin TL")]
    [InlineData(-1_500, "1,5 bin TL")]
    [InlineData(850, "850 TL")]
    public void Sentence_amounts_are_rounded_and_readable(double value, string expected)
    {
        Assert.Equal(expected, SalesTextTr.Tl(value));
    }

    [Fact]
    public void Change_of_a_missing_figure_has_no_difference()
    {
        var change = MetricChange.Of(null, 5);

        Assert.Null(change.Abs);
        Assert.Null(change.Pct);
    }
}
