using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Return SLA report. The Mirakl orders export is required; of the two return tracking templates at
/// least one has to be present, and the one left out simply contributes no rows. File names are
/// forwarded to the builder because it picks the XLSX or CSV reader from the extension.
/// </summary>
[ApiController]
[Route("api/return-sla-report")]
public sealed class ReturnSlaReportController : ControllerBase
{
    [HttpPost("data")]
    public async Task<IActionResult> Data(
        IFormFile? orders,
        IFormFile? templateA,
        IFormFile? templateB,
        CancellationToken cancellationToken)
    {
        if (orders is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the orders file (.xlsx or .csv)." });

        var hasTemplateA = templateA is { Length: > 0 };
        var hasTemplateB = templateB is { Length: > 0 };
        if (!hasTemplateA && !hasTemplateB)
            return BadRequest(new { error = "Please upload at least one return template: A (Marketplace Iade & Degisim Talepleri) or B (NNNNNN-MP.csv)." });

        using var ordersStream = await CopyToSeekableStreamAsync(orders, cancellationToken);
        using var templateAStream = hasTemplateA
            ? await CopyToSeekableStreamAsync(templateA!, cancellationToken)
            : null;
        using var templateBStream = hasTemplateB
            ? await CopyToSeekableStreamAsync(templateB!, cancellationToken)
            : null;

        var data = ReturnSlaReportBuilder.BuildData(
            ordersStream, orders.FileName,
            templateAStream, templateA?.FileName,
            templateBStream, templateB?.FileName);

        return Ok(data);
    }

    /// <summary>ClosedXML needs a seekable stream; the raw request body is not one.</summary>
    static async Task<MemoryStream> CopyToSeekableStreamAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        stream.Position = 0;
        return stream;
    }
}
