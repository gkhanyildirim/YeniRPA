using System.Text.Json;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The hand-entered addresses: the operator's answer to a seller the uploaded list does not name.
/// Getting the lookup wrong here means a seller who was answered stays unanswered, or worse, is
/// answered with a stale address.
///
/// <para>The twin of <see cref="VatMailStoreTests"/>. Both exist rather than one shared fixture for
/// the same reason the two stores do: the rules happen to agree today, and a change to one must fail
/// its own test rather than quietly pass on the other's.</para>
/// </summary>
public class OfferMailStoreTests
{
    static OfferOverrideEntry Entry(string id, string name, string email) => new(id, name, email);

    [Fact]
    public void AnAddressEnteredByHandIsFoundByTheSellersId()
    {
        var overrides = new[] { Entry("13193", "NEXA E-TİCARET", "info@nexaeticaret.com") };

        Assert.Equal("info@nexaeticaret.com", OfferMailStore.FindOverride(overrides, "13193", "Nexa"));
    }

    /// <summary>A row typed in without an id still has to reach its seller, and every human spelling
    /// of a Turkish name has to reach the same row.</summary>
    [Theory]
    [InlineData("VintageOnline")]
    [InlineData("VINTAGEONLINE")]
    [InlineData("vıntageonlıne")]
    public void ARowWithNoIdIsFoundByItsFoldedName(string spelling)
    {
        var overrides = new[] { Entry("", "VintageOnline", "leyla@example.com") };

        Assert.Equal("leyla@example.com", OfferMailStore.FindOverride(overrides, "", spelling));
    }

    /// <summary>Ids are what identify a row when there is one, so an address entered against an id is
    /// not reachable by a name that belongs to somebody else.</summary>
    [Fact]
    public void ARowCarryingAnIdIsNotReachableByNameAlone()
    {
        var overrides = new[] { Entry("13193", "Nexa", "info@nexa.com") };

        Assert.Null(OfferMailStore.FindOverride(overrides, "", "Nexa"));
    }

    /// <summary>
    /// Two rows for one seller can only come from a hand-edited file — saving collapses them. The
    /// later one is live, and <see cref="OfferMailStore.FindOverrideProblems"/> has to say the same
    /// thing, or the operator removes the wrong row.
    /// </summary>
    [Fact]
    public void WhenTwoRowsDescribeOneSellerTheLastOneIsLive()
    {
        var overrides = new[]
        {
            Entry("12552", "TonerCenter", "eski@example.com"),
            Entry("12552", "TonerCenter", "yeni@example.com")
        };

        Assert.Equal("yeni@example.com", OfferMailStore.FindOverride(overrides, "12552", "TonerCenter"));
        Assert.Contains(OfferMailStore.FindOverrideProblems(overrides), w => w.Contains("last is used"));
    }

    /// <summary>A row saved with the seller but no address yet is "seen but not finished". It must not
    /// shadow the uploaded list — the seller should still be matched there if they are in it.</summary>
    [Fact]
    public void ARowWithNoAddressYetIsNotAnAnswer()
    {
        Assert.Null(OfferMailStore.FindOverride([Entry("11806", "Proteldepo", "  ")], "11806", "Proteldepo"));
    }

    /// <summary>A seller with several people on the mail: one cell, one To line, no repeats.</summary>
    [Fact]
    public void SeveralAddressesInOneCellComeBackAsOneToLine()
    {
        var overrides = new[] { Entry("10700", "Nasa İletişim", "a@x.com, b@x.com; a@x.com") };

        Assert.Equal("a@x.com; b@x.com", OfferMailStore.FindOverride(overrides, "10700", "Nasa İletişim"));
    }

    /// <summary>A typo in a hand-entered address is caught before Outlook is asked to send to it, and
    /// the offending address is named rather than the whole cell.</summary>
    [Fact]
    public void AMangledAddressIsNamedRatherThanTheWholeRow()
    {
        var problems = OfferMailStore.FindOverrideProblems([Entry("1", "Karataş Online", "info@example.com; bilgi@")]);

        Assert.Contains(problems, w => w.Contains("bilgi@") && w.Contains("Karataş Online"));
    }

    [Fact]
    public void AHealthyListReportsNothing()
    {
        var overrides = new[]
        {
            Entry("13193", "Nexa", "info@nexa.com"),
            Entry("12552", "TonerCenter", "info@tonercenter.com")
        };

        Assert.Empty(OfferMailStore.FindOverrideProblems(overrides));
        Assert.Null(OfferMailStore.FindOverride(overrides, "99999", "Someone Else"));
    }

    // ---------------------------------------------------------------------
    // The minimum offer count
    // ---------------------------------------------------------------------

    /// <summary>
    /// Zero, a negative number and a blank box all mean "mail everybody". Collapsed to one value so no
    /// caller has to test for three — the one that forgot would refuse to mail anybody.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    public void AMinimumOfZeroOrLessIsNoMinimumAtAll(int? saved, int? expected)
    {
        Assert.Equal(expected, OfferMailStore.NormalizeMinimum(saved));
    }

    /// <summary>The threshold is the lever that shortens a run that would otherwise take several passes,
    /// so a saved one has to survive the round trip it is written and read back through.</summary>
    [Fact]
    public void ASavedThresholdComesBackAsItWasWritten()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var written = new OfferMailFile(1, null, null, null, null, 40, null, null, null, []);

        var read = JsonSerializer.Deserialize<OfferMailFile>(JsonSerializer.Serialize(written, options), options);

        Assert.Equal(40, read?.MinOfferCount);
    }

    // ---------------------------------------------------------------------
    // The lead times
    // ---------------------------------------------------------------------

    /// <summary>Nobody typed anything, so the shipped days apply. Not an error — the box is optional,
    /// and every operator had this before it existed.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyLeadTimeBoxMeansTheDefault(string? raw)
    {
        var (days, problem) = OfferMailStore.NormalizeLeadTimes(raw);

        Assert.Null(days);
        Assert.Null(problem);
    }

    /// <summary>However the operator separates them, and in whatever order — the filter is a set.</summary>
    [Theory]
    [InlineData("0,1", new[] { 0, 1 })]
    [InlineData("0, 1", new[] { 0, 1 })]
    [InlineData("0 1", new[] { 0, 1 })]
    [InlineData("0;1", new[] { 0, 1 })]
    [InlineData("1, 0", new[] { 0, 1 })]
    [InlineData("0, 0, 1", new[] { 0, 1 })]
    [InlineData("2", new[] { 2 })]
    public void TheLeadTimesAreSplitDeduplicatedAndSorted(string raw, int[] expected)
    {
        var (days, problem) = OfferMailStore.NormalizeLeadTimes(raw);

        Assert.Equal(expected, days);
        Assert.Null(problem);
    }

    /// <summary>
    /// Named rather than quietly dropped: this is the moment the operator is looking at what they
    /// typed, and a box read as nothing is a filter nobody chose.
    /// </summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("1O")]        // a letter O where a zero was meant
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("99")]
    [InlineData("0,1,2,3,4,5,6")]
    public void ALeadTimeBoxThatCannotBeUsedIsRefusedWithAReason(string raw)
    {
        var (days, problem) = OfferMailStore.NormalizeLeadTimes(raw);

        Assert.Null(days);
        Assert.NotNull(problem);
    }

    /// <summary>A settings file written before this was a setting has no lead times in it and must open
    /// on the default rather than on an empty filter that warns nobody.</summary>
    [Fact]
    public void AFileWithNoLeadTimesResolvesToTheDefault()
    {
        var file = new OfferMailFile(1, null, null, null, null, null, null, null, null, []);

        Assert.Equal(OfferSplitBuilder.DefaultWarnedLeadTimes, OfferMailStore.ResolveLeadTimes(file));
    }

    [Fact]
    public void TheOperatorsSavedLeadTimesWinOverTheDefault()
    {
        var file = new OfferMailFile(1, null, null, null, null, null, [3, 2], null, null, []);

        Assert.Equal([2, 3], OfferMailStore.ResolveLeadTimes(file));
    }

    // ---------------------------------------------------------------------
    // Superseded templates
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>The operator's real settings file is this case.</b> Saving the panel stores the text of the
    /// boxes rather than a null, so an operator who never edited a word still has a frozen copy of the
    /// default from the day they first pressed Save. When the tokens change, that copy keeps quoting a
    /// placeholder the build no longer fills — and the mail leaves with "{leadTime2}" printed in it.
    /// </summary>
    [Fact]
    public void AFrozenCopyOfAnOldDefaultIsDroppedSoTheCurrentOneApplies()
    {
        var old = OfferMailBuilder.SupersededBodyTemplates[0];

        Assert.Null(OfferMailStore.DropSuperseded(old, OfferMailBuilder.SupersededBodyTemplates));
    }

    /// <summary>The same text saved by an editor that rewrote the line endings is still the same text;
    /// a settings file round-tripped through Windows must not escape the migration on that alone.</summary>
    [Fact]
    public void LineEndingsDoNotHideAFrozenCopy()
    {
        var old = OfferMailBuilder.SupersededBodyTemplates[0].Replace("\n", "\r\n");

        Assert.Null(OfferMailStore.DropSuperseded(old, OfferMailBuilder.SupersededBodyTemplates));
    }

    /// <summary>
    /// The other half, and the more important one: a template the operator actually wrote is theirs.
    /// One changed character is enough to keep it, because the alternative — a heuristic that decides
    /// their wording is "close enough" to a default — silently deletes work nobody can get back.
    /// </summary>
    [Theory]
    [InlineData("Sayın yetkili,")]
    [InlineData("")]
    [InlineData("   ")]
    public void ATemplateTheOperatorWroteIsNeverTouched(string saved)
    {
        Assert.Equal(saved, OfferMailStore.DropSuperseded(saved, OfferMailBuilder.SupersededBodyTemplates));
    }

    [Fact]
    public void OneChangedCharacterIsEnoughToKeepATemplate()
    {
        var edited = OfferMailBuilder.SupersededBodyTemplates[0].Replace("Sayın", "Sayin");

        Assert.Equal(edited, OfferMailStore.DropSuperseded(edited, OfferMailBuilder.SupersededBodyTemplates));
    }

    /// <summary>The current default is not superseded — dropping it would be harmless but it would mean
    /// the list had been edited rather than appended to.</summary>
    [Fact]
    public void TheCurrentDefaultIsNotOnTheSupersededList()
    {
        Assert.DoesNotContain(OfferMailBuilder.DefaultBodyTemplate, OfferMailBuilder.SupersededBodyTemplates);
        Assert.DoesNotContain(OfferMailBuilder.DefaultSubjectTemplate, OfferMailBuilder.SupersededSubjectTemplates);
    }

    // ---------------------------------------------------------------------
    // The CC line
    // ---------------------------------------------------------------------

    /// <summary>Nobody typed anything, so nobody is copied. Not an error — the CC is optional.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ; , ")]
    public void AnEmptyCcCopiesNobodyAndIsNotAProblem(string? raw)
    {
        var (cc, problem) = OfferMailStore.NormalizeCc(raw);

        Assert.Null(cc);
        Assert.Null(problem);
    }

    /// <summary>Several people can be copied, written the way any other address cell in this app is
    /// written — and joined into the one line Outlook's CC field expects.</summary>
    [Theory]
    [InlineData("bilgi@sirket.com", "bilgi@sirket.com")]
    [InlineData("  bilgi@sirket.com  ", "bilgi@sirket.com")]
    [InlineData("a@x.com; b@x.com", "a@x.com; b@x.com")]
    [InlineData("a@x.com, b@x.com", "a@x.com; b@x.com")]
    [InlineData("a@x.com; A@X.com; b@x.com", "a@x.com; b@x.com")]
    public void TheCcIsSplitDeduplicatedAndJoinedBack(string raw, string expected)
    {
        var (cc, problem) = OfferMailStore.NormalizeCc(raw);

        Assert.Equal(expected, cc);
        Assert.Null(problem);
    }

    /// <summary>
    /// A typo is named rather than dropped. Dropping it would mail every seller with the copy going
    /// nowhere, and nothing on the screen would say so.
    /// </summary>
    [Fact]
    public void AMangledCcIsRefusedAndNamed()
    {
        var (cc, problem) = OfferMailStore.NormalizeCc("bilgi@sirket.com; kayit@");

        Assert.Null(cc);
        Assert.NotNull(problem);
        Assert.Contains("kayit@", problem);
    }
}
