using LiteDB;
using Microsoft.AspNetCore.DataProtection;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.GmvNotification;

namespace YeniRPA.Tests;

/// <summary>
/// The GMV notification rules that must not drift: nothing wrong or empty is ever sent, one scheduled
/// time produces one entry, an expired login is announced once, and the bot token never rests in the
/// database in the clear.
/// </summary>
public class GmvNotificationTests
{
    sealed class MemoryContext : ILiteDbContext, IDisposable
    {
        readonly LiteDatabase _database = new(new MemoryStream());

        public string DatabasePath => "(in memory)";
        public ILiteCollection<T> GetCollection<T>(string name) => _database.GetCollection<T>(name);
        public ILiteCollection<BsonDocument> Raw(string name) => _database.GetCollection(name);
        public void Dispose() => _database.Dispose();
    }

    sealed class FakeReader(Func<GmvReading> read) : IGmvReader
    {
        public int Reads { get; private set; }
        public GmvSessionStatus Status => new("valid", null);

        public Task<GmvReading> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(read());
        }
    }

    sealed class FakeTelegram : ITelegramSender
    {
        public List<string> Messages { get; } = [];
        public bool Fail { get; set; }

        public Task SendAsync(string token, string chatId, string text, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new GmvSendException("Telegram refused the message (400): chat not found.");
            Messages.Add(text);
            return Task.CompletedTask;
        }
    }

    sealed class Rig : IDisposable
    {
        public MemoryContext Context { get; } = new();
        public GmvSettingsStore Settings { get; }
        public GmvHistoryStore History { get; }
        public FakeTelegram Telegram { get; } = new();
        public FakeReader Reader { get; }
        public GmvNotifier Notifier { get; }

        public Rig(Func<GmvReading> read, double? target = null)
        {
            Settings = new GmvSettingsStore(Context, new EphemeralDataProtectionProvider());
            Settings.Save(new GmvSettingsUpdate(true, GmvMode.Times, ["14:00"], 9, 19, "123", "token-abc", target));
            History = new GmvHistoryStore(Context);
            Reader = new FakeReader(read);
            Notifier = new GmvNotifier(Settings, History, Reader, Telegram);
        }

        public void Dispose() => Context.Dispose();
    }

    static GmvReading Reading(double gmv) => new(gmv, DateTimeOffset.Now);

    static GmvSlot Slot(string time = "14:00") =>
        new($"2026-10-07 {time}", time, new DateTime(2026, 10, 7, int.Parse(time[..2]), int.Parse(time[3..]), 0));

    // -----------------------------------------------------------------
    // Message text
    // -----------------------------------------------------------------

    [Fact]
    public void TheReportNamesTheDateTheTimeAndTheAmount()
    {
        var text = GmvMessageComposer.Report(58790, new DateTime(2026, 10, 7, 14, 0, 0), null);
        Assert.Equal("07 Ekim 2026, 14:00 itibarıyla güncel GMV: €58.790.", text);
    }

    [Fact]
    public void ATargetAddsHowFarTheDayHasGot()
    {
        var text = GmvMessageComposer.Report(72000, new DateTime(2026, 10, 7, 14, 0, 0), 100000);
        Assert.EndsWith("Günlük hedefe göre %72 seviyesinde.", text);
    }

    [Fact]
    public void NoTargetMeansNoPercentage()
    {
        Assert.DoesNotContain("%", GmvMessageComposer.Report(1000, DateTime.Now, null));
        Assert.DoesNotContain("%", GmvMessageComposer.Report(1000, DateTime.Now, 0));
    }

    // -----------------------------------------------------------------
    // Schedule
    // -----------------------------------------------------------------

    [Fact]
    public void ATimeIsDueFromItsMinuteUntilTheGraceRunsOut()
    {
        var settings = new GmvSettings { Mode = GmvMode.Times, Times = ["14:00"] };

        Assert.Null(GmvSchedule.Due(settings, new DateTime(2026, 10, 7, 13, 59, 0)));
        Assert.Equal("2026-10-07 14:00", GmvSchedule.Due(settings, new DateTime(2026, 10, 7, 14, 0, 0))?.Key);
        Assert.Equal("2026-10-07 14:00", GmvSchedule.Due(settings, new DateTime(2026, 10, 7, 14, 9, 59))?.Key);
        Assert.Null(GmvSchedule.Due(settings, new DateTime(2026, 10, 7, 14, 10, 0)));
    }

    [Fact]
    public void HourlyModeMeansEveryFullHourInsideTheWindow()
    {
        var settings = new GmvSettings { Mode = GmvMode.Hourly, WindowStartHour = 9, WindowEndHour = 11 };

        Assert.Equal(["09:00", "10:00", "11:00"], GmvSchedule.SlotTimes(settings).Select(t => t.ToString("HH:mm")));
        Assert.Null(GmvSchedule.Due(settings, new DateTime(2026, 10, 7, 8, 0, 0)));
        Assert.NotNull(GmvSchedule.Due(settings, new DateTime(2026, 10, 7, 10, 2, 0)));
        Assert.Null(GmvSchedule.Due(settings, new DateTime(2026, 10, 7, 12, 0, 0)));
    }

    [Fact]
    public void ManualModeHasNoScheduledTimes()
    {
        var settings = new GmvSettings { Mode = GmvMode.Manual, Times = ["14:00"] };
        Assert.Empty(GmvSchedule.SlotTimes(settings));
        Assert.Null(GmvSchedule.Due(settings, new DateTime(2026, 10, 7, 14, 0, 0)));
    }

    [Fact]
    public void OnlySpecificTimesThatPassedRecentlyCountAsMissed()
    {
        var settings = new GmvSettings { Mode = GmvMode.Times, Times = ["06:00", "10:00", "13:00", "18:00"] };
        var missed = GmvSchedule.PassedToday(settings, new DateTime(2026, 10, 7, 14, 0, 0), 6);

        // 06:00 is further back than the lookback, 18:00 has not come yet.
        Assert.Equal(["10:00", "13:00"], missed.Select(s => s.Label));
    }

    // -----------------------------------------------------------------
    // Settings: the token
    // -----------------------------------------------------------------

    [Fact]
    public void TheTokenIsStoredEncryptedAndReadsBackIntact()
    {
        using var context = new MemoryContext();
        var store = new GmvSettingsStore(context, new EphemeralDataProtectionProvider());
        store.Save(new GmvSettingsUpdate(false, GmvMode.Manual, [], 9, 19, "123", "secret-token", null));

        var raw = context.Raw("gmvSettings").FindAll().Single().ToString();
        Assert.DoesNotContain("secret-token", raw);
        Assert.Equal("secret-token", store.ReadToken());
    }

    [Fact]
    public void SavingWithoutATokenKeepsTheStoredOne()
    {
        using var context = new MemoryContext();
        var store = new GmvSettingsStore(context, new EphemeralDataProtectionProvider());
        store.Save(new GmvSettingsUpdate(false, GmvMode.Manual, [], 9, 19, "123", "secret-token", null));
        store.Save(new GmvSettingsUpdate(false, GmvMode.Manual, [], 9, 19, "456", "", null));

        Assert.Equal("secret-token", store.ReadToken());
        Assert.Equal("456", store.Load().ChatId);
    }

    [Fact]
    public void NotificationsCannotBeSwitchedOnWithoutATokenAndAChatId()
    {
        using var context = new MemoryContext();
        var store = new GmvSettingsStore(context, new EphemeralDataProtectionProvider());

        Assert.Throws<InvalidOperationException>(() =>
            store.Save(new GmvSettingsUpdate(true, GmvMode.Times, ["14:00"], 9, 19, "123", null, null)));
        Assert.Throws<InvalidOperationException>(() =>
            store.Save(new GmvSettingsUpdate(true, GmvMode.Times, ["14:00"], 9, 19, "", "token", null)));
    }

    [Fact]
    public void TimesAreValidatedSortedAndDeduplicated()
    {
        using var context = new MemoryContext();
        var store = new GmvSettingsStore(context, new EphemeralDataProtectionProvider());

        store.Save(new GmvSettingsUpdate(false, GmvMode.Times, ["18:00", "9:30", "18:00"], 9, 19, "", null, null));
        Assert.Equal(["09:30", "18:00"], store.Load().Times);

        Assert.Throws<InvalidOperationException>(() =>
            store.Save(new GmvSettingsUpdate(false, GmvMode.Times, ["25:00"], 9, 19, "", null, null)));
    }

    [Fact]
    public void RemovingTheTokenSwitchesNotificationsOff()
    {
        using var rig = new Rig(() => Reading(1));

        rig.Settings.ClearToken();

        Assert.Null(rig.Settings.ReadToken());
        Assert.False(rig.Settings.Load().Enabled);
    }

    // -----------------------------------------------------------------
    // Reading the response
    // -----------------------------------------------------------------

    [Fact]
    public void TheGmvIsTheNumberInTheResponse()
    {
        Assert.Equal(78297, GmvReader.ParseGmv("""{"gmv":78297,"other":1}"""u8.ToArray()));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"gmv":null}""")]
    [InlineData("""{"gmv":"78297"}""")]
    [InlineData("""{"gmv":-5}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""<html>sign in</html>""")]
    public void AnythingButANumberIsUnavailableRatherThanZero(string body)
    {
        Assert.Throws<GmvUnavailableException>(() => GmvReader.ParseGmv(System.Text.Encoding.UTF8.GetBytes(body)));
    }

    // -----------------------------------------------------------------
    // Notifier
    // -----------------------------------------------------------------

    [Fact]
    public async Task AScheduledTimeSendsOneMessageAndRecordsIt()
    {
        using var rig = new Rig(() => Reading(58790), target: 100000);

        var outcome = await rig.Notifier.RunSlotAsync(Slot(), default);

        Assert.True(outcome.Success);
        Assert.Equal("07 Ekim 2026, 14:00 itibarıyla güncel GMV: €58.790. Günlük hedefe göre %59 seviyesinde.", Assert.Single(rig.Telegram.Messages));

        var entry = Assert.Single(rig.History.Recent(10));
        Assert.Equal(GmvStatus.Sent, entry.Status);
        Assert.Equal(58790, entry.Gmv);
    }

    [Fact]
    public async Task TheSameTimeNeverSendsTwice()
    {
        using var rig = new Rig(() => Reading(1000));

        await rig.Notifier.RunSlotAsync(Slot(), default);
        var second = await rig.Notifier.RunSlotAsync(Slot(), default);

        Assert.Equal(GmvStatus.Skipped, second.Status);
        Assert.Single(rig.Telegram.Messages);
        Assert.Single(rig.History.Recent(10));
        Assert.Equal(1, rig.Reader.Reads);
    }

    [Fact]
    public async Task ADashboardWithoutAFigureSendsNothing()
    {
        using var rig = new Rig(() => throw new GmvUnavailableException("The dashboard response did not contain a GMV figure."));

        var outcome = await rig.Notifier.RunSlotAsync(Slot(), default);

        Assert.False(outcome.Success);
        Assert.Empty(rig.Telegram.Messages);
        var entry = Assert.Single(rig.History.Recent(10));
        Assert.Equal(GmvStatus.Failed, entry.Status);
        Assert.Null(entry.Gmv);
        Assert.Contains("GMV figure", entry.Error);
    }

    [Fact]
    public async Task AnExpiredSessionSendsTheSignInAgainWarningOnce()
    {
        using var rig = new Rig(() => throw new GmvSessionExpiredException("expired"));

        await rig.Notifier.RunSlotAsync(Slot("14:00"), default);
        var later = await rig.Notifier.RunSlotAsync(Slot("15:00"), default);

        Assert.Equal(GmvMessageComposer.SessionExpired, Assert.Single(rig.Telegram.Messages));
        Assert.Equal(GmvStatus.Skipped, later.Status);
        Assert.Equal([GmvStatus.Skipped, GmvStatus.Session], rig.History.Recent(10).Select(x => x.Status));
    }

    [Fact]
    public async Task AWorkingReadAfterAnExpiryReArmsTheWarning()
    {
        var expired = true;
        using var rig = new Rig(() => expired ? throw new GmvSessionExpiredException("expired") : Reading(5000));

        await rig.Notifier.RunSlotAsync(Slot("10:00"), default);
        expired = false;
        await rig.Notifier.RunSlotAsync(Slot("11:00"), default);
        expired = true;
        await rig.Notifier.RunSlotAsync(Slot("12:00"), default);

        Assert.Equal(2, rig.Telegram.Messages.Count(m => m == GmvMessageComposer.SessionExpired));
    }

    [Fact]
    public async Task ACheckWithoutSendingNeverTouchesTelegram()
    {
        using var rig = new Rig(() => Reading(42000));

        var outcome = await rig.Notifier.CheckNowAsync(false, default);

        Assert.True(outcome.Success);
        Assert.Equal(42000, outcome.Gmv);
        Assert.Empty(rig.Telegram.Messages);
        Assert.Equal(GmvStatus.Checked, Assert.Single(rig.History.Recent(10)).Status);
    }

    [Fact]
    public async Task AManualCheckOnAnExpiredSessionReportsItWithoutAWarningMessage()
    {
        using var rig = new Rig(() => throw new GmvSessionExpiredException("expired"));

        var outcome = await rig.Notifier.CheckNowAsync(true, default);

        Assert.False(outcome.Success);
        Assert.Empty(rig.Telegram.Messages);
    }

    [Fact]
    public async Task ATelegramFailureIsRecordedWithItsReason()
    {
        using var rig = new Rig(() => Reading(1000));
        rig.Telegram.Fail = true;

        var outcome = await rig.Notifier.RunSlotAsync(Slot(), default);

        Assert.False(outcome.Success);
        var entry = Assert.Single(rig.History.Recent(10));
        Assert.Equal(GmvStatus.Failed, entry.Status);
        Assert.Contains("chat not found", entry.Error);
        Assert.Equal(1000, entry.Gmv);
    }

    [Fact]
    public async Task AMissedTimeIsRecordedOnceAndBlocksALateSend()
    {
        using var rig = new Rig(() => Reading(1000));

        rig.Notifier.RecordMissed(Slot());
        rig.Notifier.RecordMissed(Slot());
        var late = await rig.Notifier.RunSlotAsync(Slot(), default);

        Assert.Equal(GmvStatus.Skipped, late.Status);
        Assert.Empty(rig.Telegram.Messages);
        Assert.Equal(GmvStatus.Missed, Assert.Single(rig.History.Recent(10)).Status);
    }

    [Fact]
    public async Task TheTestMessageNeedsTheTelegramSettings()
    {
        using var context = new MemoryContext();
        var settings = new GmvSettingsStore(context, new EphemeralDataProtectionProvider());
        var telegram = new FakeTelegram();
        var notifier = new GmvNotifier(settings, new GmvHistoryStore(context), new FakeReader(() => Reading(1)), telegram);

        var outcome = await notifier.SendTestAsync(default);

        Assert.False(outcome.Success);
        Assert.Empty(telegram.Messages);
    }
}
