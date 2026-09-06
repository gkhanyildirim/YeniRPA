using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Incidents report. The Mirakl incident panel exports open and closed incidents as two separate
/// downloads, so both arrive here; at least one has to be present and the one left out simply
/// contributes no rows. File names are forwarded to the builder because it picks the XLSX or CSV
/// reader from the extension.
/// </summary>
[ApiController]
[Route("api/incidents-report")]
public sealed class IncidentsReportController : ControllerBase
{
    [HttpPost("data")]
    public async Task<IActionResult> Data(
        IFormFile? openIncidents,
        IFormFile? closedIncidents,
        CancellationToken cancellationToken)
    {
        var hasOpen = openIncidents is { Length: > 0 };
        var hasClosed = closedIncidents is { Length: > 0 };

        if (!hasOpen && !hasClosed)
            return BadRequest(new { error = "Please upload at least one incident export: open incidents, closed incidents, or both." });

        using var openStream = hasOpen ? openIncidents!.OpenReadStream() : null;
        using var closedStream = hasClosed ? closedIncidents!.OpenReadStream() : null;

        var data = IncidentsReportBuilder.BuildData(
            openStream, openIncidents?.FileName,
            closedStream, closedIncidents?.FileName);

        return Ok(data);
    }
}
