using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services.SalesAnalysis;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Sales Analysis, the second view of the Order Report panel. <c>load</c> parses the orders export
/// once and keeps it server-side; <c>analyze</c> answers each filter/tab with aggregates only — no
/// raw order line ever goes to the browser.
///
/// <para>Both endpoints are new, so they use the <c>{ success, message, data }</c> envelope per
/// CLAUDE.md, following <see cref="PosReconciliationController"/>'s pattern.</para>
/// </summary>
[ApiController]
[Route("api/sales-analysis")]
public sealed class SalesAnalysisController : ControllerBase
{
    readonly SalesAnalysisService _service;
    readonly ILogger<SalesAnalysisController> _logger;

    public SalesAnalysisController(SalesAnalysisService service, ILogger<SalesAnalysisController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpPost("load")]
    public IActionResult Load(IFormFile? file)
    {
        if (file is not { Length: > 0 })
            return Failure("Önce sipariş Excel dosyasını (.xlsx) seçin.");

        try
        {
            using var stream = file.OpenReadStream();
            var result = _service.Load(stream, file.FileName);
            return Success($"{result.Lines:N0} sipariş satırı yüklendi.", result);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sales analysis upload could not be read.");
            return Failure($"Dosya okunamadı. {ex.Message}");
        }
    }

    [HttpPost("analyze")]
    public IActionResult Analyze([FromBody] SalesAnalysisRequest? request)
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
