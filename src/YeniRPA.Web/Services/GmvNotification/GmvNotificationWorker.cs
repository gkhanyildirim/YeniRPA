namespace YeniRPA.Web.Services.GmvNotification;

/// <summary>
/// Runs the scheduled GMV notifications while the app is running. It is a background task inside this
/// process, not a separate service: with the app closed nothing is sent, and a time that passes
/// meanwhile is recorded as missed when the app starts again.
///
/// <para>While notifications are on, it also makes one light request every
/// <see cref="GmvSettings.KeepAliveMinutes"/> minutes. The Marketplace ends a login after 30 minutes
/// without activity, so without this the login would be gone by the next scheduled time. If that
/// request finds the login gone, the operator is told right away.</para>
///
/// <para>The settings are read at every 30-second tick, so a change made on the Jobs screen applies at
/// the next tick without a restart.</para>
/// </summary>
public sealed class GmvNotificationWorker(
    IGmvSettingsStore settings,
    IGmvHistoryStore history,
    IGmvReader reader,
    GmvNotifier notifier,
    JobStatusTracker jobs,
    ILogger<GmvNotificationWorker> logger) : BackgroundService
{
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
            var outcome = await notifier.RunSlotAsync(due, cancellationToken);
            jobs.Record(JobIds.GmvReport, outcome.Success, outcome.Message);

            // A scheduled read is activity on the login too.
            _lastKeepAlive = DateTime.Now;
            return;
        }

        if ((now - _lastKeepAlive).TotalMinutes >= config.KeepAliveMinutes)
        {
            _lastKeepAlive = now;
            await KeepAliveAsync(cancellationToken);
        }
    }

    async Task KeepAliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reading = await reader.ReadAsync(cancellationToken);
            notifier.NoteSessionWorking(reading.Gmv);
            jobs.Record(JobIds.SessionKeepAlive, true, "The login is valid.");
        }
        catch (GmvSessionExpiredException ex)
        {
            jobs.Record(JobIds.SessionKeepAlive, false, ex.Message);
            await notifier.NotifySessionLostAsync(cancellationToken);
        }
        catch (GmvUnavailableException ex)
        {
            jobs.Record(JobIds.SessionKeepAlive, false, ex.Message);
            logger.LogWarning("GMV keep-alive could not read the dashboard: {Message}", ex.Message);
        }
    }
}
