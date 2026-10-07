namespace YeniRPA.Web.Services.GmvNotification;

/// <summary>
/// Runs the scheduled GMV notifications while the app is running. It is a background task inside this
/// process, not a separate service: with the app closed nothing is sent, and a time that passes
/// meanwhile is recorded as missed when the app starts again.
///
/// <para>While notifications are on, it also makes one light request every
/// <see cref="KeepAliveMinutes"/> minutes. The Marketplace ends a login after 30 minutes without
/// activity, so without this the login would be gone by the next scheduled time.</para>
/// </summary>
public sealed class GmvNotificationWorker(
    IGmvSettingsStore settings,
    IGmvHistoryStore history,
    IGmvReader reader,
    GmvNotifier notifier,
    ILogger<GmvNotificationWorker> logger) : BackgroundService
{
    public const int KeepAliveMinutes = 10;
    const int MissedLookbackHours = 6;

    DateTime _lastKeepAlive = DateTime.MinValue;
    bool _missedChecked;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One bad tick must not end the schedule for the rest of the day.
                    logger.LogError(ex, "GMV notification tick failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down.
        }
    }

    async Task TickAsync(CancellationToken cancellationToken)
    {
        var config = settings.Load();
        if (!config.Enabled || config.Mode == GmvMode.Manual)
            return;

        var now = DateTime.Now;

        if (!_missedChecked)
        {
            _missedChecked = true;
            foreach (var slot in GmvSchedule.PassedToday(config, now, MissedLookbackHours))
                notifier.RecordMissed(slot);
        }

        var due = GmvSchedule.Due(config, now);
        if (due is not null && !history.HasSlot(due.Key))
        {
            await notifier.RunSlotAsync(due, cancellationToken);
            _lastKeepAlive = DateTime.Now;
            return;
        }

        if ((now - _lastKeepAlive).TotalMinutes >= KeepAliveMinutes)
        {
            _lastKeepAlive = now;
            await KeepAliveAsync(cancellationToken);
        }
    }

    async Task KeepAliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await reader.ReadAsync(cancellationToken);
        }
        catch (GmvSessionExpiredException)
        {
            // Shown on the page; the operator is told by Telegram at the next scheduled time.
        }
        catch (GmvUnavailableException ex)
        {
            logger.LogWarning("GMV keep-alive could not read the dashboard: {Message}", ex.Message);
        }
    }
}
