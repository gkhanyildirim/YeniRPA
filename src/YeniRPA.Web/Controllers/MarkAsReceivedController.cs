using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Mark as Received automation: given a list of order IDs, clicks "Mark as received" for each on
/// Mirakl. The simplest of the automation modules — no prepare/review step, because the source
/// feature this was ported from never had one either. One endpoint, straight from input to run.
/// </summary>
[ApiController]
[Route("api/mark-received")]
public sealed class MarkAsReceivedController : ControllerBase
{
    readonly MarkAsReceivedRunner _runner;

    public MarkAsReceivedController(MarkAsReceivedRunner runner) => _runner = runner;

    [HttpPost("start")]
    public async Task<IActionResult> Start(
        IFormFile? file,
        // [FromForm] is required: [ApiController] infers query-string binding for simple types, so
        // without it a pasted textarea silently arrives as null no matter what the operator typed.
        [FromForm] string? orders,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();

        if (file is { Length: > 0 })
        {
            using var reader = new StreamReader(file.OpenReadStream());
            lines.AddRange((await reader.ReadToEndAsync(cancellationToken)).Split('\n'));
        }

        if (!string.IsNullOrWhiteSpace(orders))
            lines.AddRange(orders.Split('\n'));

        var orderIds = lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (orderIds.Count == 0)
        {
            return BadRequest(new
            {
                error = "No order IDs were found. Upload a .txt file (one order ID per line) or paste them below."
            });
        }

        if (orderIds.Count > MarkAsReceivedRunner.MaxOrdersPerRun)
        {
            return BadRequest(new
            {
                error = $"{orderIds.Count} order IDs is over the {MarkAsReceivedRunner.MaxOrdersPerRun}-order " +
                        "limit for one run. Narrow the list and run it in batches."
            });
        }

        if (!_runner.TryStart(orderIds))
            return BadRequest(new { error = "An automation run is already in progress. Wait for it to finish." });

        return Ok(new { count = orderIds.Count });
    }
}
