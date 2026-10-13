namespace YeniRPA.Web.Services.GmvNotification;

public sealed record GmvOutcome(bool Success, string Status, string Message, double? Gmv, GmvComparison? Comparison = null);

/// <summary>
/// Reads the GMV and tells the operator about it, and writes down what happened. The scheduled worker
/// and the manual buttons both go through here, so they cannot disagree about the rules:
///
/// <list type="bullet">
///   <item>A figure that could not be read is never sent — not zero, not an empty message.</item>
///   <item>A scheduled time produces at most one entry, whatever the outcome
///   (<see cref="IGmvHistoryStore.HasSlot"/>).</item>
///   <item>An expired login produces one "sign in again" warning, then stays quiet until a read
///   works again, instead of repeating it at every scheduled time.</item>
/// </list>
///
/// <para>Reading is plain HTTP, so it does not take the single automation run slot
/// (<c>AutomationJobBus</c>) and does not wait for a Create Return run to finish.</para>
/// </summary>
public sealed class GmvNotifier(
    IGmvSettingsStore settings,
    IGmvHistoryStore history,
    IGmvReader reader,
    ITelegramSender telegram,
    ITeamsSender? teams = null)
{
    const string MissingTelegram = "Enter the Telegram bot token and chat ID first.";
    const string ExpiredReason = "The Marketplace session has expired. Sign in again.";

    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<GmvOutcome> SendTestAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryCredentials(out var token, out var chatId))
                return Record(GmvTrigger.Test, null, null, GmvStatus.Failed, MissingTelegram);

            var delivery = await DeliverAsync(token, chatId, GmvMessageComposer.Test, cancellationToken);
            if (delivery.TelegramError is not null)
                return Record(GmvTrigger.Test, null, null, GmvStatus.Failed, delivery.FailureText());

            return Record(GmvTrigger.Test, null, null, GmvStatus.Test, delivery.TeamsError);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"Check GMV now": reads it, and sends it too when <paramref name="send"/> is set.</summary>
    public Task<GmvOutcome> CheckNowAsync(bool send, CancellationToken cancellationToken) =>
        ExecuteAsync(GmvTrigger.Manual, null, send, send && settings.Load().IncludeComparison, cancellationToken);

    /// <summary>
    /// A Telegram command's read: nothing is sent from here (the handler answers), but the result
    /// carries the comparison with yesterday and last week when <paramref name="compare"/> is set.
    /// </summary>
    public Task<GmvOutcome> QueryAsync(bool compare, CancellationToken cancellationToken) =>
        ExecuteAsync(GmvTrigger.Manual, null, false, compare, cancellationToken);

    /// <summary>A scheduled notification. Does nothing when the slot was already handled.</summary>
    public Task<GmvOutcome> RunSlotAsync(GmvSlot slot, CancellationToken cancellationToken) =>
        ExecuteAsync(GmvTrigger.Scheduled, slot, true, settings.Load().IncludeComparison, cancellationToken);

    /// <summary>Writes down a scheduled time the app was not running for.</summary>
    public void RecordMissed(GmvSlot slot)
    {
        if (!history.HasSlot(slot.Key))
            Record(GmvTrigger.Scheduled, slot.Key, null, GmvStatus.Missed, "The app was not running at this time.");
    }

    async Task<GmvOutcome> ExecuteAsync(
        string trigger, GmvSlot? slot, bool send, bool compare, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var slotKey = slot?.Key;
            if (slotKey is not null && history.HasSlot(slotKey))
                return new GmvOutcome(false, GmvStatus.Skipped, "This time was already handled.", null);

            GmvReading reading;
            GmvComparison? comparison = null;
            try
            {
                if (compare)
                {
                    comparison = await reader.ReadComparedAsync(cancellationToken);
                    reading = new GmvReading(comparison.Current, DateTimeOffset.Now);
                }
                else
                {
                    reading = await reader.ReadAsync(cancellationToken);
                }
            }
            catch (GmvSessionExpiredException ex)
            {
                return await OnSessionExpiredAsync(trigger, slotKey, send, ex.Message, cancellationToken);
            }
            catch (GmvUnavailableException ex)
            {
                return Record(trigger, slotKey, null, GmvStatus.Failed, ex.Message);
            }

            if (!send)
                return Record(trigger, slotKey, reading.Gmv, GmvStatus.Checked, null) with { Comparison = comparison };

            if (!TryCredentials(out var token, out var chatId))
                return Record(trigger, slotKey, reading.Gmv, GmvStatus.Failed, MissingTelegram);

            var text = GmvMessageComposer.Report(reading.Gmv, slot?.At ?? DateTime.Now, settings.Load().DailyTarget, comparison);
            var delivery = await DeliverAsync(token, chatId, text, cancellationToken);
            if (delivery.TelegramError is not null)
                return Record(trigger, slotKey, reading.Gmv, GmvStatus.Failed, delivery.FailureText());

            // Telegram is the channel the operator relies on; a Teams hiccup is noted in the history
            // row but does not turn a delivered notification into a failed one.
            return Record(trigger, slotKey, reading.Gmv, GmvStatus.Sent, delivery.TeamsError);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<GmvOutcome> OnSessionExpiredAsync(
        string trigger, string? slotKey, bool send, string reason, CancellationToken cancellationToken)
    {
        // Someone is looking at the screen for a manual check; the page tells them. The Telegram
        // warning is for the scheduled run that nobody is watching.
        if (trigger != GmvTrigger.Scheduled || !send)
            return Record(trigger, slotKey, null, GmvStatus.Failed, reason);

        return await WarnSessionAsync(slotKey, cancellationToken);
    }

    /// <summary>
    /// The background keep-alive found the login gone between two scheduled times. Tells the operator
    /// right away, under the same "once, until a read works again" rule as a scheduled time.
    /// </summary>
    public async Task<GmvOutcome> NotifySessionLostAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await WarnSessionAsync(null, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A keep-alive read worked. When the operator had been warned, that is the "signed in again" signal
    /// that re-arms the warning; without a row for it a second expiry before the next scheduled time
    /// would go unannounced.
    /// </summary>
    public void NoteSessionWorking(double gmv)
    {
        if (history.LastLoginSignal() == GmvStatus.Session)
            Record(GmvTrigger.Scheduled, null, gmv, GmvStatus.Checked, null);
    }

    async Task<GmvOutcome> WarnSessionAsync(string? slotKey, CancellationToken cancellationToken)
    {
        const string trigger = GmvTrigger.Scheduled;

        if (history.LastLoginSignal() == GmvStatus.Session)
        {
            const string waiting = "Session warning already sent; waiting for a new sign-in.";

            // The keep-alive runs every few minutes; a history row for each would bury the real ones.
            return slotKey is null
                ? new GmvOutcome(false, GmvStatus.Skipped, waiting, null)
                : Record(trigger, slotKey, null, GmvStatus.Skipped, waiting);
        }

        if (!TryCredentials(out var token, out var chatId))
            return Record(trigger, slotKey, null, GmvStatus.Failed, MissingTelegram);

        var delivery = await DeliverAsync(token, chatId, GmvMessageComposer.SessionExpired, cancellationToken);
        if (delivery.TelegramError is not null)
            return Record(trigger, slotKey, null, GmvStatus.Failed, $"{ExpiredReason} The warning could not be sent: {delivery.FailureText()}");

        return Record(trigger, slotKey, null, GmvStatus.Session,
            delivery.TeamsError is null ? ExpiredReason : $"{ExpiredReason} {delivery.TeamsError}");
    }

    /// <summary>What happened to one message on each channel. A null error means it went out (or the channel is off).</summary>
    sealed record Delivery(string? TelegramError, string? TeamsError, bool TeamsSent)
    {
        public string FailureText() =>
            TelegramError + (TeamsSent ? " The Teams copy was sent." : "");
    }

    /// <summary>
    /// Sends <paramref name="text"/> to Telegram and, when it is switched on, to Teams. The two do not
    /// depend on each other: one being down never keeps the message from the other.
    /// </summary>
    async Task<Delivery> DeliverAsync(string token, string chatId, string text, CancellationToken cancellationToken)
    {
        string? telegramError = null;
        try { await telegram.SendAsync(token, chatId, text, cancellationToken); }
        catch (GmvSendException ex) { telegramError = ex.Message; }

        string? teamsError = null;
        var teamsSent = false;
        var url = teams is not null && settings.Load().TeamsEnabled ? settings.ReadTeamsUrl() : null;

        if (url is not null)
        {
            try
            {
                await teams!.SendAsync(url, text, cancellationToken);
                teamsSent = true;
            }
            catch (GmvSendException ex)
            {
                teamsError = $"Teams: {ex.Message}";
            }
        }

        return new Delivery(telegramError, teamsError, teamsSent);
    }

    bool TryCredentials(out string token, out string chatId)
    {
        token = settings.ReadToken() ?? "";
        chatId = settings.Load().ChatId;
        return token.Length > 0 && chatId.Length > 0;
    }

    GmvOutcome Record(string trigger, string? slotKey, double? gmv, string status, string? error)
    {
        history.Add(new GmvHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Trigger = trigger,
            SlotKey = slotKey,
            Gmv = gmv,
            Status = status,
            Error = error,
        });

        var success = status is GmvStatus.Sent or GmvStatus.Checked or GmvStatus.Test;
        var message = error ?? status switch
        {
            GmvStatus.Sent => "Notification sent.",
            GmvStatus.Checked when gmv is not null => $"Current GMV: {GmvMessageComposer.Money(gmv.Value)}.",
            GmvStatus.Test => "Test notification sent.",
            _ => status,
        };

        return new GmvOutcome(success, status, message, gmv);
    }
}
