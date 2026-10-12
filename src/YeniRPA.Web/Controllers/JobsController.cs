using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services.GmvNotification;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// The Jobs screen: the background jobs, when each last ran and runs next, and their schedule. New
/// endpoints, so every response is <c>{ success, message, data }</c>. Changes are written to the same
/// settings document the GMV Notification panel uses, and the worker re-reads it every 30 seconds, so a
/// saved change applies without a restart.
///
/// <para>Adding a job means adding one entry to <see cref="Describe"/> (and, if it has a schedule of its
/// own, a branch in <see cref="Update"/>); the screen renders whatever the list says.</para>
/// </summary>
[ApiController]
[Route("api/jobs")]
public sealed class JobsController(
    IGmvSettingsStore settings,
    IGmvReader reader,
    JobStatusTracker tracker) : ControllerBase
{
    public sealed class JobUpdateRequest
    {
        public bool? Enabled { get; set; }
        public int? IntervalMinutes { get; set; }
        public int? WindowStartHour { get; set; }
        public int? WindowEndHour { get; set; }
    }

    [HttpGet]
    public IActionResult List() => Success(string.Empty, Describe());

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] JobUpdateRequest request)
    {
        try
        {
            switch (id)
            {
                case JobIds.GmvReport:
                    var current = settings.Load();
                    settings.UpdateSchedule(new GmvScheduleUpdate(
                        request.Enabled,
                        // This row says "every N minutes", so saving it that way means that mode;
                        // otherwise a saved "manual" or "specific times" would ignore the interval.
                        // Switching the job off is the one save that leaves the mode alone.
                        request.IntervalMinutes is not null && request.Enabled != false
                            && current.Mode != GmvMode.Interval ? GmvMode.Interval : null,
                        request.IntervalMinutes,
                        request.WindowStartHour,
                        request.WindowEndHour));
                    break;

                case JobIds.SessionKeepAlive:
                    settings.UpdateSchedule(new GmvScheduleUpdate(KeepAliveMinutes: request.IntervalMinutes));
                    break;

                default:
                    return NotFound(new { success = false, message = "There is no such job.", data = (object?)null });
            }
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message, data = (object?)null });
        }

        return Success("Saved. The new schedule applies from the next check.", Describe());
    }

    object Describe()
    {
        var config = settings.Load();
        var now = DateTime.Now;
        var report = tracker.Get(JobIds.GmvReport);
        var keepAlive = tracker.Get(JobIds.SessionKeepAlive);
        var session = reader.Status;

        var reportNext = config.Enabled ? GmvSchedule.Next(config, now) : null;
        var keepAliveNext = config.Enabled && keepAlive.LastRunUtc is { } last
            ? DateTime.SpecifyKind(last, DateTimeKind.Utc).AddMinutes(config.KeepAliveMinutes)
            : (DateTime?)null;

        return new[]
        {
            new
            {
                id = JobIds.GmvReport,
                name = "GMV Telegram notification",
                description = "Reads today's GMV from the Marketplace dashboard and sends it to Telegram.",
                enabled = config.Enabled,
                canToggle = true,
                mode = (string?)config.Mode,
                intervalMinutes = (int?)config.IntervalMinutes,
                minMinutes = GmvLimits.MinIntervalMinutes,
                maxMinutes = GmvLimits.MaxIntervalMinutes,
                hasWindow = true,
                windowStartHour = (int?)config.WindowStartHour,
                windowEndHour = (int?)config.WindowEndHour,
                lastRunUtc = AsUtc(report.LastRunUtc),
                nextRunUtc = reportNext is null ? null : (DateTime?)reportNext.Value.ToUniversalTime(),
                lastOk = report.Ok,
                lastMessage = report.Message,
                note = config.Mode == GmvMode.Interval
                    ? (string?)null
                    : $"Frequency is set to '{config.Mode}' in GMV Notification. Saving an interval here switches it to \"every N minutes\".",
            },
            new
            {
                id = JobIds.SessionKeepAlive,
                name = "Session keep-alive",
                description = "A light request that keeps the Marketplace login from timing out. Runs while GMV notifications are on.",
                enabled = config.Enabled,
                // It runs whenever GMV notifications are on; there is no separate switch for it.
                canToggle = false,
                mode = (string?)null,
                intervalMinutes = (int?)config.KeepAliveMinutes,
                minMinutes = GmvLimits.MinKeepAliveMinutes,
                maxMinutes = GmvLimits.MaxKeepAliveMinutes,
                hasWindow = false,
                windowStartHour = (int?)null,
                windowEndHour = (int?)null,
                lastRunUtc = AsUtc(keepAlive.LastRunUtc),
                nextRunUtc = keepAliveNext?.ToUniversalTime(),
                lastOk = keepAlive.Ok,
                lastMessage = keepAlive.Message ?? SessionText(session),
                note = (string?)"Keep it under 25 minutes: the Marketplace ends a login after 30 idle minutes.",
            },
        };
    }

    static DateTime? AsUtc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    static string? SessionText(GmvSessionStatus session) => session.State switch
    {
        "valid" => "The login is valid.",
        "expired" => "The login has expired. Sign in again.",
        "none" => "No saved Marketplace session.",
        _ => null,
    };

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });
}
