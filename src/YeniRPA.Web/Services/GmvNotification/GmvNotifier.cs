namespace YeniRPA.Web.Services.GmvNotification;

public sealed record GmvOutcome(bool Success, string Status, string Message, double? Gmv);

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
    ITelegramSender telegram)
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

            try { await telegram.SendAsync(token, chatId, GmvMessageComposer.Test, cancellationToken); }
            catch (GmvSendException ex) { return Record(GmvTrigger.Test, null, null, GmvStatus.Failed, ex.Message); }

            return Record(GmvTrigger.Test, null, null, GmvStatus.Test, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"Check GMV now": reads it, and sends it too when <paramref name="send"/> is set.</summary>
    public Task<GmvOutcome> CheckNowAsync(bool send, CancellationToken cancellationToken) =>
        ExecuteAsync(GmvTrigger.Manual, null, send, cancellationToken);

    /// <summary>A scheduled notification. Does nothing when the slot was already handled.</summary>
    public Task<GmvOutcome> RunSlotAsync(GmvSlot slot, CancellationToken cancellationToken) =>
        ExecuteAsync(GmvTrigger.Scheduled, slot, true, cancellationToken);

    /// <summary>Writes down a scheduled time the app was not running for.</summary>
    public void RecordMissed(GmvSlot slot)
    {
        if (!history.HasSlot(slot.Key))
            Record(GmvTrigger.Scheduled, slot.Key, null, GmvStatus.Missed, "The app was not running at this time.");
    }

    async Task<GmvOutcome> ExecuteAsync(string trigger, GmvSlot? slot, bool send, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var slotKey = slot?.Key;
            if (slotKey is not null && history.HasSlot(slotKey))
                return new GmvOutcome(false, GmvStatus.Skipped, "This time was already handled.", null);

            GmvReading reading;
            try
            {
                reading = await reader.ReadAsync(cancellationToken);
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
                return Record(trigger, slotKey, reading.Gmv, GmvStatus.Checked, null);

            if (!TryCredentials(out var token, out var chatId))
                return Record(trigger, slotKey, reading.Gmv, GmvStatus.Failed, MissingTelegram);

            var text = GmvMessageComposer.Report(reading.Gmv, slot?.At ?? DateTime.Now, settings.Load().DailyTarget);
            try { await telegram.SendAsync(token, chatId, text, cancellationToken); }
            catch (GmvSendException ex) { return Record(trigger, slotKey, reading.Gmv, GmvStatus.Failed, ex.Message); }

            return Record(trigger, slotKey, reading.Gmv, GmvStatus.Sent, null);
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

        if (history.LastLoginSignal() == GmvStatus.Session)
            return Record(trigger, slotKey, null, GmvStatus.Skipped, "Session warning already sent; waiting for a new sign-in.");

        if (!TryCredentials(out var token, out var chatId))
            return Record(trigger, slotKey, null, GmvStatus.Failed, MissingTelegram);

        try { await telegram.SendAsync(token, chatId, GmvMessageComposer.SessionExpired, cancellationToken); }
        catch (GmvSendException ex)
        {
            return Record(trigger, slotKey, null, GmvStatus.Failed, $"{ExpiredReason} The warning could not be sent: {ex.Message}");
        }

        return Record(trigger, slotKey, null, GmvStatus.Session, ExpiredReason);
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
