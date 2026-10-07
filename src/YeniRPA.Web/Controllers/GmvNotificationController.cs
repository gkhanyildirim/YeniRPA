using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services.GmvNotification;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// The GMV Notification panel: Telegram and schedule settings, the manual buttons, and the send
/// history. New endpoints, so every response is <c>{ success, message, data }</c>. The bot token goes
/// in but never comes back out — the browser only learns whether one is saved.
/// </summary>
[ApiController]
[Route("api/gmv")]
public sealed class GmvNotificationController(
    IGmvSettingsStore settings,
    IGmvHistoryStore history,
    IGmvReader reader,
    GmvNotifier notifier) : ControllerBase
{
    public sealed class SettingsRequest
    {
        public bool Enabled { get; set; }
        public string? Mode { get; set; }
        public List<string>? Times { get; set; }
        public int WindowStartHour { get; set; } = 9;
        public int WindowEndHour { get; set; } = 19;
        public string? ChatId { get; set; }
        public string? Token { get; set; }
        public double? DailyTarget { get; set; }
    }

    public sealed class CheckRequest
    {
        public bool Send { get; set; }
    }

    [HttpGet("settings")]
    public IActionResult GetSettings() => Success(string.Empty, SettingsView());

    [HttpPost("settings")]
    public IActionResult SaveSettings([FromBody] SettingsRequest request)
    {
        try
        {
            settings.Save(new GmvSettingsUpdate(
                request.Enabled, request.Mode, request.Times, request.WindowStartHour,
                request.WindowEndHour, request.ChatId, request.Token, request.DailyTarget));
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        return Success("Settings saved.", SettingsView());
    }

    [HttpPost("clear-token")]
    public IActionResult ClearToken()
    {
        settings.ClearToken();
        return Success("The bot token was removed and notifications were switched off.", SettingsView());
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken cancellationToken) =>
        FromOutcome(await notifier.SendTestAsync(cancellationToken));

    [HttpPost("check")]
    public async Task<IActionResult> Check([FromBody] CheckRequest? request, CancellationToken cancellationToken) =>
        FromOutcome(await notifier.CheckNowAsync(request?.Send ?? false, cancellationToken));

    [HttpGet("history")]
    public IActionResult History([FromQuery] int count = 50) =>
        Success(string.Empty, history.Recent(count).Select(x => new
        {
            timestampUtc = DateTime.SpecifyKind(x.TimestampUtc, DateTimeKind.Utc),
            trigger = x.Trigger,
            gmv = x.Gmv,
            status = x.Status,
            error = x.Error,
        }));

    object SettingsView()
    {
        var current = settings.Load();
        var session = reader.Status;

        return new
        {
            enabled = current.Enabled,
            mode = current.Mode,
            times = current.Times,
            windowStartHour = current.WindowStartHour,
            windowEndHour = current.WindowEndHour,
            chatId = current.ChatId,
            hasToken = current.ProtectedToken is not null,
            dailyTarget = current.DailyTarget,
            keepAliveMinutes = GmvNotificationWorker.KeepAliveMinutes,
            session = new { state = session.State, checkedUtc = session.CheckedUtc },
        };
    }

    IActionResult FromOutcome(GmvOutcome outcome) =>
        outcome.Success
            ? Success(outcome.Message, new { status = outcome.Status, gmv = outcome.Gmv })
            : Failure(outcome.Message);

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) => BadRequest(new { success = false, message, data = (object?)null });
}
