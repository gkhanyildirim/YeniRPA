using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// POS Reconciliation: backfills Bulut Tahsilat's blank "Sipariş Numarası" rows from Craftgate
/// ("Provizyon No" → "authCode" → "externalId") and pivots the result by POS Banka × Taksit. See
/// <see cref="PosReconciliationBuilder"/> for the merge and pivot rules.
///
/// <para>Mirakl is optional: when uploaded, its per-order Amount becomes each row's Marketplace GMV
/// and the remainder of İşlem Tutarı becomes Retail GMV.</para>
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
    readonly IPosRateStore _rateStore;

    public PosReconciliationController(PosReconciliationStore store, IPosRateStore rateStore)
    {
        _store = store;
        _rateStore = rateStore;
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
            using var miraklStream = miraklFile is { Length: > 0 } ? miraklFile.OpenReadStream() : null;

            var batch = PosReconciliationBuilder.Build(
                bulutTahsilatStream, bulutTahsilatFile.FileName,
                craftgateStream, craftgateFile.FileName,
                miraklStream, miraklFile?.FileName);

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

        // Rates are read at download time, so edits made after Generate still land in the report.
        var bytes = PosReconciliationBuilder.BuildWorkbook(batch, _rateStore.Load());
        var fileName = $"pos_mutabakat_{batch.GeneratedAt:yyyyMMdd_HHmmss}.xlsx";

        return File(bytes, XlsxContentType, fileName);
    }

    // ---------------------------------------------------------------------
    // Banka oranları — the editable rate table and the on-us family rules
    // ---------------------------------------------------------------------

    [HttpGet("rates")]
    public IActionResult GetRates() => Success("OK", _rateStore.Load());

    [HttpPut("rates")]
    public IActionResult SaveRates([FromBody] PosRateSettings? settings)
    {
        if (settings is null)
            return Failure("No rate table was sent.");

        var errors = PosRateStore.Validate(settings);
        if (errors.Count > 0)
            return Failure(string.Join(" ", errors));

        _rateStore.Save(settings);
        return Success("Rates saved.", _rateStore.Load());
    }

    [HttpPost("rates/excel")]
    public IActionResult ExportRates() =>
        File(PosRateWorkbook.Build(_rateStore.Load()), XlsxContentType, "pos_banka_oranlar.xlsx");

    /// <summary>Parses an uploaded rate workbook and hands it back for review — nothing is saved until
    /// the operator presses Save in the editor.</summary>
    [HttpPost("rates/import")]
    public IActionResult ImportRates(IFormFile? file)
    {
        if (file is not { Length: > 0 })
            return Failure("Choose a rate workbook (.xlsx) first.");
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return Failure("Only .xlsx workbooks can be imported.");

        try
        {
            using var stream = file.OpenReadStream();
            var imported = PosRateWorkbook.Read(stream, _rateStore.Load().OnUsRules);
            return Success($"{imported.Banks.Count} bank(s) loaded — review them and press Save.", imported);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }
        catch (Exception ex)
        {
            return Failure($"The workbook could not be read. {ex.Message}");
        }
    }

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) => BadRequest(new { success = false, message, data = (object?)null });
}
