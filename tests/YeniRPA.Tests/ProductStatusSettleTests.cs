using YeniRPA.Web.Models;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Tests;

/// <summary>
/// When a read of Mirakl's status dropdown may be believed.
///
/// <para>The dropdown renders every count at "(0)" the instant it opens and fills the real figures in
/// a moment later, so deciding this wrongly is what put zeroes in the table: the old rule retried only
/// while <em>every</em> figure was zero, which meant a dropdown caught halfway through filling in was
/// accepted on the spot. The decision is kept free of Playwright so that sequence — the one the browser
/// is expensive to reproduce — can be replayed here.</para>
/// </summary>
public class ProductStatusSettleTests
{
    static List<ProductStatusRow> Read(params (string Label, int Count)[] items) =>
        [.. items.Select(i => new ProductStatusRow("Seller A", i.Label, i.Count))];

    /// <summary>Replays a run of reads through the loop's own line, and reports the read it settled on.</summary>
    static IReadOnlyList<ProductStatusRow>? Settle(params IReadOnlyList<ProductStatusRow>[] reads)
    {
        var stable = 0;
        IReadOnlyList<ProductStatusRow>? previous = null;

        foreach (var read in reads)
        {
            stable = ProductStatusRunner.NextStableReadCount(read, previous, stable);
            previous = read;

            if (ProductStatusRunner.CountsHaveLanded(read, stable))
                return read;
        }

        return null;
    }

    [Fact]
    public void AnAllZeroDropdownNeverSettlesNoMatterHowLongItIsWatched()
    {
        // The state the page is in before the figures arrive. Steady, but not an answer.
        var zeroes = Read(("Online", 0), ("Taslak", 0));

        Assert.Null(Settle(zeroes, zeroes, zeroes, zeroes, zeroes, zeroes));
    }

    [Fact]
    public void TheFirstReadWithFiguresInItIsNotBelievedOnItsOwn()
    {
        // The regression this whole change exists for: one look at a dropdown that has started filling
        // in says nothing about the statuses still showing zero.
        Assert.Null(Settle(Read(("Online", 1204), ("Taslak", 0))));
    }

    [Fact]
    public void SettlesOnceTheSameFiguresComeBackEnoughTimes()
    {
        var read = Read(("Online", 1204), ("Taslak", 17));

        Assert.Equal(read, Settle(read, read, read));
    }

    [Fact]
    public void AFigureLandingLateRestartsTheAgreementAndTheLaterFiguresWin()
    {
        // "Taslak" arrives after "Online" does. The old rule could not see this at all, because the
        // first read was already not all-zero.
        var partial = Read(("Online", 1204), ("Taslak", 0));
        var full = Read(("Online", 1204), ("Taslak", 17));

        Assert.Null(Settle(partial, partial, full, full));
        Assert.Equal(full, Settle(partial, partial, full, full, full));
    }

    [Fact]
    public void AnAllZeroReadPartWayThroughBreaksTheRun()
    {
        var read = Read(("Online", 1204));
        var zeroes = Read(("Online", 0));

        Assert.Null(Settle(read, read, zeroes, read, read));
    }

    [Fact]
    public void AReadThatFoundNothingIsNeverSettled()
    {
        // The dropdown is on screen but its items have not rendered yet.
        Assert.Null(Settle(Read(), Read(), Read(), Read()));
    }

    [Fact]
    public void ADropdownStillGainingStatusesHasNotSettled()
    {
        // Same figures for the statuses they share, but the page is still adding rows.
        var two = Read(("Online", 1204), ("Taslak", 17));
        var three = Read(("Online", 1204), ("Taslak", 17), ("Reddedildi", 3));

        Assert.False(ProductStatusRunner.SameCounts(three, two));
    }

    [Fact]
    public void ReorderedStatusesCountAsAChange()
    {
        Assert.False(ProductStatusRunner.SameCounts(
            Read(("Taslak", 17), ("Online", 1204)),
            Read(("Online", 1204), ("Taslak", 17))));
    }

    [Fact]
    public void TheFirstReadHasNothingToAgreeWith()
    {
        Assert.False(ProductStatusRunner.SameCounts(Read(("Online", 1204)), null));
    }

    [Fact]
    public void AZeroIsStillAnAnswerAsLongAsSomethingElseIsNot()
    {
        // A seller with products but genuinely no drafts. The zero here is real and has to survive.
        var read = Read(("Online", 1204), ("Taslak", 0));

        Assert.True(ProductStatusRunner.IsUsableRead(read));
        Assert.Equal(read, Settle(read, read, read));
    }
}
