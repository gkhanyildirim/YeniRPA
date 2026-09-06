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

        using var ticketsStream = tickets.OpenReadStream();
        using var ordersStream = orders.OpenReadStream();

        var data = TicketSellerBuilder.BuildData(
            ticketsStream, tickets.FileName,
            ordersStream, orders.FileName);

        return Ok(data);
    }
}
