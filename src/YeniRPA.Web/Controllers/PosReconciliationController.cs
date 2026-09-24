using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// POS Reconciliation: backfills Bulut Tahsilat's blank "Sipariş Numarası" rows from Craftgate
/// ("Provizyon No" → "authCode" → "externalId") and pivots the result by POS Banka × Taksit. See
/// <see cref="PosReconciliationBuilder"/> for the merge and pivot rules.
///
/// <para>Mirakl is uploaded here too — the operator pulls all 3 platform reports together — but its
/// role in this merge is not defined yet, so <c>miraklFile</c> is accepted and otherwise ignored.</para>
///
/// <para>Every endpoint here is new, so it uses the <c>{ success, message, data }</c> envelope per
/// CLAUDE.md, following <see cref="CargoSellerReportController"/>'s pattern.</para>
/// </summary>
[ApiController]
[Route("api/pos-reconciliation")]
public sealed class PosReconciliationController : ControllerBase
{
    const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    readonly PosReconciliationStore _store;

    public PosReconciliationController(PosReconciliationStore store)
    {
        _store = store;
    }

    public sealed record DownloadRequest([property: JsonPropertyName("batchId")] string? BatchId);

    [HttpPost("generate")]
    public IActionResult Generate(IFormFile? bulutTahsilatFile, IFormFile? craftgateFile, IFormFile? miraklFile)
    {
        if (bulutTahsilatFile is not { Length: > 0 })
            return Failure("Upload the Bulut Tahsilat file first.");
        if (craftgateFile is not { Length: > 0 })
            return Failure("Upload the Craftgate file first.");

        try
        {
            using var bulutTahsilatStream = bulutTahsilatFile.OpenReadStream();
            using var craftgateStream = craftgateFile.OpenReadStream();

            var batch = PosReconciliationBuilder.Build(
                bulutTahsilatStream, bulutTahsilatFile.FileName,
                craftgateStream, craftgateFile.FileName);

            var batchId = _store.Put(batch);

            return Success(
                $"{batch.Summary.TotalRecords:N0} record(s) processed — " +
                $"{batch.Summary.BackfilledViaCraftgate:N0} order number(s) backfilled from Craftgate, " +
                $"{batch.Summary.Unmatched + batch.Summary.Conflicted:N0} still unmatched.",
                new { batchId, summary = batch.Summary, pivot = batch.Pivot, unmatched = batch.Unmatched });
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
        var batch = _store.Get(request?.BatchId);
        if (batch is null)
            return Failure("The report has expired - upload the files again and generate it once more.");

        var bytes = PosReconciliationBuilder.BuildWorkbook(batch);
        var fileName = $"pos_mutabakat_{batch.GeneratedAt:yyyyMMdd_HHmmss}.xlsx";

        return File(bytes, XlsxContentType, fileName);
    }

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) => BadRequest(new { success = false, message, data = (object?)null });
}
