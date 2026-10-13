namespace YeniRPA.Web.Services.PaymentCheck;

/// <summary>
/// Runs the scheduled payment-page checks while the app is running. Settings are re-read at every
/// 30-second tick, so a change on the Jobs screen applies without a restart. The last run's time comes
/// from the history, so a restart does not trigger an extra check right away.
/// </summary>
public sealed class PaymentCheckWorker(
    IPaymentCheckSettingsStore settings,
    IPaymentCheckHistoryStore history,
    PaymentCheckNotifier notifier,
    ILogger<PaymentCheckWorker> logger) : BackgroundService
{
    /// <summary>Last attempt this process made, so a run that fails to reach the history cannot loop.</summary>
    DateTime? _lastAttemptLocal;

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
                    logger.LogError(ex, "Payment check tick failed.");
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
        var now = DateTime.Now;

        var lastRun = history.Last()?.TimestampUtc.ToLocalTime();
        var last = lastRun is { } a && _lastAttemptLocal is { } b ? (a > b ? a : b) : lastRun ?? _lastAttemptLocal;

        if (!PaymentCheckSchedule.IsDue(config, last, now))
            return;

        _lastAttemptLocal = now;
        await notifier.TryRunAsync(PaymentCheckTrigger.Scheduled, cancellationToken);
    }
}
