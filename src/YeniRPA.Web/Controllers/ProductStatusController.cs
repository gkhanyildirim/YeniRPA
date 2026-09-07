using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Product Status automation: given a list of seller names, reads each seller's catalogue breakdown off
/// Mirakl and leaves the resulting table where the page can fetch it.
///
/// <para>Read-only, unlike the other two Mirakl modules — nothing here writes to the marketplace. The
/// result is not returned from <c>start</c>: the scrape takes minutes, so the POST only accepts the
/// batch and the table is collected from <c>result</c> once the run reports done.</para>
/// </summary>
[ApiController]
[Route("api/product-status")]
public sealed class ProductStatusController : ControllerBase
{
    readonly ProductStatusRunner _runner;
    readonly ProductStatusStore _store;

    public ProductStatusController(ProductStatusRunner runner, ProductStatusStore store)
    {
        _runner = runner;
        _store = store;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start(
        IFormFile? file,
        // [FromForm] is required: [ApiController] infers query-string binding for simple types, so
        // without it a pasted textarea silently arrives as null no matter what the operator typed.
        [FromForm] string? sellers,
        CancellationToken cancellationToken)
    {
        List<string>? fileRows = null;

        if (file is { Length: > 0 })
        {
            List<List<string>> table;
            try
            {
                using var stream = file.OpenReadStream();
                table = TabularFile.Read(stream, file.FileName);
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    error = $"'{file.FileName}' could not be read as a spreadsheet. {ex.Message}"
                });
            }

            // First row is a header, first column holds the names — the shape the source module read.
            // The header is still here rather than already dropped, because the account has to say
            // what was skipped: an operator whose file has no header only finds out by reading it back.
            fileRows = [.. table.Select(row => TabularFile.GetCell(row, 0))];
        }

        var (sellerNames, intake) = ProductStatusIntake.ParseLines(fileRows, sellers);

        if (sellerNames.Count == 0)
        {
            return BadRequest(new
            {
                error = $"None of the {intake.FileRows + intake.PastedLines} submitted line(s) is a seller name. " +
                        "Upload a file with the names in the first column, or paste them below."
            });
        }

        if (sellerNames.Count > ProductStatusRunner.MaxSellersPerRun)
        {
            // Says both figures: the limit is checked after de-duplication, so "765 rows" and
            // "512 sellers" are different numbers and quoting only the second one reads as a
            // miscount to whoever is looking at the spreadsheet.
            return BadRequest(new
            {
                error = $"{intake.FileRows + intake.PastedLines} submitted line(s) came to {sellerNames.Count} " +
                        $"distinct sellers, over the {ProductStatusRunner.MaxSellersPerRun}-seller limit for " +
                        "one run. Narrow the list and run it in batches."
            });
        }

        if (!_runner.TryStart(sellerNames, intake))
            return BadRequest(new { error = "An automation run is already in progress. Wait for it to finish." });

        // The intake goes back with the acceptance: the file is parsed here, so this response is the
        // browser's only chance to tell the operator that their 765 rows became 472 sellers.
        return Ok(new { count = sellerNames.Count, intake });
    }

    /// <summary>The last run's table. 204 before anything has run — not an error, just nothing yet.</summary>
    [HttpGet("result")]
    public IActionResult Result()
    {
        var result = _store.Current;
        return result is null ? NoContent() : Ok(result);
    }
}
