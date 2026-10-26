using System.Globalization;
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
    const int MaxStockoutRows = 5_000;

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
        [property: JsonPropertyName("age")] string? AgeGroup,
        [property: JsonPropertyName("sort")] string? Sort,
        [property: JsonPropertyName("desc")] bool Descending);

    [HttpPost("generate")]
    public IActionResult Generate(
        IFormFile? offersFile, IFormFile? salesFile, IFormFile? approvalsFile, IFormFile? stockoutFile,
        [FromForm] string? eurTryRate)
    {
        if (offersFile is not { Length: > 0 })
            return Failure("Upload the offers export first.");
        if (salesFile is not { Length: > 0 })
            return Failure("Upload the combined sales file (Birlesik_Satislar) first.");
        if (approvalsFile is not { Length: > 0 })
            return Failure("Upload the approved products file first.");

        // The sales file is in euro; every amount in it is converted at the rate the operator types.
        if (!TryParseRate(eurTryRate, out var rate))
            return Failure("Enter the EUR to TRY exchange rate first (a number above 0, for example 48.5).");

        try
        {
            using var offers = offersFile.OpenReadStream();
            using var sales = salesFile.OpenReadStream();
            using var approvals = approvalsFile.OpenReadStream();

            // The stockout file is optional: without it the report is exactly what it was before.
            using var stockout = stockoutFile is { Length: > 0 } ? stockoutFile.OpenReadStream() : null;

            var report = SkuOfferReportBuilder.Build(
                offers, offersFile.FileName, sales, salesFile.FileName, approvals, approvalsFile.FileName,
                stockout: stockout, stockoutName: stockoutFile?.FileName, eurTryRate: rate);
            var batchId = _store.Put(report);
            var s = report.Summary;

            return Success(
                $"{s.TotalSkus:N0} SKU(s) read; {s.FirstOfferClosedSkus:N0} have a closed first offer.",
                new
                {
                    batchId, summary = s, charts = report.Charts,
                    stockout = report.Stockout is { } so ? new { summary = so.Summary, topPdp = so.TopPdp } : null
                });
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
        string? batchId, string? search, string? filter, string? age, string? sort, bool desc = false,
        int page = 1, int pageSize = 50)
    {
        var report = _store.Get(batchId);
        if (report is null)
            return Failure("The report has expired - upload the files again and generate it once more.");

        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var filtered = SkuOfferReportBuilder.Query(
            report.Rows, new SkuOfferQuery { Search = search, Filter = filter, AgeGroup = age, Sort = sort, Descending = desc });

        var lastPage = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)pageSize));
        page = Math.Clamp(page, 1, lastPage);

        var slice = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Success("", new SkuOfferPage(filtered.Count, page, pageSize, slice));
    }

    /// <summary>Every stockout row — the list is hundreds of products, so sorting and search run in the browser.</summary>
    [HttpGet("stockout")]
    public IActionResult Stockout(string? batchId)
    {
        var report = _store.Get(batchId);
        if (report is null)
            return Failure("The report has expired - upload the files again and generate it once more.");
        if (report.Stockout is null)
            return Failure("No stockout file was uploaded with this report.");

        var rows = report.Stockout.Rows.Take(MaxStockoutRows).ToList();
        return Success(
            rows.Count < report.Stockout.Rows.Count ? $"Showing the first {MaxStockoutRows:N0} products." : "",
            new { rows, total = report.Stockout.Rows.Count });
    }

    public sealed record StockoutDownloadRequest([property: JsonPropertyName("batchId")] string? BatchId);

    [HttpPost("stockout-download")]
    public IActionResult StockoutDownload([FromBody] StockoutDownloadRequest? request)
    {
        var report = _store.Get(request?.BatchId);
        if (report is null)
            return Failure("The report has expired - upload the files again and generate it once more.");
        if (report.Stockout is null)
            return Failure("No stockout file was uploaded with this report.");

        return File(BuildStockoutWorkbook(report.Stockout.Rows), XlsxContentType,
            $"stockout_pdp_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    }

    [HttpPost("download")]    public IActionResult Download([FromBody] DownloadRequest? request)
    {
        var report = _store.Get(request?.BatchId);
        if (report is null || request is null)
            return Failure("The report has expired - upload the files again and generate it once more.");

        var rows = SkuOfferReportBuilder.Query(report.Rows, new SkuOfferQuery
        {
            Search = request.Search, Filter = request.Filter, AgeGroup = request.AgeGroup,
            Sort = request.Sort, Descending = request.Descending
        });

        if (rows.Count > MaxExportRows)
            return Failure($"{rows.Count:N0} rows is too many for one workbook (limit {MaxExportRows:N0}). Narrow the filter or search first.");

        return File(BuildWorkbook(rows, report.Charts.Periods.Select(p => p.Label).ToList()), XlsxContentType,
            $"sku_teklif_trafik_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    }

    sealed record ExportColumn<T>(string Header, Action<IXLCell, T> Write, string? Format = null, double Width = 16);

    /// <summary>
    /// The rate as typed: a decimal comma or point, read with the invariant culture (the server may run a
    /// Turkish culture, which would read "48.5" as 485). Zero, negative, non-finite and absurdly large values are refused.
    /// </summary>
    internal static bool TryParseRate(string? text, out double rate)
    {
        rate = 0;
        var cleaned = (text ?? "").Trim().Replace(',', '.');
        return cleaned.Length > 0 &&
               double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out rate) &&
               double.IsFinite(rate) && rate > 0 && rate <= 1000;
    }

    static byte[] BuildWorkbook(IReadOnlyList<SkuOfferRow> rows, IReadOnlyList<string> periods)
    {
        var columns = new List<ExportColumn<SkuOfferRow>>
        {
            new("Product SKU", (c, r) => c.SetValue(r.Sku)),
            new("Product", (c, r) => c.SetValue(r.Product), Width: 48),
            new("Brand", (c, r) => c.SetValue(r.Brand)),
            new("Category", (c, r) => c.SetValue(r.Category)),
            new("First offer date", (c, r) => { if (r.FirstOfferDate is { } d) c.SetValue(d); }, "dd.MM.yyyy"),
            new("Age group", (c, r) => c.SetValue(AgeLabel(r.AgeGroup))),
            new("Age (days)", (c, r) => SetNumber(c, r.AgeDays), "#,##0"),
            new("First offer seller", (c, r) => c.SetValue(r.FirstOfferSeller)),
            new("First offer active", (c, r) => c.SetValue(r.FirstOfferActive ? "Yes" : "No")),
            new("Inactivity reason", (c, r) => c.SetValue(r.FirstOfferReason)),
            new("Approval date", (c, r) => { if (r.ApprovalDate is { } d) c.SetValue(d); }, "dd.MM.yyyy"),
            new("Approved by", (c, r) => c.SetValue(r.ApprovedBy)),
            new("Total offers", (c, r) => c.SetValue(r.TotalOffers), "#,##0"),
            new("Active offers", (c, r) => c.SetValue(r.ActiveOffers), "#,##0"),
            new("Active offer seller(s)", (c, r) => c.SetValue(string.Join(", ", r.ActiveSellers)), Width: 32),
        };
        columns.AddRange(
        [
            new("Impressions", (c, r) => SetNumber(c, r.Impressions), "#,##0"),
            new("Clicks", (c, r) => SetNumber(c, r.Clicks), "#,##0"),
            new("CTR", (c, r) => SetNumber(c, r.Ctr), "0.00%"),
            new("PDP sessions", (c, r) => SetNumber(c, r.PdpViews), "#,##0"),
            new("Add to cart sessions", (c, r) => SetNumber(c, r.AddToCart), "#,##0"),
            new("Add to cart rate", (c, r) => SetNumber(c, r.AtcRate), "0.00%"),
            new("Checkout sessions", (c, r) => SetNumber(c, r.CheckoutSessions), "#,##0"),
            new("Order sessions", (c, r) => SetNumber(c, r.OrderSessions), "#,##0"),
            new("CR PDP", (c, r) => SetNumber(c, r.CrPdp), "0.00%"),
            new("CR checkout", (c, r) => SetNumber(c, r.CrCheckout), "0.00%"),
            new("Remove from cart sessions", (c, r) => SetNumber(c, r.RemoveFromCart), "#,##0"),
            new("Sold qty", (c, r) => SetNumber(c, r.SoldQty), "#,##0"),
            new("GMV (TRY, w/o shipping)", (c, r) => SetNumber(c, r.Gmv), "#,##0"),
            new("Avg price (TRY)", (c, r) => SetNumber(c, r.AvgPrice), "#,##0.00"),
            new("AOV (TRY)", (c, r) => SetNumber(c, r.Aov), "#,##0.00"),
        ]);
        for (var i = 0; i < periods.Count; i++)
        {
            var index = i;
            columns.Add(new($"Qty | {periods[i]}", (c, r) => SetNumber(c, r.PeriodQty?[index]), "#,##0"));
            columns.Add(new($"GMV (TRY) | {periods[i]}", (c, r) => SetNumber(c, r.PeriodGmv?[index]), "#,##0"));
        }
        columns.Add(new("Warning", (c, r) => c.SetValue(r.Warning ?? ""), Width: 48));
        return WriteWorkbook("SKU Offer & Traffic", columns, rows);
    }

    static byte[] BuildStockoutWorkbook(IReadOnlyList<StockoutRow> rows)
    {
        var columns = new List<ExportColumn<StockoutRow>>
        {
            new("Product id", (c, r) => c.SetValue(r.ProductId)),
            new("Product", (c, r) => c.SetValue(r.Product), Width: 48),
            new("Brand", (c, r) => c.SetValue(r.Brand)),
            new("Category", (c, r) => c.SetValue(r.Category)),
            new("Seller(s)", (c, r) => c.SetValue(r.Sellers), Width: 28),
            new("Retail buyable", (c, r) => c.SetValue(r.RetailBuyable is { } b ? (b ? "Yes" : "No") : "")),
            new("Impressions", (c, r) => SetNumber(c, r.Impressions), "#,##0"),
            new("PDP sessions", (c, r) => SetNumber(c, r.PdpViews), "#,##0"),
            new("Add to cart sessions", (c, r) => SetNumber(c, r.AddToCart), "#,##0"),
            new("Add to cart rate", (c, r) => SetNumber(c, r.AtcRate), "0.00%"),
            new("CR PDP", (c, r) => SetNumber(c, r.CrPdp), "0.00%"),
            new("Sold qty (whole period)", (c, r) => SetNumber(c, r.SoldQty), "#,##0"),
            new("GMV (TRY, whole period)", (c, r) => SetNumber(c, r.Gmv), "#,##0"),
            new("Sold items (7d)", (c, r) => c.SetValue(r.Sold7d), "#,##0"),
            new("GMV (7d)", (c, r) => c.SetValue(r.Gmv7d), "#,##0"),
            new("In offers file", (c, r) => c.SetValue(r.InOffers ? "Yes" : "No")),
            new("Total offers", (c, r) => c.SetValue(r.TotalOffers), "#,##0"),
            new("Active offers", (c, r) => c.SetValue(r.ActiveOffers), "#,##0"),
            new("First offer date", (c, r) => { if (r.FirstOfferDate is { } d) c.SetValue(d); }, "dd.MM.yyyy"),
            new("First offer closed", (c, r) => c.SetValue(r.InOffers ? (r.FirstOfferClosed ? "Yes" : "No") : "")),
            new("Age group", (c, r) => c.SetValue(AgeLabel(r.AgeGroup))),
            new("Approval date", (c, r) => { if (r.ApprovalDate is { } d) c.SetValue(d); }, "dd.MM.yyyy"),
            new("Approved by", (c, r) => c.SetValue(r.ApprovedBy)),
        };

        return WriteWorkbook("Stockout PDP", columns, rows);
    }

    static byte[] WriteWorkbook<T>(string sheetName, List<ExportColumn<T>> columns, IReadOnlyList<T> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(sheetName);
        XlsxStyles.ApplyBaseFont(sheet);

        for (var c = 0; c < columns.Count; c++)
            sheet.Cell(1, c + 1).SetValue(columns[c].Header);
        XlsxStyles.StyleHeaderRow(sheet.Range(1, 1, 1, columns.Count));

        for (var i = 0; i < rows.Count; i++)
        {
            for (var c = 0; c < columns.Count; c++)
                columns[c].Write(sheet.Cell(i + 2, c + 1), rows[i]);
        }

        for (var c = 0; c < columns.Count; c++)
        {
            if (rows.Count > 0 && columns[c].Format is { } format)
                sheet.Range(2, c + 1, rows.Count + 1, c + 1).Style.NumberFormat.Format = format;

            // Fixed widths: AdjustToContents over 100k rows measures every cell and is slow.
            sheet.Column(c + 1).Width = columns[c].Width;
        }

        if (rows.Count > 0)
            sheet.Range(1, 1, rows.Count + 1, columns.Count).SetAutoFilter();
        sheet.SheetView.FreezeRows(1);

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }
    static string AgeLabel(string? group) =>
        SkuAgeGroups.All.FirstOrDefault(g => g.Key == group).Label ?? "";

    static void SetNumber(IXLCell cell, double? value)
    {
        if (value is { } v) cell.SetValue(v);
    }

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) => BadRequest(new { success = false, message, data = (object?)null });
}
