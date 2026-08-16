using System.Text.Json.Serialization;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Late Order Warnings: finds the currently-overdue orders in an orders export, groups them by
/// seller, renders one message per seller, and owns the seller → WhatsApp group mapping.
///
/// <para><c>prepare</c> returns the rows and <c>messages</c> renders the text from them, so editing
/// the template re-posts a few KB of already-parsed rows instead of re-uploading a ~13 MB export.
/// Same split as <c>create-return/prepare</c> → <c>start-list</c>.</para>
///
/// <para>Every entry point validates synchronously and lets <c>ReportExceptionFilter</c> turn a
/// builder's <see cref="InvalidOperationException"/> into <c>400 { error }</c>.</para>
/// </summary>
[ApiController]
[Route("api/late-orders")]
public sealed class LateOrdersController : ControllerBase
{
    const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    readonly SellerGroupStore _store;
    readonly WhatsAppBrowser _browser;
    readonly LateOrderWhatsAppRunner _runner;
    readonly AutomationJobBus _bus;

    public LateOrdersController(
        SellerGroupStore store,
        WhatsAppBrowser browser,
        LateOrderWhatsAppRunner runner,
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
        [property: JsonPropertyName("sellers")] IReadOnlyList<LateOrderSeller>? Sellers,
        [property: JsonPropertyName("referenceTime")] string? ReferenceTime,
        [property: JsonPropertyName("template")] string? Template,
        [property: JsonPropertyName("orderLineTemplate")] string? OrderLineTemplate);

    public sealed record MessagesExcelRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<RenderedMessage>? Messages);

    public sealed record MappingRequest(
        [property: JsonPropertyName("entries")] IReadOnlyList<SellerGroupEntry>? Entries,
        [property: JsonPropertyName("template")] string? Template,
        [property: JsonPropertyName("orderLineTemplate")] string? OrderLineTemplate);

    public sealed record MappingExcelRequest(
        [property: JsonPropertyName("entries")] IReadOnlyList<SellerGroupEntry>? Entries);

    public sealed record SendMessage(
        [property: JsonPropertyName("groupName")] string? GroupName,
        [property: JsonPropertyName("sellerId")] string? SellerId,
        [property: JsonPropertyName("sellerName")] string? SellerName,
        [property: JsonPropertyName("body")] string? Body);

    public sealed record SendRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<SendMessage>? Messages,
        [property: JsonPropertyName("dryRun")] bool DryRun);

    // -----------------------------------------------------------------
    // Prepare
    // -----------------------------------------------------------------

    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare(
        IFormFile? file,
        // [FromForm] is required: [ApiController] infers query-string binding for simple types, so
        // without it the offset silently arrives as 0 no matter what the operator typed.
        [FromForm] double offsetHours,
        CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the Mirakl orders export." });

        using var stream = await CopyToSeekableStreamAsync(file, cancellationToken);

        var data = LateOrderBuilder.Build(stream, file.FileName, new LateOrderOptions(offsetHours), _store.BuildMap());
        return Ok(data);
    }

    // -----------------------------------------------------------------
    // Messages
    // -----------------------------------------------------------------

    [HttpPost("messages")]
    public IActionResult Messages([FromBody] MessagesRequest? request)
    {
        var sellers = request?.Sellers ?? [];

        // Unmapped sellers are not messaged, but they are also not the caller's mistake — the panel
        // reports them separately, so they are simply not rendered here.
        var messages = sellers
            .Where(s => !string.IsNullOrWhiteSpace(s.GroupName))
            .Select(s => LateOrderMessageBuilder.Render(
                s, request?.ReferenceTime ?? "", request?.Template, request?.OrderLineTemplate))
            .ToList();

        var warnings = messages
            .SelectMany(m => m.UnknownPlaceholders)
            .Distinct(StringComparer.Ordinal)
            .Select(token => $"'{token}' is not a placeholder and was left in the message text as-is.")
            .ToList();

        return Ok(new { messages, warnings });
    }

    /// <summary>
    /// A dedicated export rather than the generic <c>/api/export/xlsx</c>: that path reads cells back
    /// out of rendered HTML and collapses whitespace runs, which would flatten a multi-line message
    /// body into one long line.
    /// </summary>
    [HttpPost("messages/excel")]
    public IActionResult MessagesExcel([FromBody] MessagesExcelRequest? request)
    {
        var messages = request?.Messages ?? [];
        if (messages.Count == 0)
            return BadRequest(new { error = "There is nothing to export." });

        return File(BuildMessagesWorkbook(messages), XlsxContentType, $"late-order-messages-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    // -----------------------------------------------------------------
    // Session + send
    // -----------------------------------------------------------------

    /// <summary>
    /// Its own endpoint rather than an extension of <c>/api/automation/status</c>: that one reports the
    /// Mirakl browser's saved-session state, and widening it would make create-return.js read fields it
    /// has no use for. The run-slot fields are repeated from the same shared bus so this panel needs
    /// one call, and so Send can be disabled with a reason instead of failing after the click.
    /// </summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        hasProfile = _browser.HasProfile,
        signedIn = _browser.LastKnownSignedIn,
        lastCheckedUtc = _browser.LastCheckedUtc,
        browserReady = _browser.IsBrowserReady,
        isRunning = _bus.IsRunning,
        runningModule = _bus.RunningModule,
        profilePath = _browser.ProfilePath,
        maxGroupsPerRun = LateOrderWhatsAppRunner.MaxGroupsPerRun,
        maxMessageChars = LateOrderWhatsAppRunner.MaxMessageChars
    });

    /// <summary>Opens a real Chrome window on WhatsApp Web for the QR scan. Blocks until it is up.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login()
    {
        await _browser.OpenLoginAsync();
        return Ok();
    }

    [HttpPost("check-session")]
    public async Task<IActionResult> CheckSession() => Ok(new { signedIn = await _browser.CheckSignedInAsync() });

    [HttpPost("clear-session")]
    public async Task<IActionResult> ClearSession()
    {
        if (_bus.IsRunning)
            return BadRequest(new { error = "An automation run is in progress. Wait for it to finish." });

        return Ok(new { message = await _browser.ClearSessionAsync() });
    }

    /// <summary>
    /// Runs the approved messages. The bodies come back from the browser rather than being re-rendered
    /// here, so the bytes the operator read are the bytes that get typed — re-rendering server-side
    /// would create two rendering paths that could disagree, and the one place they would disagree is
    /// between what was approved and what a seller receives.
    /// </summary>
    [HttpPost("send")]
    public IActionResult Send([FromBody] SendRequest? request)
    {
        var raw = request?.Messages ?? [];
        if (raw.Count == 0)
            return BadRequest(new { error = "There is nothing to send." });

        if (raw.Count > LateOrderWhatsAppRunner.MaxGroupsPerRun)
        {
            return BadRequest(new
            {
                error = $"{raw.Count} messages is over the {LateOrderWhatsAppRunner.MaxGroupsPerRun}-group limit for one run. " +
                        "Narrow the list and run it in batches — sending the first 40 silently would leave you " +
                        "believing all of them went out."
            });
        }

        // The allow-list that matters: the only WhatsApp groups this app can ever post to are ones the
        // operator typed into the mapping table by hand. A group name arriving from anywhere else is
        // refused outright.
        var map = _store.BuildMap();
        var messages = new List<WhatsAppMessage>(raw.Count);

        foreach (var message in raw)
        {
            var group = (message.GroupName ?? "").Trim();
            var body = (message.Body ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            if (group.Length == 0)
                return BadRequest(new { error = "One of the messages has no WhatsApp group." });

            if (!map.HasGroup(group))
            {
                return BadRequest(new
                {
                    error = $"'{group}' is not in the seller/group mapping. Add it there first — this app only " +
                            "sends to groups you have entered by hand."
                });
            }

            if (body.Length == 0)
                return BadRequest(new { error = $"The message for '{group}' is empty." });

            if (body.Length > LateOrderWhatsAppRunner.MaxMessageChars)
            {
                return BadRequest(new
                {
                    error = $"The message for '{group}' is {body.Length} characters, over the " +
                            $"{LateOrderWhatsAppRunner.MaxMessageChars} limit."
                });
            }

            messages.Add(new WhatsAppMessage(group, (message.SellerId ?? "").Trim(), (message.SellerName ?? "").Trim(), body));
        }

        var duplicate = messages
            .GroupBy(m => m.GroupName, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
            return BadRequest(new { error = $"'{duplicate.Key}' appears twice in this run. Each group is messaged once." });

        if (!_runner.TryStart(messages, request!.DryRun))
            return BadRequest(new { error = "An automation run is already in progress. Wait for it to finish." });

        return Ok(new { count = messages.Count, dryRun = request.DryRun });
    }

    // -----------------------------------------------------------------
    // Mapping
    // -----------------------------------------------------------------

    [HttpGet("mapping")]
    public IActionResult GetMapping()
    {
        var file = _store.Load();
        var map = SellerGroupMap.FromEntries(file.Entries);

        return Ok(new
        {
            entries = file.Entries,
            template = file.MessageTemplate ?? LateOrderMessageBuilder.DefaultTemplate,
            orderLineTemplate = file.OrderLineTemplate ?? LateOrderMessageBuilder.DefaultOrderLineTemplate,
            defaultTemplate = LateOrderMessageBuilder.DefaultTemplate,
            defaultOrderLineTemplate = LateOrderMessageBuilder.DefaultOrderLineTemplate,
            placeholders = LateOrderMessageBuilder.KnownPlaceholders,
            path = _store.FilePath,
            updatedUtc = file.UpdatedUtc,
            warnings = map.LoadWarnings
        });
    }

    [HttpPut("mapping")]
    public IActionResult SaveMapping([FromBody] MappingRequest? request)
    {
        var entries = Clean(request?.Entries);

        _store.Save(new SellerGroupFile(
            Version: 0,                       // stamped by the store
            UpdatedUtc: null,                 // stamped by the store
            MessageTemplate: NullIfBlank(request?.Template),
            OrderLineTemplate: NullIfBlank(request?.OrderLineTemplate),
            Entries: entries));

        return Ok(new
        {
            saved = entries.Count,
            path = _store.FilePath,
            warnings = SellerGroupMap.FromEntries(entries).LoadWarnings
        });
    }

    /// <summary>
    /// Returns the merged table for review; it does <b>not</b> save. An import that silently
    /// overwrote a hand-built mapping from a wrong-shaped file would only be recoverable from the
    /// backup, so the operator looks at the result and presses Save.
    /// </summary>
    [HttpPost("mapping/import")]
    public async Task<IActionResult> ImportMapping(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = "Please upload a mapping file (.xlsx or .csv)." });

        using var stream = await CopyToSeekableStreamAsync(file, cancellationToken);
        var imported = SellerGroupStore.ReadWorkbook(stream, file.FileName);

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

        return Ok(new { entries = merged, added, updated, skipped });
    }

    [HttpPost("mapping/excel")]
    public IActionResult MappingExcel([FromBody] MappingExcelRequest? request)
    {
        var entries = Clean(request?.Entries);
        if (entries.Count == 0)
            return BadRequest(new { error = "The mapping table is empty." });

        return File(SellerGroupStore.BuildWorkbook(entries), XlsxContentType, "seller-groups.xlsx");
    }

    // -----------------------------------------------------------------

    /// <summary>Matches on the normalized seller id when there is one, otherwise on the folded name —
    /// the same precedence <see cref="SellerGroupMap.Resolve"/> applies.</summary>
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

    static byte[] BuildMessagesWorkbook(IReadOnlyList<RenderedMessage> messages)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Messages");

        string[] headers = ["WhatsApp group", "Seller ID", "Seller", "Orders", "Truncated", "Message"];
        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).Value = headers[c];
        sheet.Row(1).Style.Font.Bold = true;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var row = i + 2;

            sheet.Cell(row, 1).SetValue(message.GroupName);
            sheet.Cell(row, 2).SetValue(message.SellerId);
            sheet.Cell(row, 3).SetValue(message.SellerName);
            sheet.Cell(row, 4).SetValue(message.OrderCount);
            sheet.Cell(row, 5).SetValue(message.Truncated ? "Yes" : "No");
            sheet.Cell(row, 6).SetValue(message.Body);
        }

        // Ids and the body are text: an id loses its leading zeros as a number, and the body has to
        // keep the line breaks that make it a message rather than a paragraph.
        sheet.Column(2).Style.NumberFormat.Format = "@";
        sheet.Column(6).Style.NumberFormat.Format = "@";
        sheet.Column(6).Style.Alignment.WrapText = true;
        sheet.Column(6).Width = 80;
        sheet.Columns(1, 5).AdjustToContents();

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
    }

    static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>ClosedXML needs a seekable stream; the raw request body is not one.</summary>
    static async Task<MemoryStream> CopyToSeekableStreamAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        stream.Position = 0;
        return stream;
    }
}
