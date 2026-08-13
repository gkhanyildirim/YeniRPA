using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Everything the automation modules share: the Mirakl login they all run under, and the event
/// stream they all report progress on.
/// </summary>
[ApiController]
[Route("api/automation")]
public sealed class AutomationController : ControllerBase
{
    readonly MiraklBrowser _browser;
    readonly AutomationJobBus _bus;

    public AutomationController(MiraklBrowser browser, AutomationJobBus bus)
    {
        _browser = browser;
        _bus = bus;
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        hasSession = _browser.HasSavedSession,
        browserReady = _browser.IsBrowserReady,
        isRunning = _bus.IsRunning,
        runningModule = _bus.RunningModule
    });

    /// <summary>Opens a real browser window on the Mirakl login page. Blocks until Chrome is up.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login()
    {
        await _browser.StartLoginAsync();
        return Ok();
    }

    [HttpPost("save-session")]
    public async Task<IActionResult> SaveSession()
    {
        await _browser.SaveSessionAsync();
        return Ok();
    }

    [HttpPost("clear-session")]
    public IActionResult ClearSession()
    {
        _browser.ClearSession();
        return Ok();
    }

    /// <summary>
    /// Server-Sent Events: the current run's log replayed, then live progress as it happens. The
    /// browser's own <c>EventSource</c> handles reconnection, so nothing here retries.
    /// </summary>
    [HttpGet("events")]
    public async Task Events(CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache,no-store";
        // Harmless locally, and stops a reverse proxy from buffering the stream into silence.
        Response.Headers["X-Accel-Buffering"] = "no";

        await Response.Body.FlushAsync(cancellationToken);

        try
        {
            // Every payload is a single-line JSON object, so it needs no SSE-specific escaping.
            await foreach (var payload in _bus.ReadAllAsync(cancellationToken))
            {
                await Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The tab was closed or navigated away. Nothing to report — the run is unaffected.
        }
    }
}
