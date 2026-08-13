using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Create Return automation, plus the step that builds its input list.
///
/// <c>prepare</c> turns the four exports into a reviewable list; <c>list/excel</c> and
/// <c>start-list</c> then take that list back as JSON rather than asking for the files again — the
/// orders export alone is ~30 MB and re-uploading it to produce a two-column workbook is waste.
/// <c>start</c> stays for the hand-made workbook the operator has always been able to upload.
///
/// Every entry point validates synchronously so a bad input is rejected with a message; the run
/// itself happens in the background and reports on <c>/api/automation/events</c>.
/// </summary>
[ApiController]
[Route("api/create-return")]
public sealed class CreateReturnController : ControllerBase
{
    const int OrderIdColumn = 1;
    const int TrackingNumberColumn = 2;
    const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    readonly CreateReturnRunner _runner;

    public CreateReturnController(CreateReturnRunner runner) => _runner = runner;

    /// <summary>One prepared row on its way back from the browser.</summary>
    public sealed record ListRow(
        [property: JsonPropertyName("orderNumber")] string? OrderNumber,
        [property: JsonPropertyName("trackingNumber")] string? TrackingNumber);

    public sealed record ListRequest(
        [property: JsonPropertyName("rows")] IReadOnlyList<ListRow>? Rows);

    // -----------------------------------------------------------------
    // Prepare
    // -----------------------------------------------------------------

    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare(
        IFormFile? templateA,
        IFormFile? templateB,
        IFormFile? returns,
        IFormFile? orders,
        // [FromForm] is required: [ApiController] infers query-string binding for simple types, so
        // without it the filters silently arrive empty and every row passes the date range.
        [FromForm] DateTime? from,
        [FromForm] DateTime? to,
        [FromForm] bool returnsOnly,
        CancellationToken cancellationToken)
    {
        if (templateA is not { Length: > 0 })
            return BadRequest(new { error = "Please upload return template A (Marketplace Iade & Degisim Talepleri)." });
        if (templateB is not { Length: > 0 })
            return BadRequest(new { error = "Please upload return template B (the MP file)." });
        if (returns is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the returns export." });
        if (orders is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the orders export." });

        if (from.HasValue && to.HasValue && from.Value.Date > to.Value.Date)
            return BadRequest(new { error = "The start of the date range is after its end." });

        using var templateAStream = await CopyToSeekableStreamAsync(templateA, cancellationToken);
        using var templateBStream = await CopyToSeekableStreamAsync(templateB, cancellationToken);
        using var returnsStream = await CopyToSeekableStreamAsync(returns, cancellationToken);
        using var ordersStream = await CopyToSeekableStreamAsync(orders, cancellationToken);

        var data = ReturnListBuilder.Build(
            templateAStream, templateA.FileName,
            templateBStream, templateB.FileName,
            returnsStream, returns.FileName,
            ordersStream, orders.FileName,
            new ReturnListOptions(from, to, returnsOnly));

        return Ok(data);
    }

    /// <summary>The prepared list as the same two-column workbook the operator used to build by hand.</summary>
    [HttpPost("list/excel")]
    public IActionResult ListExcel([FromBody] ListRequest? request)
    {
        var rows = ParseRows(request);
        if (rows.Count == 0)
            return BadRequest(new { error = "There is nothing to export." });

        return File(BuildWorkbook(rows), XlsxContentType, $"create-return-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    /// <summary>Runs the prepared list directly, with no download-and-upload round trip.</summary>
    [HttpPost("start-list")]
    public IActionResult StartList([FromBody] ListRequest? request)
    {
        var rows = ParseRows(request);
        if (rows.Count == 0)
            return BadRequest(new { error = "There is nothing to run. Prepare the list first." });

        if (!_runner.TryStart(rows))
            return BadRequest(new { error = "An automation run is already in progress. Wait for it to finish." });

        return Ok(new { count = rows.Count });
    }

    // -----------------------------------------------------------------
    // Hand-made workbook
    // -----------------------------------------------------------------

    [HttpPost("start")]
    public async Task<IActionResult> Start(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = "Please upload an Excel file (.xlsx)." });

        List<ReturnRow> rows;
        try
        {
            rows = await ReadRowsAsync(file, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { error = $"The uploaded file could not be read as an Excel workbook: {ex.Message}" });
        }

        if (rows.Count == 0)
        {
            return BadRequest(new
            {
                error = "No usable rows were found. Column A must hold the order ID and column B the tracking number, " +
                        "with the first row as a header."
            });
        }

        if (!_runner.TryStart(rows))
            return BadRequest(new { error = "An automation run is already in progress. Wait for it to finish." });

        return Ok(new { count = rows.Count });
    }

    /// <summary>Column A = order ID, column B = tracking number, first used row is the header.</summary>
    static async Task<List<ReturnRow>> ReadRowsAsync(IFormFile file, CancellationToken cancellationToken)
    {
        // ClosedXML needs a seekable stream; the raw request body is not one.
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        stream.Position = 0;

        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();

        var rows = new List<ReturnRow>();
        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var orderId = row.Cell(OrderIdColumn).GetString().Trim();
            var trackingNumber = row.Cell(TrackingNumberColumn).GetString().Trim();

            // A row missing either half cannot produce a return, so it is dropped rather than
            // failing the whole upload.
            if (orderId.Length > 0 && trackingNumber.Length > 0)
                rows.Add(new ReturnRow(orderId, trackingNumber));
        }

        return rows;
    }

    // -----------------------------------------------------------------

    static List<ReturnRow> ParseRows(ListRequest? request)
    {
        if (request?.Rows is null)
            return [];

        return [.. request.Rows
            .Select(r => new ReturnRow((r.OrderNumber ?? "").Trim(), (r.TrackingNumber ?? "").Trim()))
            .Where(r => r.OrderId.Length > 0 && r.TrackingNumber.Length > 0)];
    }

    /// <summary>
    /// The plain two-column shape <see cref="ReadRowsAsync"/> expects — deliberately not
    /// <c>TableWorkbookBuilder</c>, which writes a styled report with title rows above the data.
    /// </summary>
    static byte[] BuildWorkbook(IReadOnlyList<ReturnRow> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Create Return");

        sheet.Cell(1, OrderIdColumn).Value = "Order number";
        sheet.Cell(1, TrackingNumberColumn).Value = "Tracking number";
        sheet.Row(1).Style.Font.Bold = true;

        // Text, not numbers: a 12-digit tracking code stored as a number comes back out of
        // GetString() as "6.02679E+11", and leading zeros would be gone for good.
        sheet.Column(OrderIdColumn).Style.NumberFormat.Format = "@";
        sheet.Column(TrackingNumberColumn).Style.NumberFormat.Format = "@";

        for (var i = 0; i < rows.Count; i++)
        {
            sheet.Cell(i + 2, OrderIdColumn).SetValue(rows[i].OrderId);
            sheet.Cell(i + 2, TrackingNumberColumn).SetValue(rows[i].TrackingNumber);
        }

        sheet.Columns(OrderIdColumn, TrackingNumberColumn).AdjustToContents();

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
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
