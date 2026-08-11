using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Late Shipment &amp; Cancellation report. Both actions take the same Mirakl orders export and
/// differ only in the shape they return: JSON for the in-page dashboard, XLSX for download.
/// </summary>
[ApiController]
[Route("api/order-report")]
public sealed class OrderReportController : ControllerBase
{
    const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    const string MissingFileMessage = "Please upload the orders Excel file (.xlsx).";

    [HttpPost("data")]
    public async Task<IActionResult> Data(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = MissingFileMessage });

        using var stream = await CopyToSeekableStreamAsync(file, cancellationToken);
        return Ok(OrderReportBuilder.BuildData(stream));
    }

    [HttpPost("excel")]
    public async Task<IActionResult> Excel(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = MissingFileMessage });

        using var stream = await CopyToSeekableStreamAsync(file, cancellationToken);
        var bytes = OrderReportBuilder.Build(stream);

        return File(bytes, XlsxContentType, "Gec Kargolama ve Iptal Raporu.xlsx");
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
