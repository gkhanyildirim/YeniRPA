using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services.Automation;
using YeniRPA.Web.Services.GmvNotification;
using YeniRPA.Web.Services.PaymentCheck;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// The Payment Check panel: schedule, the one-time storefront sign-in, a manual run, and the history.
/// New endpoints, so every response is <c>{ success, message, data }</c>. A run that finds a payment
/// problem is still a successful request — the problem is in <c>data.status</c>.
/// </summary>
[ApiController]
[Route("api/payment-check")]
public sealed class PaymentCheckController(
    IPaymentCheckSettingsStore settings,
    IPaymentCheckHistoryStore history,
    IGmvSettingsStore gmvSettings,
    StorefrontBrowser browser,
    PaymentCheckNotifier notifier) : ControllerBase
{
    public sealed class SettingsRequest
    {
        public bool Enabled { get; set; }
        public int IntervalMinutes { get; set; }
        public int WindowStartHour { get; set; }
        public int WindowEndHour { get; set; }
    }

    [HttpGet("settings")]
    public IActionResult GetSettings() => Success(string.Empty, SettingsView());

    [HttpPost("settings")]
    public IActionResult SaveSettings([FromBody] SettingsRequest request)
    {
        try
        {
            if (request.Enabled && !TelegramReady())
                throw new InvalidOperationException("Enter the bot token and the chat ID in GMV Notification before turning this job on.");

            settings.Update(new PaymentCheckSettingsUpdate(
                request.Enabled, request.IntervalMinutes, request.WindowStartHour, request.WindowEndHour));
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        return Success("Settings saved.", SettingsView());
    }

    [HttpPost("login/start")]
    public async Task<IActionResult> StartLogin()
    {
        try
        {
            await browser.StartLoginAsync();
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        return Success("A browser window opened. Sign in with Google, then click 'Save session'.", SettingsView());
    }

    [HttpPost("login/save")]
    public async Task<IActionResult> SaveLogin()
    {
        try
        {
            await browser.SaveSessionAsync();
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        return Success("The storefront session was saved.", SettingsView());
    }

    [HttpPost("login/clear")]
    public IActionResult ClearLogin()
    {
        browser.ClearSession();
        return Success("The saved storefront session was removed.", SettingsView());
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken cancellationToken)
    {
        var (started, entry) = await notifier.TryRunAsync(PaymentCheckTrigger.Manual, cancellationToken);
        if (!started || entry is null)
            return Failure("A payment check is already running.");

        if (entry.Status == PaymentCheckStatus.Error)
            return Failure(entry.Detail ?? "The check could not run.");

        var message = entry.Status == PaymentCheckStatus.Ok
            ? "The payment page opened normally."
            : entry.Detail ?? entry.Status;

        return Success(message, new
        {
            status = entry.Status,
            step = entry.Step,
            notified = entry.Notified,
            notifyNote = entry.NotifyNote,
        });
    }

    [HttpGet("history")]
    public IActionResult History([FromQuery] int count = 50) =>
        Success(string.Empty, history.Recent(count).Select(x => new
        {
            timestampUtc = DateTime.SpecifyKind(x.TimestampUtc, DateTimeKind.Utc),
            trigger = x.Trigger,
            status = x.Status,
            step = x.Step,
            detail = x.Detail,
            durationMs = x.DurationMs,
            notified = x.Notified,
            notifyNote = x.NotifyNote,
        }));

    bool TelegramReady() => gmvSettings.ReadToken() is not null && gmvSettings.Load().ChatId.Length > 0;

    object SettingsView()
    {
        var current = settings.Load();

        return new
        {
            enabled = current.Enabled,
            intervalMinutes = current.IntervalMinutes,
            windowStartHour = current.WindowStartHour,
            windowEndHour = current.WindowEndHour,
            hasSession = browser.HasSavedSession,
            sessionSavedUtc = browser.SessionSavedUtc is { } saved ? (DateTime?)DateTime.SpecifyKind(saved, DateTimeKind.Utc) : null,
            telegramReady = TelegramReady(),
            running = notifier.IsRunning,
        };
    }

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) => BadRequest(new { success = false, message, data = (object?)null });
}
