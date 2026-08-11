using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Downloads a single dashboard section as a workbook. The dashboards aggregate in the browser, so
/// the rows arrive from the client already filtered and formatted — this endpoint only puts them into
/// an .xlsx. It is deliberately generic: every card on every report goes through the one action.
/// </summary>
[ApiController]
[Route("api/export")]
public sealed class TableExportController : ControllerBase
{
    const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [HttpPost("xlsx")]
    public IActionResult Xlsx([FromBody] TableExportRequest? request)
    {
        if (request is null)
            return BadRequest(new { error = "Nothing to export." });

        var bytes = TableWorkbookBuilder.Build(request);
        return File(bytes, XlsxContentType, TableWorkbookBuilder.FileName(request.FileName ?? request.Title));
    }
}
