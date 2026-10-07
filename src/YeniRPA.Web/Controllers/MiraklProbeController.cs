using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Diagnostic endpoints for the Mirakl session probe (see <see cref="MiraklSessionProbe"/>). Read-only
/// toward the marketplace; there is no screen for it yet.
/// </summary>
[ApiController]
[Route("api/automation/probe")]
public sealed class MiraklProbeController : ControllerBase
{
    readonly MiraklSessionProbe _probe;

    public MiraklProbeController(MiraklSessionProbe probe) => _probe = probe;

    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        success = true,
        message = "",
        data = new
        {
            running = _probe.IsRunning,
            gmvEndpoint = _probe.GmvEndpoint,
            logFile = _probe.LogPath,
            lines = _probe.RecentLines()
        }
    });

    /// <summary>Opens the dashboard once and finds the request that carries GMV.</summary>
    [HttpPost("discover")]
    public async Task<IActionResult> Discover(CancellationToken cancellationToken)
    {
        await _probe.DiscoverAsync(cancellationToken);
        return Ok(new { success = true, message = "Discovery finished. See the log lines.", data = new { gmvEndpoint = _probe.GmvEndpoint } });
    }

    /// <summary>Lists the GMV fields (path and value) found in the discovered dashboard responses.</summary>
    [HttpPost("sample")]
    public async Task<IActionResult> Sample()
    {
        var lines = await _probe.SampleAsync();
        return Ok(new { success = true, message = "", data = lines });
    }

    /// <summary>Lists the GMV the sales endpoint returns for each combination of the GMV switches.</summary>
    [HttpPost("variants")]
    public async Task<IActionResult> Variants()
    {
        var lines = await _probe.VariantsAsync();
        return Ok(new { success = true, message = "", data = lines });
    }

    /// <summary>Starts the keep-alive loop. The interval stays under the 30-minute idle limit.</summary>
    [HttpPost("start")]
    public IActionResult Start([FromQuery] int intervalMinutes = 10)
    {
        _probe.StartKeepAlive(TimeSpan.FromMinutes(Math.Clamp(intervalMinutes, 1, 25)));
        return Ok(new { success = true, message = "Probe started.", data = new { intervalMinutes = Math.Clamp(intervalMinutes, 1, 25) } });
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        await _probe.StopAsync();
        return Ok(new { success = true, message = "Probe stopped.", data = (object?)null });
    }
}
