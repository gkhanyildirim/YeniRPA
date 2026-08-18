using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Ticket → Seller lookup. Both uploads are required: the Oracle case list supplies the tickets and
/// the Mirakl orders export supplies the sellers. File names are forwarded to the builder because it
/// picks the XLSX or CSV reader from the extension.
/// </summary>
[ApiController]
[Route("api/ticket-seller")]
public sealed class TicketSellerController : ControllerBase
{
    [HttpPost("data")]
    public async Task<IActionResult> Data(
        IFormFile? tickets,
        IFormFile? orders,
        CancellationToken cancellationToken)
    {
        if (tickets is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the case list (.xlsx or .csv)." });
        if (orders is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the orders file (.xlsx or .csv)." });

        using var ticketsStream = await CopyToSeekableStreamAsync(tickets, cancellationToken);
        using var ordersStream = await CopyToSeekableStreamAsync(orders, cancellationToken);

        var data = TicketSellerBuilder.BuildData(
            ticketsStream, tickets.FileName,
            ordersStream, orders.FileName);

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
