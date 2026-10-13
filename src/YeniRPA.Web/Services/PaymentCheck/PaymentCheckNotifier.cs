using System.Globalization;
using YeniRPA.Web.Services.GmvNotification;

namespace YeniRPA.Web.Services.PaymentCheck;

/// <summary>
/// Runs one payment-page check, writes it to the history and tells the Telegram group — the same bot
/// and chat GMV Notification uses. The worker and the "Run now" button both go through here.
///
/// <para>Success and failure both send a message. The one exception is an expired storefront login on
/// a <em>scheduled</em> run: that would repeat every interval until someone signs in, so it is sent
/// once and then stays quiet until a run gets past the login again.</para>
/// </summary>
public sealed class PaymentCheckNotifier(
    PaymentCheckRunner runner,
    IPaymentCheckHistoryStore history,
    IGmvSettingsStore gmvSettings,
    ITelegramSender telegram,
    JobStatusTracker jobs)
{
    readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>False (and nothing happens) when a check is already in progress.</summary>
    public async Task<(bool Started, PaymentCheckHistoryEntry? Entry)> TryRunAsync(string trigger, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            return (false, null);

        try
        {
            var previous = history.Last();
            var result = await runner.RunAsync(cancellationToken);

            var entry = new PaymentCheckHistoryEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Trigger = trigger,
                Status = result.Status,
                Step = result.Step,
                Detail = result.Detail,
                DurationMs = result.DurationMs,
            };

            var repeatedLoginWarning = trigger == PaymentCheckTrigger.Scheduled
                && result.Status == PaymentCheckStatus.NotLoggedIn
                && previous?.Status == PaymentCheckStatus.NotLoggedIn;

            if (repeatedLoginWarning)
            {
                entry.NotifyNote = "Login warning already sent; waiting for a new sign-in.";
            }
            else
            {
                await NotifyAsync(entry, result, cancellationToken);
            }

            history.Add(entry);
            jobs.Record(JobIds.PaymentCheck, result.IsOk, Describe(result));
            return (true, entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task NotifyAsync(PaymentCheckHistoryEntry entry, PaymentCheckResult result, CancellationToken cancellationToken)
    {
        var token = gmvSettings.ReadToken() ?? "";
        var chatId = gmvSettings.Load().ChatId;
        if (token.Length == 0 || chatId.Length == 0)
        {
            entry.NotifyNote = "Telegram is not set up. Enter the bot token and chat ID in GMV Notification.";
            return;
        }

        try
        {
            await telegram.SendAsync(token, chatId, Compose(result, DateTime.Now), cancellationToken);
            entry.Notified = true;
        }
        catch (GmvSendException ex)
        {
            entry.NotifyNote = ex.Message;
        }
    }

    public static string Describe(PaymentCheckResult result) => result.Status switch
    {
        PaymentCheckStatus.Ok => "The payment page opened normally.",
        _ => result.Detail ?? result.Status,
    };

    static string Compose(PaymentCheckResult result, DateTime now)
    {
        var time = now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        var seconds = (result.DurationMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);

        return result.Status switch
        {
            PaymentCheckStatus.Ok =>
                $"✅ Payment page OK\n{time}\nThe card-entry page (odeme.mediamarkt.com.tr) opened normally. ({seconds} s)",

            PaymentCheckStatus.Unreachable =>
                $"❌ PAYMENT PAGE PROBLEM\n{time}\nStep: {result.Step}\n{result.Detail}",

            PaymentCheckStatus.NotLoggedIn =>
                $"⚠️ Payment check could not run\n{time}\nThe storefront login has expired. Sign in again from the Payment Check panel. This says nothing about the payment page.",

            PaymentCheckStatus.CartEmpty =>
                $"⚠️ Payment check could not run\n{time}\nThe test account's cart is empty. Add a product to it. This says nothing about the payment page.",

            _ =>
                $"⚠️ Payment check could not run\n{time}\n{result.Detail}",
        };
    }
}
