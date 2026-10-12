namespace YeniRPA.Web.Services.GmvNotification;

/// <summary>
/// Listens for commands typed to the Telegram bot (see <see cref="GmvCommandHandler"/>) while GMV
/// notifications are on. Like <see cref="GmvNotificationWorker"/> it lives inside this process: with the
/// app closed a command goes unanswered.
///
/// <para><b>Old messages are never answered.</b> When listening starts — at launch, or when
/// notifications are switched back on — whatever is already waiting at Telegram is confirmed without
/// being handled. Otherwise a "/gmv" typed an hour ago, while the app was closed, would answer with a
/// figure the operator no longer asked for.</para>
/// </summary>
public sealed class GmvCommandWorker(
    IGmvSettingsStore settings,
    ITelegramReceiver receiver,
    GmvCommandHandler handler,
    ILogger<GmvCommandWorker> logger) : BackgroundService
{
    const int WaitSeconds = 25;
    static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long? offset = null;
        var primed = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var config = settings.Load();
                var token = settings.ReadToken();

                if (!config.Enabled || string.IsNullOrEmpty(token) || config.ChatId.Length == 0)
                {
                    primed = false;
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                try
                {
                    if (!primed)
                    {
                        var latest = await receiver.GetUpdatesAsync(token, -1, 0, stoppingToken);
                        if (latest.Count > 0)
                            offset = latest.Max(u => u.UpdateId) + 1;
                        primed = true;
                        continue;
                    }

                    var updates = await receiver.GetUpdatesAsync(token, offset, WaitSeconds, stoppingToken);
                    foreach (var update in updates)
                    {
                        offset = Math.Max(offset ?? 0, update.UpdateId + 1);

                        try
                        {
                            await handler.HandleAsync(update, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // One bad command must not stop the listening.
                            logger.LogError(ex, "A Telegram command could not be handled.");
                        }
                    }
                }
                catch (GmvSendException ex)
                {
                    logger.LogWarning("Telegram command listening paused: {Message}", ex.Message);
                    await Task.Delay(ErrorDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down.
        }
    }
}
