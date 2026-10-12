using System.Text.Json.Serialization;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// SKU Offer &amp; Traffic Report: the offers export and the Offer KPIs export joined per product. See
/// <see cref="SkuOfferReportBuilder"/> for the rules. <c>generate</c> reads both files once and keeps the
/// result in <see cref="SkuOfferReportStore"/>; <c>rows</c> pages through it and <c>download</c> exports
/// whatever the table currently shows.
///
/// <para>New endpoints, so every response uses the <c>{ success, message, data }</c> envelope.</para>
/// </summary>
[ApiController]
[Route("api/sku-offer-report")]
public sealed class SkuOfferReportController : ControllerBase
{
    const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    const int MaxPageSize = 200;

    /// <summary>A sheet this size is still a sensible download; past it the operator should filter first.</summary>
    const int MaxExportRows = 100_000;

    readonly SkuOfferReportStore _store;

    public SkuOfferReportController(SkuOfferReportStore store)
    {
        _store = store;
    }

    public sealed record DownloadRequest(
        [property: JsonPropertyName("batchId")] string? BatchId,
        [property: JsonPropertyName("search")] string? Search,
        [property: JsonPropertyName("filter")] string? Filter,
        [property: JsonPropertyName("sort")] string? Sort,
        [property: JsonPropertyName("desc")] bool Descending);

    [HttpPost("generate")]
    public IActionResult Generate(IFormFile? offersFile, IFormFile? trafficFile)
    {
        if (offersFile is not { Length: > 0 })
            return Failure("Upload the offers export first.");
        if (trafficFile is not { Length: > 0 })
            return Failure("Upload the Offer KPIs (traffic) export first.");

        try
        {
            using var offers = offersFile.OpenReadStream();
            using var traffic = trafficFile.OpenReadStream();

            var report = SkuOfferReportBuilder.Build(offers, offersFile.FileName, traffic, trafficFile.FileName);
            var batchId = _store.Put(report);
            var s = report.Summary;

            return Success(
                $"{s.TotalSkus:N0} SKU(s) read; {s.FirstOfferClosedSkus:N0} have a closed first offer.",
                new { batchId, summary = s, charts = report.Charts });
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

    [HttpGet("rows")]
    public IActionResult Rows(
        string? batchId, string? search, string? filter, string? sort, bool desc = false,
        int page = 1, int pageSize = 50)
    {
        var report = _store.Get(batchId);
        if (report is null)
            return Failure("The report has expired - upload the files again and generate it once more.");

        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var filtered = SkuOfferReportBuilder.Query(
            report.Rows, new SkuOfferQuery { Search = search, Filter = filter, Sort = sort, Descending = desc });

        var lastPage = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)pageSize));
        page = Math.Clamp(page, 1, lastPage);

        var slice = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Success("", new SkuOfferPage(filtered.Count, page, pageSize, slice));
    }

    [HttpPost("download")]
    public IActionResult Download([FromBody] DownloadRequest? request)
    {
        var report = _store.Get(request?.BatchId);
        if (report is null || request is null)
            return Failure("The report has expired - upload the files again and generate it once more.");

        var rows = SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery
        {
            Search = request.Search, Filter = request.Filter, Sort = request.Sort, Descending = request.Descending
        });

        if (rows.Count > MaxExportRows)
            return Failure($"{rows.Count:N0} rows is too many for one workbook (limit {MaxExportRows:N0}). Narrow the filter or search first.");

        return File(BuildWorkbook(rows, report.Summary.HasImpressions), XlsxContentType,
            $"sku_teklif_trafik_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    }

    static byte[] BuildWorkbook(IReadOnlyList<SkuOfferRow> rows, bool hasImpressions)
    {
        var headers = new List<string>
        {
            "Product SKU", "Product", "Brand", "Category", "First offer date", "First offer seller",
            "First offer active", "Inactivity reason", "Total offers", "Active offers",
            "PDP views (30d)", "Add to cart (30d)", "Add to cart rate", "Sold items (30d)", "GMV (30d)", "Warning"
        };
        if (hasImpressions) headers.Insert(10, "Impressions");

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("SKU Offer & Traffic");
        XlsxStyles.ApplyBaseFont(sheet);

        for (var c = 0; c < headers.Count; c++)
            sheet.Cell(1, c + 1).SetValue(headers[c]);
        XlsxStyles.StyleHeaderRow(sheet.Range(1, 1, 1, headers.Count));

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var col = 1;
            var row = i + 2;

            sheet.Cell(row, col++).SetValue(r.Sku);
            sheet.Cell(row, col++).SetValue(r.Product);
            sheet.Cell(row, col++).SetValue(r.Brand);
            sheet.Cell(row, col++).SetValue(r.Category);
            if (r.FirstOfferDate is { } date) sheet.Cell(row, col).SetValue(date);
            col++;
            sheet.Cell(row, col++).SetValue(r.FirstOfferSeller);
            sheet.Cell(row, col++).SetValue(r.FirstOfferActive ? "Yes" : "No");
            sheet.Cell(row, col++).SetValue(r.FirstOfferReason);
            sheet.Cell(row, col++).SetValue(r.TotalOffers);
            sheet.Cell(row, col++).SetValue(r.ActiveOffers);
            if (hasImpressions) SetNumber(sheet.Cell(row, col++), r.Impressions);
            SetNumber(sheet.Cell(row, col++), r.PdpViews);
            SetNumber(sheet.Cell(row, col++), r.AddToCart);
            SetNumber(sheet.Cell(row, col++), r.AtcRate);
            SetNumber(sheet.Cell(row, col++), r.Sold30d);
            SetNumber(sheet.Cell(row, col++), r.Gmv30d);
            sheet.Cell(row, col).SetValue(r.Warning ?? "");
        }

        if (rows.Count > 0)
        {
            sheet.Column(5).Style.DateFormat.Format = "dd.MM.yyyy";
            var rateCol = hasImpressions ? 14 : 13;
            sheet.Column(rateCol).Style.NumberFormat.Format = "0.0%";
            foreach (var c in new[] { 9, 10, hasImpressions ? 11 : -1, rateCol - 2, rateCol - 1, rateCol + 1, rateCol + 2 }.Where(c => c > 0))
                sheet.Column(c).Style.NumberFormat.Format = "#,##0";
            sheet.Range(1, 1, rows.Count + 1, headers.Count).SetAutoFilter();
        }

        // Fixed widths: AdjustToContents over 100k rows measures every cell and is slow.
        for (var c = 1; c <= headers.Count; c++)
            sheet.Column(c).Width = c == 2 || c == headers.Count ? 48 : 16;
        sheet.SheetView.FreezeRows(1);

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    static void SetNumber(IXLCell cell, double? value)
    {
        if (value is { } v) cell.SetValue(v);
    }

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) => BadRequest(new { success = false, message, data = (object?)null });
}
