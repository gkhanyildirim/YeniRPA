using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Cargo Seller Report: matches a cargo-invoice export, grouped by every seller it holds, against the
/// Marketplace return/exchange export and the MM Pazaryeri cargo data export to recover each
/// shipment's order number. Run once a month over the whole file — <c>generate</c> processes every
/// seller in one pass and <c>download</c> hands back one workbook per seller, zipped together. See
/// <see cref="CargoSellerReportBuilder"/> for the matching rules.
///
/// <para>Every endpoint here is new, so it uses the <c>{ success, message, data }</c> envelope per
/// CLAUDE.md, following <see cref="Track17Controller"/>'s pattern.</para>
/// </summary>
[ApiController]
[Route("api/cargo-seller-report")]
public sealed class CargoSellerReportController : ControllerBase
{
    const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    const string ZipContentType = "application/zip";

    readonly CargoSellerReportStore _store;

    public CargoSellerReportController(CargoSellerReportStore store)
    {
        _store = store;
    }

    public sealed record DownloadRequest([property: JsonPropertyName("batchId")] string? BatchId);

    [HttpPost("analyze")]
    public IActionResult Analyze(IFormFile? kargoFile, [FromForm] string? sellerColumn)
    {
        if (kargoFile is not { Length: > 0 })
            return Failure("Upload the cargo invoice file first.");

        try
        {
            using var stream = kargoFile.OpenReadStream();
            var result = CargoSellerReportBuilder.Analyze(stream, kargoFile.FileName, sellerColumn);

            var message = result.SellerColumnResolved
                ? $"Found {result.Sellers.Count:N0} seller(s) in column '{result.SellerColumn}'."
                : "Could not tell which column holds the seller - pick it from the file's headers.";

            return Success(message, result);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }
        catch (Exception ex)
        {
            return Failure($"'{kargoFile.FileName}' could not be read as a spreadsheet. {ex.Message}");
        }
    }

    [HttpPost("generate")]
    public IActionResult Generate(
        IFormFile? kargoFile, IFormFile? marketplaceFile, IFormFile? mmFile,
        [FromForm] string? sellerColumn)
    {
        if (kargoFile is not { Length: > 0 })
            return Failure("Upload the cargo invoice file first.");
        if (marketplaceFile is not { Length: > 0 })
            return Failure("Upload the Marketplace return/exchange file first.");
        if (mmFile is not { Length: > 0 })
            return Failure("Upload the MM Pazaryeri cargo data file first.");
        if (string.IsNullOrWhiteSpace(sellerColumn))
            return Failure("The seller column was not provided.");

        try
        {
            using var kargoStream = kargoFile.OpenReadStream();
            using var marketplaceStream = marketplaceFile.OpenReadStream();
            using var mmStream = mmFile.OpenReadStream();

            var results = CargoSellerReportBuilder.GenerateAll(
                kargoStream, kargoFile.FileName, sellerColumn,
                marketplaceStream, marketplaceFile.FileName,
                mmStream, mmFile.FileName);

            var batchId = _store.Put(results);
            var summary = CargoSellerReportBuilder.Summarize(results);

            return Success(
                $"{summary.TotalMatched:N0} of {summary.TotalRecords:N0} record(s) matched across " +
                $"{summary.SellerCount:N0} seller(s).",
                new { batchId, summary });
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }
        catch (Exception ex)
        {
            return Failure($"The uploaded files could not be read. {ex.Message}");
        }
    }

    [HttpPost("download")]
    public IActionResult Download([FromBody] DownloadRequest? request)
    {
        var results = _store.Get(request?.BatchId);
        if (results is null)
            return Failure("The report has expired - upload the files again and generate it once more.");

        var now = DateTime.Now;
        var bytes = CargoSellerReportBuilder.BuildZip(results, now);
        var fileName = $"kargo_raporlari_{now:yyyyMMdd_HHmmss}.zip";

        return File(bytes, ZipContentType, fileName);
    }

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) => BadRequest(new { success = false, message, data = (object?)null });
}
