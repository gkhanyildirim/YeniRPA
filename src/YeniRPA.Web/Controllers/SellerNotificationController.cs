using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Seller Notification automation: given a list of order IDs and a topic/message, opens each
/// order's Mirakl conversation dialog and sends it. No prepare/review step, the same shape as
/// <see cref="MarkAsReceivedController"/> — the source feature this was ported from never had one
/// either. Session/login/status/stop/events all stay on the shared <see cref="AutomationController"/>.
///
/// <para>Every endpoint here is new, so per this app's convention every response uses the
/// <c>{ success, message, data }</c> envelope — the ~20 pre-existing endpoints keep their own
/// bespoke shapes, this module is not one of them.</para>
/// </summary>
[ApiController]
[Route("api/seller-notification")]
public sealed class SellerNotificationController : ControllerBase
{
    readonly ISellerNotificationStore _store;
    readonly SellerNotificationRunner _runner;

    public SellerNotificationController(ISellerNotificationStore store, SellerNotificationRunner runner)
    {
        _store = store;
        _runner = runner;
    }

    [HttpGet("templates")]
    public IActionResult GetTemplates() =>
        Ok(new { success = true, message = (string?)null, data = _store.Load().Templates });

    [HttpPut("templates")]
    public IActionResult SaveTemplates([FromBody] SellerNotificationTemplateFile file)
    {
        if (file is null)
            return BadRequest(new { success = false, message = "No templates were posted.", data = (object?)null });

        _store.Save(file);
        return Ok(new
        {
            success = true,
            message = $"Saved {file.Templates?.Count ?? 0} template(s).",
            data = _store.Load().Templates
        });
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start(
        IFormFile? file,
        // [FromForm] is required: [ApiController] infers query-string binding for simple types, so
        // without it a pasted textarea silently arrives as null no matter what the operator typed.
        [FromForm] string? orders,
        [FromForm] string? topic,
        [FromForm] string? message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return BadRequest(new { success = false, message = "Topic cannot be empty.", data = (object?)null });

        if (string.IsNullOrWhiteSpace(message))
            return BadRequest(new { success = false, message = "Message cannot be empty.", data = (object?)null });

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
                success = false,
                message = "No order IDs were found. Upload a .txt file (one order ID per line) or paste them below.",
                data = (object?)null
            });
        }

        if (orderIds.Count > SellerNotificationRunner.MaxOrdersPerRun)
        {
            return BadRequest(new
            {
                success = false,
                message = $"{orderIds.Count} order IDs is over the {SellerNotificationRunner.MaxOrdersPerRun}-order " +
                           "limit for one run. Narrow the list and run it in batches.",
                data = (object?)null
            });
        }

        if (!_runner.TryStart(orderIds, topic.Trim(), message))
        {
            return BadRequest(new
            {
                success = false,
                message = "An automation run is already in progress. Wait for it to finish.",
                data = (object?)null
            });
        }

        return Ok(new { success = true, message = (string?)null, data = new { count = orderIds.Count } });
    }
}
