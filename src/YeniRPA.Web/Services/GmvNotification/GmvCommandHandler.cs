namespace YeniRPA.Web.Services.GmvNotification;

/// <summary>
/// Answers the commands typed to the bot. Only the chat saved in the settings is listened to: the GMV
/// is business data, and anyone who finds the bot's name can write to it. A message from any other chat
/// gets no reply at all, not even "not allowed", so the bot does not confirm it is there.
///
/// <para>The reading goes through <see cref="GmvNotifier.CheckNowAsync"/>, so a command and the
/// "Check GMV now" button follow the same rules (a figure that could not be read is never sent as a
/// number).</para>
/// </summary>
public sealed class GmvCommandHandler(IGmvSettingsStore settings, GmvNotifier notifier, ITelegramSender telegram)
{
    public async Task HandleAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        var config = settings.Load();
        if (update.Text is null || update.ChatId is null || config.ChatId.Length == 0 || update.ChatId != config.ChatId)
            return;

        var token = settings.ReadToken();
        if (string.IsNullOrEmpty(token))
            return;

        string reply;
        switch (Command(update.Text))
        {
            case "/gmv":
                reply = await AnswerAsync(config.IncludeComparison, config.DailyTarget, cancellationToken);
                break;

            case "/weekgmv":
                // Asked for by name, so it compares even when the comparison is switched off for the schedule.
                reply = await AnswerAsync(true, config.DailyTarget, cancellationToken);
                break;

            case null:
                return;

            default:
                reply = GmvMessageComposer.UnknownCommand;
                break;
        }

        try { await telegram.SendAsync(token, config.ChatId, reply, cancellationToken); }
        catch (GmvSendException)
        {
            // Nothing to tell the sender through; the next command tries again.
        }
    }

    async Task<string> AnswerAsync(bool compare, double? dailyTarget, CancellationToken cancellationToken)
    {
        var outcome = await notifier.QueryAsync(compare, cancellationToken);
        return outcome is { Success: true, Gmv: { } gmv }
            ? GmvMessageComposer.Report(gmv, DateTime.Now, dailyTarget, outcome.Comparison)
            : GmvMessageComposer.CommandFailed(outcome.Message);
    }

    /// <summary>The lower-case command word ("/gmv" for "/gmv", "/GMV@my_bot now"), or null for plain text.</summary>
    internal static string? Command(string text)
    {
        var word = text.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (word is null || !word.StartsWith('/'))
            return null;

        var at = word.IndexOf('@');
        return (at < 0 ? word : word[..at]).ToLowerInvariant();
    }
}
