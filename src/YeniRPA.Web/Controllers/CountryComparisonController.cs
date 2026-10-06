using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services.SalesAnalysis.Country;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// "Ülke Kıyaslama", the second mode of the Sales Analysis view. <c>load</c> parses two orders
/// exports (one per country) and keeps them server-side; <c>analyze</c> answers each settings change
/// with aggregates only — no raw order line goes to the browser.
///
/// <para>Both endpoints are new, so they use the <c>{ success, message, data }</c> envelope per
/// CLAUDE.md, the same shape as <see cref="SalesAnalysisController"/>.</para>
/// </summary>
[ApiController]
[Route("api/country-comparison")]
public sealed class CountryComparisonController : ControllerBase
{
    readonly CountryComparisonService _service;
    readonly ILogger<CountryComparisonController> _logger;

    public CountryComparisonController(CountryComparisonService service, ILogger<CountryComparisonController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpPost("load")]
    public IActionResult Load(IFormFile? fileA, IFormFile? fileB)
    {
        if (fileA is not { Length: > 0 } || fileB is not { Length: > 0 })
            return Failure("İki ülkenin de sipariş Excel dosyasını (.xlsx) seçin.");

        try
        {
            using var streamA = fileA.OpenReadStream();
            using var streamB = fileB.OpenReadStream();
            var result = _service.Load(streamA, fileA.FileName, streamB, fileB.FileName);
            return Success($"{result.A.Lines:N0} + {result.B.Lines:N0} sipariş satırı yüklendi.", result);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Country comparison upload could not be read.");
            return Failure($"Dosya okunamadı. {ex.Message}");
        }
    }

    [HttpPost("analyze")]
    public IActionResult Analyze([FromBody] CountryComparisonRequest? request)
    {
        if (request is null)
            return Failure("İstek boş.");

        try
        {
            return Success("", _service.Analyze(request));
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }
    }

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) => BadRequest(new { success = false, message, data = (object?)null });
}
