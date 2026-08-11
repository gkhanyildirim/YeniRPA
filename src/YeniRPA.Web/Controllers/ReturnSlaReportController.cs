using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Return SLA report. Needs three uploads on every run: the Mirakl orders export plus the two return
/// tracking templates. File names are forwarded to the builder because it picks the XLSX or CSV
/// reader from the extension.
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
        if (templateA is not { Length: > 0 })
            return BadRequest(new { error = "Please upload return template A (Marketplace Iade & Degisim Talepleri)." });
        if (templateB is not { Length: > 0 })
            return BadRequest(new { error = "Please upload return template B (NNNNNN-MP.csv)." });

        using var ordersStream = await CopyToSeekableStreamAsync(orders, cancellationToken);
        using var templateAStream = await CopyToSeekableStreamAsync(templateA, cancellationToken);
        using var templateBStream = await CopyToSeekableStreamAsync(templateB, cancellationToken);

        var data = ReturnSlaReportBuilder.BuildData(
            ordersStream, orders.FileName,
            templateAStream, templateA.FileName,
            templateBStream, templateB.FileName);

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
