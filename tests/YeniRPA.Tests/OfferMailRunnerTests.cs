using System.Runtime.Versioning;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Tests;

/// <summary>
/// How a run is cut into passes. The rest of <see cref="OfferMailRunner"/> needs a live Outlook and
/// cannot be tested here, which is exactly why this arithmetic was split out: a run that loses its
/// last few mails to an off-by-one would look like a run that finished, and the operator's only
/// evidence would be the sellers who never complained.
/// </summary>
[SupportedOSPlatform("windows")]
public class OfferMailRunnerTests
{
    static IReadOnlyList<(int Start, int Count)> Plan(int total, int perPass = 250) =>
        OfferMailRunner.PlanPasses(total, perPass);

    [Fact]
    public void NothingToSendIsNoPasses()
    {
        Assert.Empty(Plan(0));
        Assert.Empty(Plan(-1));
    }

    /// <summary>A run that fits stays one pass, so it never pauses and its log reads as it always did.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(249)]
    [InlineData(250)]
    public void ARunThatFitsInOnePassIsNotSplit(int total)
    {
        var passes = Plan(total);

        Assert.Equal(new[] { (0, total) }, passes);
    }

    /// <summary>
    /// One mail over the pass size. The second pass carries that single mail rather than being
    /// skipped — the case where "the last pass is a full one" would silently drop it.
    /// </summary>
    [Fact]
    public void OneOverThePassSizeGetsItsOwnSecondPass()
    {
        Assert.Equal(new[] { (0, 250), (250, 1) }, Plan(251));
    }

    [Fact]
    public void TheLastPassCarriesTheRemainder()
    {
        Assert.Equal(new[] { (0, 250), (250, 150) }, Plan(400));
    }

    /// <summary>An exact multiple must not add an empty pass on the end.</summary>
    [Fact]
    public void AnExactMultipleEndsOnAFullPass()
    {
        Assert.Equal(new[] { (0, 250), (250, 250), (500, 250), (750, 250) }, Plan(1_000));
    }

    /// <summary>
    /// The property that matters more than any single case: every mail is in exactly one pass, the
    /// passes are in order, and there is no gap between them. A run that skipped a range would report
    /// itself as finished having never touched those sellers.
    /// </summary>
    [Theory]
    [InlineData(1, 250)]
    [InlineData(287, 250)]
    [InlineData(500, 250)]
    [InlineData(999, 250)]
    [InlineData(7, 3)]
    [InlineData(9, 3)]
    [InlineData(4, 1)]
    public void ThePassesCoverEveryMailExactlyOnce(int total, int perPass)
    {
        var passes = Plan(total, perPass);

        Assert.Equal(total, passes.Sum(p => p.Count));
        Assert.All(passes, p => Assert.InRange(p.Count, 1, perPass));

        var next = 0;
        foreach (var (start, count) in passes)
        {
            Assert.Equal(next, start);
            next = start + count;
        }

        Assert.Equal(total, next);
    }

    /// <summary>A pass has to carry at least one mail; zero would plan forever.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void APassSizeBelowOneIsRefused(int perPass)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OfferMailRunner.PlanPasses(10, perPass));
    }
}
