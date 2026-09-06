using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Infrastructure;

/// <summary>
/// Records one <see cref="SystemLog"/> row for every <c>POST</c> action in the app — a report
/// generated, a settings save, a database import, an automation run accepted.
///
/// <para><b>Only <c>POST</c>.</b> A <c>GET</c> here is a status poll or a dashboard data read, not an
/// operation with a meaningful "did it work" — logging every one of those would swamp the health
/// dashboard with noise nobody asked to see.</para>
///
/// <para><b>What this does and does not capture for automation.</b> A module like Create Return
/// answers its <c>POST</c> as soon as the run is accepted, minutes before the browser automation
/// itself finishes — so the row this filter writes says "the request to start was accepted",
/// not "the automation succeeded". The real outcome, with the run's actual duration, is a second,
/// separate row written by <see cref="Automation.AutomationJobBus"/> once the run reports done. Both
/// are real, honest signals; they just answer different questions, and an operator reading the log
/// sees both a request-level line and a run-level line for the same click.</para>
///
/// <para>Registered once, globally, next to <see cref="ReportExceptionFilter"/> — see
/// <c>Program.cs</c>. It only observes <c>context.Exception</c>; it never sets
/// <c>ExceptionHandled</c>, so <see cref="ReportExceptionFilter"/> still gets to turn a builder's
/// <see cref="InvalidOperationException"/> into its usual <c>400 {"error"}</c> afterwards.</para>
/// </summary>
public sealed class SystemLogActionFilter : IAsyncActionFilter
{
    readonly ISystemLogStore _logs;

    public SystemLogActionFilter(ISystemLogStore logs) => _logs = logs;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method))
        {
            await next();
            return;
        }

        var (category, operation) = Describe(context);
        var stopwatch = Stopwatch.StartNew();

        var executed = await next();

        stopwatch.Stop();

        // Two failure conventions exist in this app (see CLAUDE.md's note on the { success, message,
        // data } envelope being forward-only): an older one throws and lets ReportExceptionFilter turn
        // it into 400 { error } — that leaves context.Exception set here, before that filter ever
        // runs. A newer one (e.g. SettingsController.ImportDatabase) catches its own exception and
        // returns { success: false, ... } directly, which never sets Exception at all. Both still
        // resolve to a >= 400 status on the IActionResult itself, so that is what actually decides.
        var statusCode = executed.Result switch
        {
            ObjectResult obj => obj.StatusCode,
            StatusCodeResult sc => sc.StatusCode,
            _ => null,
        };

        var failed = executed.Exception is not null || statusCode is >= 400;
        var status = failed ? SystemLogStatus.Error : SystemLogStatus.Success;

        // Only a failure's own explanation is worth surfacing as "Detay" — a success message would
        // just repeat what the row's Success badge already says.
        var detail = failed ? executed.Exception?.Message ?? DetailFrom(executed.Result) : null;

        _logs.Record(category, operation, status, stopwatch.ElapsedMilliseconds, detail);
    }

    /// <summary>The <c>message</c>/<c>error</c> field off a JSON result, when there is one worth
    /// keeping — a failed action's own explanation, without this filter having to know every
    /// controller's exact response shape.</summary>
    static string? DetailFrom(IActionResult? result)
    {
        if (result is not ObjectResult { Value: { } value })
            return null;

        var type = value.GetType();
        var text = (type.GetProperty("message")?.GetValue(value)
            ?? type.GetProperty("error")?.GetValue(value)) as string;

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    static (string Category, string Operation) Describe(ActionExecutingContext context)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor descriptor)
            return (descriptor.ControllerName, descriptor.ActionName);

        // Every action in this app is a controller action; this is only a fallback for whatever
        // ASP.NET Core's own conventions might route here in the future.
        return ("App", context.ActionDescriptor.DisplayName ?? "Unknown");
    }
}
