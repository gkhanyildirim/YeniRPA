using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Kargo Takip (17Track delivery check): filters an uploaded Mirakl orders export down to the Kolay
/// Gelsin / Sürat Kargo / PTT Kargo lines, then checks each tracking number's status on 17track.net
/// and reports which ones are delivered.
///
/// <para>Every endpoint here is new, so it uses the <c>{ success, message, data }</c> envelope per
/// CLAUDE.md, following <see cref="IncidentWarningsController"/>'s pattern.</para>
/// </summary>
[ApiController]
[Route("api/track17")]
public sealed class Track17Controller : ControllerBase
{
    readonly Track17BatchStore _batchStore;
    readonly Track17Store _store;
    readonly Track17Runner _runner;

    public Track17Controller(Track17BatchStore batchStore, Track17Store store, Track17Runner runner)
    {
        _batchStore = batchStore;
        _store = store;
        _runner = runner;
    }

    public sealed record StartRequest([property: JsonPropertyName("batchId")] string? BatchId);

    [HttpPost("prepare")]
    public IActionResult Prepare(IFormFile? file)
    {
        if (file is not { Length: > 0 })
            return Failure("Upload a Mirakl orders export (.xlsx) first.");

        List<List<string>> table;
        try
        {
            using var stream = file.OpenReadStream();
            table = TabularFile.Read(stream, file.FileName);
        }
        catch (Exception ex)
        {
            return Failure($"'{file.FileName}' could not be read as a spreadsheet. {ex.Message}");
        }

        (IReadOnlyList<Track17Row> Rows, int Skipped) filtered;
        try
        {
            filtered = Track17Filter.Filter(table);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        if (filtered.Rows.Count == 0)
        {
            return Failure(
                $"None of the {table.Count - 1:N0} row(s) in '{file.FileName}' ship with Kolay Gelsin, " +
                "Sürat Kargo or PTT Kargo with a usable tracking number.");
        }

        var distinctCount = filtered.Rows.Select(r => r.TrackingNumber).Distinct(StringComparer.Ordinal).Count();
        if (distinctCount > Track17Filter.MaxTrackingNumbersPerRun)
        {
            return Failure(
                $"{distinctCount:N0} distinct tracking number(s) matched, over the " +
                $"{Track17Filter.MaxTrackingNumbersPerRun:N0}-number limit for one run. Narrow the file and run it in batches.");
        }

        var batch = _batchStore.Put(filtered.Rows);
        var summary = Track17Filter.Summarize(batch.BatchId, table.Count - 1, filtered.Rows, filtered.Skipped);

        return Success(
            $"{summary.MatchedRows.Count:N0} order line(s) matched across {summary.DistinctTrackingNumbers:N0} " +
            $"tracking number(s) - {summary.BatchCount} batch(es) of up to {Track17Filter.MaxPerBatch} for 17track.",
            summary);
    }

    [HttpPost("start")]
    public IActionResult Start([FromBody] StartRequest? request)
    {
        var batch = _batchStore.Get(request?.BatchId);
        if (batch is null)
            return Failure("Prepare a file first, or the prepared batch has expired - upload the file again.");

        if (!_runner.TryStart(batch))
            return Failure("An automation run is already in progress. Wait for it to finish.");

        var distinctCount = batch.Rows.Select(r => r.TrackingNumber).Distinct(StringComparer.Ordinal).Count();
        return Success($"Checking {distinctCount:N0} tracking number(s) on 17track.", new { batchId = batch.BatchId });
    }

    /// <summary>The last run's delivered rows. <c>data</c> is <c>null</c> until a run has finished.</summary>
    [HttpGet("result")]
    public IActionResult Result() => Success("", _store.Current);

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) => BadRequest(new { success = false, message, data = (object?)null });
}
