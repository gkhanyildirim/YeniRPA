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

    sealed class FakeReader(Func<GmvReading> read, Func<DateTimeOffset, DateTimeOffset, GmvReading>? readRange = null) : IGmvReader
    {
        public int Reads { get; private set; }
        public List<(DateTimeOffset From, DateTimeOffset To)> Ranges { get; } = [];
        public GmvSessionStatus Status => new("valid", null);

        public Task<GmvReading> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(read());
        }

        public Task<GmvReading> ReadRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            Ranges.Add((from, to));
            return Task.FromResult(readRange is null
                ? throw new GmvUnavailableException("No earlier figure.")
                : readRange(from, to));
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

        public Rig(
            Func<GmvReading> read,
            double? target = null,
            Func<DateTimeOffset, DateTimeOffset, GmvReading>? readRange = null,
            bool compare = false)
        {
            Settings = new GmvSettingsStore(Context, new EphemeralDataProtectionProvider());
            Settings.Save(new GmvSettingsUpdate(true, GmvMode.Times, ["14:00"], 9, 19, "123", "token-abc", target,
                IncludeComparison: compare));
            History = new GmvHistoryStore(Context);
            Reader = new FakeReader(read, readRange);
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
        Assert.Equal("7 Ekim 2026;\nGMV (14:00): €58,790", text);
    }

    [Fact]
    public void ATargetAddsHowFarTheDayHasGot()
    {
        var text = GmvMessageComposer.Report(72000, new DateTime(2026, 10, 7, 14, 0, 0), 100000);
        Assert.EndsWith(" | Hedef %72", text);
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
        Assert.Equal("7 Ekim 2026;\nGMV (14:00): €58,790 | Hedef %59", Assert.Single(rig.Telegram.Messages));

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

    // -----------------------------------------------------------------
    // Interval mode, the Jobs screen and the keep-alive warning
    // -----------------------------------------------------------------

    [Fact]
    public void IntervalModeMeansEveryNMinutesFromTheFirstWindowHourToTheEndOfTheLast()
    {
        var settings = new GmvSettings { Mode = GmvMode.Interval, IntervalMinutes = 15, WindowStartHour = 9, WindowEndHour = 10 };

        Assert.Equal(["09:00", "09:15", "09:30", "09:45", "10:00", "10:15", "10:30", "10:45"],
            GmvSchedule.SlotTimes(settings).Select(t => t.ToString("HH:mm")));
        Assert.Equal("2026-10-07 09:15", GmvSchedule.Due(settings, new DateTime(2026, 10, 7, 9, 16, 0))?.Key);
        Assert.Null(GmvSchedule.Due(settings, new DateTime(2026, 10, 7, 11, 0, 0)));
        Assert.Empty(GmvSchedule.PassedToday(settings, new DateTime(2026, 10, 7, 10, 0, 0), 6));
    }

    [Fact]
    public void TheNextTimeRollsOverToTomorrowAfterTheLastOne()
    {
        var settings = new GmvSettings { Mode = GmvMode.Interval, IntervalMinutes = 30, WindowStartHour = 9, WindowEndHour = 9 };

        Assert.Equal(new DateTime(2026, 10, 7, 9, 30, 0), GmvSchedule.Next(settings, new DateTime(2026, 10, 7, 9, 5, 0)));
        Assert.Equal(new DateTime(2026, 10, 8, 9, 0, 0), GmvSchedule.Next(settings, new DateTime(2026, 10, 7, 12, 0, 0)));
        Assert.Null(GmvSchedule.Next(new GmvSettings { Mode = GmvMode.Manual }, DateTime.Now));
    }

    [Fact]
    public void TheIntervalAndTheKeepAliveAreRangeChecked()
    {
        using var context = new MemoryContext();
        var store = new GmvSettingsStore(context, new EphemeralDataProtectionProvider());

        Assert.Throws<InvalidOperationException>(() => store.UpdateSchedule(new GmvScheduleUpdate(IntervalMinutes: 4)));
        Assert.Throws<InvalidOperationException>(() => store.UpdateSchedule(new GmvScheduleUpdate(IntervalMinutes: 121)));
        Assert.Throws<InvalidOperationException>(() => store.UpdateSchedule(new GmvScheduleUpdate(KeepAliveMinutes: 26)));
        Assert.Throws<InvalidOperationException>(() => store.UpdateSchedule(new GmvScheduleUpdate(KeepAliveMinutes: 0)));
    }

    [Fact]
    public void ChangingTheScheduleLeavesTheTelegramSettingsAlone()
    {
        using var rig = new Rig(() => Reading(1), target: 90000);

        var saved = rig.Settings.UpdateSchedule(new GmvScheduleUpdate(Mode: GmvMode.Interval, IntervalMinutes: 20, KeepAliveMinutes: 8));

        Assert.Equal(GmvMode.Interval, saved.Mode);
        Assert.Equal(20, rig.Settings.Load().IntervalMinutes);
        Assert.Equal(8, rig.Settings.Load().KeepAliveMinutes);
        Assert.Equal("123", rig.Settings.Load().ChatId);
        Assert.Equal("token-abc", rig.Settings.ReadToken());
        Assert.Equal(90000, rig.Settings.Load().DailyTarget);
        Assert.Equal(["14:00"], rig.Settings.Load().Times);
    }

    [Fact]
    public void AJobCannotBeSwitchedOnWithoutTelegramSettings()
    {
        using var context = new MemoryContext();
        var store = new GmvSettingsStore(context, new EphemeralDataProtectionProvider());

        Assert.Throws<InvalidOperationException>(() => store.UpdateSchedule(new GmvScheduleUpdate(Enabled: true)));
    }

    [Fact]
    public async Task TheKeepAliveFindingTheLoginGoneWarnsOnceUntilItWorksAgain()
    {
        var expired = true;
        using var rig = new Rig(() => expired ? throw new GmvSessionExpiredException("expired") : Reading(5000));

        await rig.Notifier.NotifySessionLostAsync(default);
        await rig.Notifier.NotifySessionLostAsync(default);
        Assert.Equal(GmvMessageComposer.SessionExpired, Assert.Single(rig.Telegram.Messages));
        // The repeats between keep-alives leave no history rows behind.
        Assert.Single(rig.History.Recent(10));

        expired = false;
        rig.Notifier.NoteSessionWorking(5000);
        await rig.Notifier.NotifySessionLostAsync(default);

        Assert.Equal(2, rig.Telegram.Messages.Count(m => m == GmvMessageComposer.SessionExpired));
    }

    [Fact]
    public void AWorkingKeepAliveWithoutAnEarlierWarningWritesNothing()
    {
        using var rig = new Rig(() => Reading(1));

        rig.Notifier.NoteSessionWorking(1);

        Assert.Empty(rig.History.Recent(10));
    }

    // -----------------------------------------------------------------
    // Telegram commands
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("/gmv", "/gmv")]
    [InlineData("  /GMV  ", "/gmv")]
    [InlineData("/gmv@my_bot", "/gmv")]
    [InlineData("/gmv şimdi", "/gmv")]
    [InlineData("/baska", "/baska")]
    [InlineData("gmv", null)]
    [InlineData("", null)]
    public void TheCommandWordIsTheFirstWordWithoutTheBotName(string text, string? expected)
    {
        Assert.Equal(expected, GmvCommandHandler.Command(text));
    }

    [Fact]
    public async Task TheGmvCommandAnswersTheSavedChatWithTheCurrentFigure()
    {
        using var rig = new Rig(() => Reading(58790), target: 100000);
        var handler = new GmvCommandHandler(rig.Settings, rig.Notifier, rig.Telegram);

        await handler.HandleAsync(new TelegramUpdate(1, "123", "/gmv"), default);

        var reply = Assert.Single(rig.Telegram.Messages);
        Assert.Contains("€58,790", reply);
        Assert.Contains("Hedef %59", reply);
    }

    [Fact]
    public async Task AnotherChatGetsNoAnswerAndNoReading()
    {
        using var rig = new Rig(() => Reading(58790));
        var handler = new GmvCommandHandler(rig.Settings, rig.Notifier, rig.Telegram);

        await handler.HandleAsync(new TelegramUpdate(1, "999", "/gmv"), default);

        Assert.Empty(rig.Telegram.Messages);
        Assert.Equal(0, rig.Reader.Reads);
    }

    [Fact]
    public async Task ACommandOnAnExpiredSessionSaysSoInsteadOfAFigure()
    {
        using var rig = new Rig(() => throw new GmvSessionExpiredException("The Marketplace session has expired. Sign in again."));
        var handler = new GmvCommandHandler(rig.Settings, rig.Notifier, rig.Telegram);

        await handler.HandleAsync(new TelegramUpdate(1, "123", "/gmv"), default);

        var reply = Assert.Single(rig.Telegram.Messages);
        Assert.StartsWith("GMV şu an okunamadı", reply);
        Assert.DoesNotContain("€", reply);
    }

    [Fact]
    public async Task AnUnknownCommandGetsAHintAndPlainTextGetsNothing()
    {
        using var rig = new Rig(() => Reading(1));
        var handler = new GmvCommandHandler(rig.Settings, rig.Notifier, rig.Telegram);

        await handler.HandleAsync(new TelegramUpdate(1, "123", "/baska"), default);
        await handler.HandleAsync(new TelegramUpdate(2, "123", "merhaba"), default);
        await handler.HandleAsync(new TelegramUpdate(3, "123", null), default);

        Assert.Equal(GmvMessageComposer.UnknownCommand, Assert.Single(rig.Telegram.Messages));
        Assert.Equal(0, rig.Reader.Reads);
    }

    [Fact]
    public void TelegramUpdatesAreReadWithTheirChatAndText()
    {
        var updates = TelegramReceiver.Parse("""
            {"ok":true,"result":[
              {"update_id":10,"message":{"chat":{"id":-100123},"text":"/gmv","date":1}},
              {"update_id":11,"message":{"chat":{"id":5},"photo":[]}},
              {"update_id":12,"edited_message":{}}
            ]}
            """);

        Assert.Equal([10L, 11L, 12L], updates.Select(u => u.UpdateId));
        Assert.Equal(("-100123", "/gmv"), (updates[0].ChatId, updates[0].Text));
        Assert.Equal(("5", null), (updates[1].ChatId, updates[1].Text));
        Assert.Null(updates[2].ChatId);
    }

    // -----------------------------------------------------------------
    // Comparison: yesterday / last week at this time
    // -----------------------------------------------------------------

    static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    static DateTimeOffset Berlin_(int y, int m, int d, int h, int min) =>
        new(new DateTime(y, m, d, h, min, 0), Berlin.GetUtcOffset(new DateTime(y, m, d, h, min, 0)));

    [Fact]
    public void YesterdayAtThisTimeRunsFromYesterdaysMidnightToTheSameTimeOfDay()
    {
        var (from, to) = GmvDashboard.ComparisonRange(Berlin_(2026, 10, 12, 14, 15), 1, Berlin);

        Assert.Equal(Berlin_(2026, 10, 11, 0, 0), from);
        Assert.Equal(Berlin_(2026, 10, 11, 14, 15), to);
    }

    [Fact]
    public void LastWeekIsTheSameWeekdaySevenCalendarDaysBack()
    {
        var (from, to) = GmvDashboard.ComparisonRange(Berlin_(2026, 10, 12, 9, 30), 7, Berlin);

        Assert.Equal(Berlin_(2026, 10, 5, 0, 0), from);
        Assert.Equal(Berlin_(2026, 10, 5, 9, 30), to);
        Assert.Equal(DayOfWeek.Monday, to.DayOfWeek);
    }

    [Fact]
    public void ADayAcrossTheClockChangeStillStartsAtItsOwnMidnight()
    {
        // Clocks went back on 2026-10-25: the 24th is on summer time (+02:00), the 26th on winter time (+01:00).
        var (from, to) = GmvDashboard.ComparisonRange(Berlin_(2026, 10, 26, 12, 0), 2, Berlin);

        Assert.Equal(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.FromHours(2)), from);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(2)), to);
    }

    [Fact]
    public void TheWholeDayEndsAtTheNextMidnight()
    {
        var (from, to) = GmvDashboard.FullDay(Berlin_(2026, 10, 12, 14, 15), 1, Berlin);

        Assert.Equal(Berlin_(2026, 10, 11, 0, 0), from);
        Assert.Equal(Berlin_(2026, 10, 12, 0, 0), to);
    }

    [Fact]
    public void TheSalesUrlCarriesTheRequestedStretch()
    {
        var url = GmvDashboard.SalesUrl(Berlin_(2026, 10, 11, 0, 0), Berlin_(2026, 10, 11, 14, 15));

        Assert.Contains("startDate=2026-10-11T00%3A00%3A00.000%2B02%3A00", url);
        Assert.Contains("endDate=2026-10-11T14%3A15%3A00.000%2B02%3A00", url);
        Assert.Contains("showShipped=ALL", url);
    }

    [Theory]
    [InlineData(10700, 10000, "▲ +7% (Y €10,000, +€700)")]
    [InlineData(9700, 10000, "▼ -3% (Y €10,000, -€300)")]
    [InlineData(10004, 10000, "0% (Y €10,000, +€4)")]
    [InlineData(10000.3, 10000, "0% (Y €10,000, €0)")]
    [InlineData(500, 0, "— (Y €0, +€500)")]
    public void TheChangeShowsAnArrowThePercentageTheEarlierFigureAndTheEuroChange(double current, double earlier, string expected)
    {
        Assert.Equal(expected, GmvMessageComposer.Change(current, earlier, "Y"));
    }

    [Fact]
    public void AnEarlierFigureThatCouldNotBeReadSaysSoInsteadOfShowingZero()
    {
        Assert.Equal("veri yok", GmvMessageComposer.Change(58790, null, "Y"));
    }

    [Fact]
    public void TheReportIsADateLineAndOneFigureLineWithDodAndWow()
    {
        var text = GmvMessageComposer.Report(18233, new DateTime(2026, 10, 12, 11, 45, 0), null, new GmvComparison(18233, 17040, 19500));

        Assert.Equal("12 Ekim 2026;\nGMV (11:45): €18,233 | DoD ▲ +7% (Y €17,040, +€1,193) | WoW ▼ -6% (LW €19,500, -€1,267)", text);
    }

    [Fact]
    public void TheTargetComesLastAndAMissingFigureIsNamed()
    {
        var text = GmvMessageComposer.Report(72000, new DateTime(2026, 10, 12, 14, 0, 0), 100000, new GmvComparison(72000, 60000, null));

        Assert.Equal("12 Ekim 2026;\nGMV (14:00): €72,000 | DoD ▲ +20% (Y €60,000, +€12,000) | WoW veri yok | Hedef %72", text);
    }

    static GmvReading EarlierFigure(DateTimeOffset from, double yesterday, double lastWeek) =>
        Reading(from.Date == GmvDashboard.PlatformNow().Date.AddDays(-1) ? yesterday : lastWeek);

    [Fact]
    public async Task AScheduledMessageCarriesDodAndWowWhenTheSettingIsOn()
    {
        using var rig = new Rig(() => Reading(58790), readRange: (from, _) => EarlierFigure(from, 54200, 60000), compare: true);

        await rig.Notifier.RunSlotAsync(Slot(), default);

        Assert.EndsWith("GMV (14:00): €58,790 | DoD ▲ +8% (Y €54,200, +€4,590) | WoW ▼ -2% (LW €60,000, -€1,210)", Assert.Single(rig.Telegram.Messages));
        Assert.Equal(2, rig.Reader.Ranges.Count);
        // Only today's figure goes into the history.
        Assert.Equal(58790, Assert.Single(rig.History.Recent(10)).Gmv);
    }

    [Fact]
    public async Task TheSettingOffKeepsTheMessageAsItWas()
    {
        using var rig = new Rig(() => Reading(58790), readRange: (_, _) => Reading(54200), compare: false);

        await rig.Notifier.RunSlotAsync(Slot(), default);

        Assert.DoesNotContain("DoD", Assert.Single(rig.Telegram.Messages));
        Assert.Empty(rig.Reader.Ranges);
    }

    [Fact]
    public async Task AnEarlierFigureThatFailsStillSendsTodaysWithoutInventingOne()
    {
        using var rig = new Rig(() => Reading(58790), readRange: null, compare: true);

        var outcome = await rig.Notifier.RunSlotAsync(Slot(), default);

        Assert.True(outcome.Success);
        var message = Assert.Single(rig.Telegram.Messages);
        Assert.Contains("€58,790", message);
        Assert.EndsWith("DoD veri yok | WoW veri yok", message);
    }

    [Fact]
    public async Task AnExpiredLoginWithTheComparisonOnStillSendsTheWarningOnce()
    {
        using var rig = new Rig(() => throw new GmvSessionExpiredException("expired"), readRange: (_, _) => Reading(1), compare: true);

        await rig.Notifier.RunSlotAsync(Slot("14:00"), default);

        Assert.Equal(GmvMessageComposer.SessionExpired, Assert.Single(rig.Telegram.Messages));
        Assert.Empty(rig.Reader.Ranges);
    }

    [Fact]
    public async Task TheWeekCommandComparesEvenWhenTheScheduleHasItSwitchedOff()
    {
        using var rig = new Rig(() => Reading(58790), readRange: (from, _) => EarlierFigure(from, 54200, 60000), compare: false);
        var handler = new GmvCommandHandler(rig.Settings, rig.Notifier, rig.Telegram);

        await handler.HandleAsync(new TelegramUpdate(1, "123", "/weekgmv"), default);

        Assert.EndsWith("GMV (" + DateTime.Now.ToString("HH:mm") + "): €58,790 | DoD ▲ +8% (Y €54,200, +€4,590) | WoW ▼ -2% (LW €60,000, -€1,210)", Assert.Single(rig.Telegram.Messages));

        // One stretch starts yesterday's midnight, the other the midnight seven days back; each ends the same day.
        var today = GmvDashboard.PlatformNow().Date;
        Assert.Equal([today.AddDays(-1), today.AddDays(-7)], rig.Reader.Ranges.Select(r => r.From.Date));
        Assert.All(rig.Reader.Ranges, r => Assert.Equal(r.From.Date, r.To.Date));
    }

    [Fact]
    public async Task TheGmvCommandAddsTheComparisonOnlyWhenTheSettingIsOn()
    {
        using var on = new Rig(() => Reading(58790), readRange: (_, _) => Reading(54200), compare: true);
        await new GmvCommandHandler(on.Settings, on.Notifier, on.Telegram).HandleAsync(new TelegramUpdate(1, "123", "/gmv"), default);
        Assert.Contains("DoD", Assert.Single(on.Telegram.Messages));

        using var off = new Rig(() => Reading(58790), readRange: (_, _) => Reading(54200), compare: false);
        await new GmvCommandHandler(off.Settings, off.Notifier, off.Telegram).HandleAsync(new TelegramUpdate(1, "123", "/gmv"), default);
        Assert.DoesNotContain("DoD", Assert.Single(off.Telegram.Messages));
    }

    [Fact]
    public void ChangingTheScheduleKeepsTheComparisonSetting()
    {
        using var rig = new Rig(() => Reading(1), compare: false);

        rig.Settings.UpdateSchedule(new GmvScheduleUpdate(IntervalMinutes: 20));

        Assert.False(rig.Settings.Load().IncludeComparison);
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
