using System.Text.Json.Serialization;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Stockout Warnings: finds the stockout products worth chasing in a Partner Manager export
/// ("Products Unavailable"), filters them by GMV, groups them by seller, renders one message per
/// seller and sends them.
///
/// <para>Same <c>prepare</c> → <c>messages</c> → <c>send</c> split as Late Order Warnings and
/// Incident Warnings: <c>prepare</c> returns the parsed rows so editing the template re-posts a
/// few KB instead of re-uploading the export.</para>
///
/// <para><b>The seller → WhatsApp group data is shared with Late Order Warnings and Incident
/// Warnings</b> — one seller has one group whichever module is chasing them — but this panel owns
/// a full editor of its own rather than sending the operator to another tab: its own session
/// buttons for the one shared Chrome profile, and its own mapping table saved through
/// <see cref="ISellerGroupStore.SaveEntries"/> so editing sellers here can never touch another
/// module's message templates. Its own message templates and GMV threshold are saved separately,
/// through <see cref="ISellerGroupStore.SaveStockoutSettings"/>.</para>
///
/// <para><b>Every JSON endpoint here returns <c>{ success, message, data }</c>.</b> This is a new
/// endpoint set, so CLAUDE.md's envelope rule applies — including failures, which is why the
/// builder's <see cref="InvalidOperationException"/> is caught locally rather than left to
/// <c>ReportExceptionFilter</c>. Same call as <see cref="IncidentWarningsController"/>.</para>
/// </summary>
[ApiController]
[Route("api/stockout-warnings")]
public sealed class StockoutWarningsController : ControllerBase
{
    const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    const double MinGmvThreshold = 0;
    const double MaxGmvThreshold = 10_000_000;

    readonly ISellerGroupStore _store;
    readonly WhatsAppBrowser _browser;
    readonly WhatsAppMessageRunner _runner;
    readonly AutomationJobBus _bus;

    public StockoutWarningsController(
        ISellerGroupStore store,
        WhatsAppBrowser browser,
        WhatsAppMessageRunner runner,
        AutomationJobBus bus)
    {
        _store = store;
        _browser = browser;
        _runner = runner;
        _bus = bus;
    }

    // -----------------------------------------------------------------
    // Request shapes
    // -----------------------------------------------------------------

    public sealed record MessagesRequest(
        [property: JsonPropertyName("sellers")] IReadOnlyList<StockoutWarningSeller>? Sellers,
        [property: JsonPropertyName("referenceTime")] string? ReferenceTime,
        [property: JsonPropertyName("template")] string? Template,
        [property: JsonPropertyName("productLineTemplate")] string? ProductLineTemplate);

    public sealed record MessagesExcelRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<RenderedMessage>? Messages);

    public sealed record SettingsRequest(
        [property: JsonPropertyName("template")] string? Template,
        [property: JsonPropertyName("productLineTemplate")] string? ProductLineTemplate,
        [property: JsonPropertyName("gmvThreshold")] double GmvThreshold);

    public sealed record SendMessage(
        [property: JsonPropertyName("groupName")] string? GroupName,
        [property: JsonPropertyName("sellerName")] string? SellerName,
        [property: JsonPropertyName("body")] string? Body);

    public sealed record SendRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<SendMessage>? Messages,
        [property: JsonPropertyName("dryRun")] bool DryRun);

    public sealed record MappingRequest(
        [property: JsonPropertyName("entries")] IReadOnlyList<SellerGroupEntry>? Entries);

    public sealed record MappingExcelRequest(
        [property: JsonPropertyName("entries")] IReadOnlyList<SellerGroupEntry>? Entries);

    // -----------------------------------------------------------------
    // Prepare
    // -----------------------------------------------------------------

    [HttpPost("prepare")]
    public IActionResult Prepare(
        IFormFile? file,
        // [FromForm] is required: [ApiController] infers query-string binding for simple types, so
        // without it the threshold silently arrives as 0 no matter what the operator typed.
        [FromForm] double gmvThreshold)
    {
        if (file is not { Length: > 0 })
            return Failure("Please upload the Partner Manager stockout export (.csv or .xlsx).");

        try
        {
            using var stream = file.OpenReadStream();
            var map = _store.BuildMap();
            var data = StockoutWarningBuilder.Build(stream, file.FileName, new StockoutWarningOptions(gmvThreshold), map);

            return Success(
                data.Funnel.Eligible == 0
                    ? $"No stockout product is at or above a GMV of {gmvThreshold:N0}."
                    : $"{data.Funnel.Eligible:N0} product(s) across {data.Funnel.Sellers:N0} seller(s) are at or above a GMV of {gmvThreshold:N0}.",
                data);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }
    }

    // -----------------------------------------------------------------
    // Messages
    // -----------------------------------------------------------------

    [HttpPost("messages")]
    public IActionResult Messages([FromBody] MessagesRequest? request)
    {
        var sellers = request?.Sellers ?? [];

        // Grouped by destination, not by seller: two seller names mapped to one group get one
        // message, not two in the same chat. Merging here rather than at send time keeps the
        // preview cards, the Excel export and the typed keystrokes identical.
        var rendered = sellers
            .Where(s => !string.IsNullOrWhiteSpace(s.GroupName))
            .GroupBy(s => s.GroupName!.Trim(), StringComparer.Ordinal)
            .Select(group => StockoutWarningMessageBuilder.Render(
                [.. group], request?.ReferenceTime ?? "", request?.Template, request?.ProductLineTemplate))
            .ToList();

        var missingRequired = StockoutWarningMessageBuilder.FindMissingRequired(request?.ProductLineTemplate);

        var messages = rendered.Select(m => new
        {
            groupName = m.GroupName,
            sellerName = m.SellerName,
            body = m.Body,
            productCount = m.OrderCount,
            truncated = m.Truncated,
            unknownPlaceholders = m.UnknownPlaceholders,
            accountCount = m.AccountCount,
            overLimit = m.Body.Trim().Length > WhatsAppMessageRunner.MaxMessageChars,
        }).ToList();

        var warnings = rendered
            .SelectMany(m => m.UnknownPlaceholders)
            .Distinct(StringComparer.Ordinal)
            .Select(token => $"'{token}' is not a placeholder and was left in the message text as-is.")
            .ToList();

        if (missingRequired.Count > 0)
        {
            warnings.Add(
                $"The product line template is missing {string.Join(" and ", missingRequired)} — every " +
                "product line must carry the GTIN and sold-items-accepted count. Add them back before sending.");
        }

        if (messages.Any(m => m.overLimit))
        {
            warnings.Add(
                $"Some messages are over the {WhatsAppMessageRunner.MaxMessageChars}-character limit and " +
                "will be refused. Shorten the template or raise the GMV threshold to list fewer products.");
        }

        return Success($"{messages.Count:N0} message(s) rendered.", new { messages, warnings });
    }

    /// <summary>
    /// A dedicated export rather than the generic <c>/api/export/xlsx</c>: that path reads cells
    /// back out of rendered HTML and collapses whitespace runs, which would flatten a multi-line
    /// message body into one long line. Same call as <c>LateOrdersController.MessagesExcel</c>.
    /// </summary>
    [HttpPost("messages/excel")]
    public IActionResult MessagesExcel([FromBody] MessagesExcelRequest? request)
    {
        var messages = request?.Messages ?? [];
        if (messages.Count == 0)
            return Failure("There is nothing to export.");

        return File(BuildMessagesWorkbook(messages), XlsxContentType,
            $"stockout-warnings-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    // -----------------------------------------------------------------
    // Session
    // -----------------------------------------------------------------

    /// <summary>
    /// This panel has its own login / check-session / clear-session buttons even though they mutate
    /// the one Chrome profile every WhatsApp module shares — see the class summary. That sharing is
    /// exactly why <c>ClearSession</c> below still refuses while any module's run is in progress,
    /// the same guard <see cref="LateOrdersController.ClearSession"/> applies.
    /// </summary>
    [HttpGet("status")]
    public IActionResult Status() => Success("", new
    {
        hasProfile = _browser.HasProfile,
        signedIn = _browser.LastKnownSignedIn,
        lastCheckedUtc = _browser.LastCheckedUtc,
        browserReady = _browser.IsBrowserReady,
        isRunning = _bus.IsRunning,
        runningModule = _bus.RunningModule,
        profilePath = _browser.ProfilePath,
        maxGroupsPerRun = WhatsAppMessageRunner.MaxGroupsPerRun,
        maxMessageChars = WhatsAppMessageRunner.MaxMessageChars,
        maxProductLines = StockoutWarningBuilder.MaxProductLinesPerMessage,
    });

    /// <summary>Opens a real Chrome window on WhatsApp Web for the QR scan. Blocks until it is up.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login()
    {
        await _browser.OpenLoginAsync();
        return Success("", null);
    }

    [HttpPost("check-session")]
    public async Task<IActionResult> CheckSession() => Success("", new { signedIn = await _browser.CheckSignedInAsync() });

    [HttpPost("clear-session")]
    public async Task<IActionResult> ClearSession()
    {
        if (_bus.IsRunning)
            return Failure("An automation run is in progress. Wait for it to finish.");

        return Success(await _browser.ClearSessionAsync(), null);
    }

    // -----------------------------------------------------------------
    // Mapping
    // -----------------------------------------------------------------

    [HttpGet("mapping")]
    public IActionResult GetMapping()
    {
        var file = _store.Load();
        var map = SellerGroupMap.FromEntries(file.Entries);

        return Success("", new
        {
            entries = file.Entries,
            path = _store.FilePath,
            updatedUtc = file.UpdatedUtc,
            warnings = map.LoadWarnings,
        });
    }

    [HttpPut("mapping")]
    public IActionResult SaveMapping([FromBody] MappingRequest? request)
    {
        var entries = Clean(request?.Entries);

        // SaveEntries, not SaveStockoutSettings and not Save: this table is also read by Late Order
        // Warnings and Incident Warnings, and neither of their templates is any of this request's
        // business.
        _store.SaveEntries(entries);

        return Success($"{entries.Count:N0} entries saved.", new
        {
            saved = entries.Count,
            path = _store.FilePath,
            warnings = SellerGroupMap.FromEntries(entries).LoadWarnings,
        });
    }

    /// <summary>
    /// Returns the merged table for review; it does <b>not</b> save. An import that silently
    /// overwrote a hand-built mapping from a wrong-shaped file would only be recoverable from the
    /// backup, so the operator looks at the result and presses Save. Same call as
    /// <see cref="LateOrdersController.ImportMapping"/>.
    /// </summary>
    [HttpPost("mapping/import")]
    public IActionResult ImportMapping(IFormFile? file)
    {
        if (file is not { Length: > 0 })
            return Failure("Please upload a mapping file (.xlsx or .csv).");

        List<SellerGroupEntry> imported;
        try
        {
            using var stream = file.OpenReadStream();
            imported = SellerGroupStore.ReadWorkbook(stream, file.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        var merged = _store.Load().Entries.ToList();
        var added = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var entry in imported)
        {
            var index = FindExisting(merged, entry);
            if (index < 0)
            {
                merged.Add(entry);
                added++;
                continue;
            }

            var existing = merged[index];
            var next = new SellerGroupEntry(
                SellerId: entry.SellerId.Length > 0 ? entry.SellerId : existing.SellerId,
                SellerName: entry.SellerName.Length > 0 ? entry.SellerName : existing.SellerName,
                GroupName: entry.GroupName.Length > 0 ? entry.GroupName : existing.GroupName);

            if (next == existing)
            {
                skipped++;
                continue;
            }

            merged[index] = next;
            updated++;
        }

        return Success($"{added} added, {updated} updated, {skipped} unchanged — not saved yet.",
            new { entries = merged, added, updated, skipped });
    }

    [HttpPost("mapping/excel")]
    public IActionResult MappingExcel([FromBody] MappingExcelRequest? request)
    {
        var entries = Clean(request?.Entries);
        if (entries.Count == 0)
            return Failure("The mapping table is empty.");

        return File(SellerGroupStore.BuildWorkbook(entries), XlsxContentType, "seller-groups.xlsx");
    }

    // -----------------------------------------------------------------
    // Settings
    // -----------------------------------------------------------------

    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        var file = _store.Load();

        return Success("", new
        {
            template = file.StockoutMessageTemplate ?? StockoutWarningMessageBuilder.DefaultTemplate,
            productLineTemplate = file.StockoutProductLineTemplate ?? StockoutWarningMessageBuilder.DefaultProductLineTemplate,
            defaultTemplate = StockoutWarningMessageBuilder.DefaultTemplate,
            defaultProductLineTemplate = StockoutWarningMessageBuilder.DefaultProductLineTemplate,
            placeholders = StockoutWarningMessageBuilder.KnownPlaceholders,
            requiredProductLinePlaceholders = StockoutWarningMessageBuilder.RequiredProductLinePlaceholders,
            gmvThreshold = file.StockoutGmvThreshold ?? StockoutWarningBuilder.DefaultGmvThreshold,
            minGmvThreshold = MinGmvThreshold,
            maxGmvThreshold = MaxGmvThreshold,
            updatedUtc = file.UpdatedUtc,
        });
    }

    [HttpPut("settings")]
    public IActionResult SaveSettings([FromBody] SettingsRequest? request)
    {
        var threshold = Math.Clamp(
            request?.GmvThreshold ?? StockoutWarningBuilder.DefaultGmvThreshold,
            MinGmvThreshold,
            MaxGmvThreshold);

        var productLineTemplate = NullIfBlank(request?.ProductLineTemplate);
        var missingRequired = StockoutWarningMessageBuilder.FindMissingRequired(productLineTemplate);
        if (missingRequired.Count > 0)
        {
            return Failure(
                $"The product line template must keep {string.Join(" and ", missingRequired)} — every " +
                "product line has to carry the GTIN and sold-items-accepted count.");
        }

        // SaveStockoutSettings, not Save: the same document also carries the seller mapping and the
        // other two modules' templates, which this request knows nothing about.
        _store.SaveStockoutSettings(NullIfBlank(request?.Template), productLineTemplate, threshold);

        return Success("Stockout warning settings saved.", new { gmvThreshold = threshold });
    }

    // -----------------------------------------------------------------
    // Send
    // -----------------------------------------------------------------

    /// <summary>
    /// Runs the approved messages. The bodies come back from the browser rather than being
    /// re-rendered here, so the bytes the operator read are the bytes that get typed. The
    /// validation chain is deliberately the same one, in the same order, as
    /// <see cref="LateOrdersController.Send"/> and <see cref="IncidentWarningsController.Send"/>.
    /// </summary>
    [HttpPost("send")]
    public IActionResult Send([FromBody] SendRequest? request)
    {
        var raw = request?.Messages ?? [];
        if (raw.Count == 0)
            return Failure("There is nothing to send.");

        if (raw.Count > WhatsAppMessageRunner.MaxGroupsPerRun)
        {
            return Failure(
                $"{raw.Count} messages is over the {WhatsAppMessageRunner.MaxGroupsPerRun}-group limit for one run. " +
                "Raise the GMV threshold and run it in batches — sending the first 40 silently would leave " +
                "you believing all of them went out.");
        }

        // The allow-list that matters: the only WhatsApp groups this app can ever post to are ones
        // the operator typed into the mapping table by hand.
        var map = _store.BuildMap();
        var messages = new List<WhatsAppMessage>(raw.Count);

        foreach (var message in raw)
        {
            var group = (message.GroupName ?? "").Trim();
            var body = (message.Body ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            if (group.Length == 0)
                return Failure("One of the messages has no WhatsApp group.");

            if (!map.HasGroup(group))
            {
                return Failure(
                    $"'{group}' is not in the seller/group mapping above. Add it there first — this app " +
                    "only sends to groups you have entered by hand.");
            }

            if (body.Length == 0)
                return Failure($"The message for '{group}' is empty.");

            if (body.Length > WhatsAppMessageRunner.MaxMessageChars)
            {
                return Failure(
                    $"The message for '{group}' is {body.Length} characters, over the " +
                    $"{WhatsAppMessageRunner.MaxMessageChars} limit.");
            }

            messages.Add(new WhatsAppMessage(group, "", (message.SellerName ?? "").Trim(), body));
        }

        // A backstop, not a gate the operator can walk into: /messages already merges every seller
        // that shares a group into one message.
        var duplicate = messages
            .GroupBy(m => m.GroupName, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            return Failure(
                $"'{duplicate.Key}' appears twice in this run and each group is messaged once. " +
                "Re-render the messages and try again.");
        }

        if (!_runner.TryStart(messages, request!.DryRun, WhatsAppMessageRunner.StockoutWarningModule))
            return Failure("An automation run is already in progress. Wait for it to finish.");

        return Success(
            request.DryRun
                ? $"Dry run started for {messages.Count:N0} group(s)."
                : $"Sending to {messages.Count:N0} group(s).",
            new { count = messages.Count, dryRun = request.DryRun });
    }

    // -----------------------------------------------------------------

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) =>
        BadRequest(new { success = false, message, data = (object?)null });

    static byte[] BuildMessagesWorkbook(IReadOnlyList<RenderedMessage> messages)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Messages");

        string[] headers = ["WhatsApp group", "Seller", "Products", "Truncated", "Message"];
        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).Value = headers[c];
        sheet.Row(1).Style.Font.Bold = true;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var row = i + 2;

            sheet.Cell(row, 1).SetValue(message.GroupName);
            sheet.Cell(row, 2).SetValue(message.SellerName);
            sheet.Cell(row, 3).SetValue(message.OrderCount);
            sheet.Cell(row, 4).SetValue(message.Truncated ? "Yes" : "No");
            sheet.Cell(row, 5).SetValue(message.Body);
        }

        // The body is text: it has to keep the line breaks that make it a message rather than a
        // paragraph.
        sheet.Column(5).Style.NumberFormat.Format = "@";
        sheet.Column(5).Style.Alignment.WrapText = true;
        sheet.Column(5).Width = 80;
        sheet.Columns(1, 4).AdjustToContents();

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
    }

    static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Matches on the normalized seller id when there is one, otherwise on the folded name —
    /// the same precedence <see cref="SellerGroupMap.Resolve"/> applies. Same call as
    /// <see cref="LateOrdersController.FindExisting"/>.</summary>
    static int FindExisting(List<SellerGroupEntry> entries, SellerGroupEntry candidate)
    {
        var id = SellerGroupMap.NormalizeSellerId(candidate.SellerId);
        if (id.Length > 0)
        {
            var byId = entries.FindIndex(e => SellerGroupMap.NormalizeSellerId(e.SellerId) == id);
            if (byId >= 0)
                return byId;
        }

        var name = SellerGroupMap.FoldName(candidate.SellerName);
        if (name.Length == 0)
            return -1;

        return entries.FindIndex(e => SellerGroupMap.FoldName(e.SellerName) == name);
    }

    /// <summary>Trims every field and drops rows with nothing to match a seller on. A row with a
    /// seller but no group is kept — that is "seen but not finished", not junk.</summary>
    static List<SellerGroupEntry> Clean(IReadOnlyList<SellerGroupEntry>? entries)
    {
        if (entries is null)
            return [];

        return [.. entries
            .Select(e => new SellerGroupEntry(
                SellerGroupMap.NormalizeSellerId(e.SellerId ?? ""),
                (e.SellerName ?? "").Trim(),
                (e.GroupName ?? "").Trim()))
            .Where(e => e.SellerId.Length > 0 || e.SellerName.Length > 0)];
    }
}
