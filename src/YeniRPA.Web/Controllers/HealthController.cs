using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// System health: what <see cref="SystemLogActionFilter"/> and <see cref="AutomationJobBus"/> have
/// recorded about every report generated, setting saved, and automation run — a small standalone
/// page rather than another tab in <c>Home/Index</c>, since it is an occasional ops check rather than
/// a daily-use module.
///
/// <para>Every JSON action here follows the <c>{ success, message, data }</c> envelope — see
/// CLAUDE.md — since this whole controller was written after that rule existed.</para>
/// </summary>
[Route("Health")]
public sealed class HealthController : Controller
{
    readonly ISystemLogStore _logs;

    public HealthController(ISystemLogStore logs) => _logs = logs;

    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("Summary")]
    public IActionResult Summary() =>
        Ok(new { success = true, message = "", data = _logs.Summary() });

    [HttpGet("Categories")]
    public IActionResult Categories() =>
        Ok(new { success = true, message = "", data = _logs.Categories() });

    [HttpGet("Logs")]
    public IActionResult Logs(string? category, string? status, string? search, int page = 1, int pageSize = 25) =>
        Ok(new { success = true, message = "", data = _logs.Query(category, status, search, page, pageSize) });
}
