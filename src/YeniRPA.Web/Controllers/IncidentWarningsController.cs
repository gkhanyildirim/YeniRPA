using System.Text.Json.Serialization;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Incident Warnings: finds the incidents a seller has left unanswered past the chase threshold,
/// groups them by seller, renders one message per WhatsApp group and sends them.
///
/// <para>Lives inside the Incidents Report panel and reuses the rows that panel already holds, so
/// <c>prepare</c> takes those rows in its body rather than a second upload of the same export. The
/// <c>prepare</c> → <c>messages</c> split is the same one Late Order Warnings uses: editing the
/// template re-posts the small grouped structure instead of the whole row set.</para>
///
/// <para><b>The seller → WhatsApp group mapping is shared with Late Order Warnings</b> and is edited
/// there — one seller has one group whichever module is chasing them, and a second editor for the same
/// table would be two places to keep in sync. What this controller owns is only its own message
/// templates and threshold, saved through
/// <see cref="ISellerGroupStore.SaveIncidentSettings"/> so the mapping half of that document survives.</para>
///
/// <para><b>Every JSON endpoint here returns <c>{ success, message, data }</c>.</b> They are all new,
/// so CLAUDE.md's envelope rule applies to all of them — including the failures, which is why the
/// builders' <see cref="InvalidOperationException"/> is caught locally rather than left to
/// <c>ReportExceptionFilter</c>: that filter writes <c>{ error }</c> and would break the shape this
/// panel's JavaScript reads. Same call, and the same reason, as
/// <see cref="SettingsController.ImportDatabase"/>.</para>
/// </summary>
[ApiController]
[Route("api/incident-warnings")]
public sealed class IncidentWarningsController : ControllerBase
{
    const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    readonly ISellerGroupStore _store;
    readonly WhatsAppBrowser _browser;
    readonly WhatsAppMessageRunner _runner;
    readonly AutomationJobBus _bus;

    public IncidentWarningsController(
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

    public sealed record PrepareRequest(
        [property: JsonPropertyName("rows")] IReadOnlyList<IncidentWarningInputRow>? Rows,
        [property: JsonPropertyName("thresholdDays")] int ThresholdDays);

    public sealed record MessagesRequest(
        [property: JsonPropertyName("sellers")] IReadOnlyList<IncidentWarningSeller>? Sellers,
        [property: JsonPropertyName("referenceTime")] string? ReferenceTime,
        [property: JsonPropertyName("template")] string? Template,
        [property: JsonPropertyName("lineTemplate")] string? LineTemplate);

    public sealed record MessagesExcelRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<RenderedMessage>? Messages);

    public sealed record SettingsRequest(
        [property: JsonPropertyName("template")] string? Template,
        [property: JsonPropertyName("lineTemplate")] string? LineTemplate,
        [property: JsonPropertyName("thresholdDays")] int ThresholdDays);

    public sealed record SendMessage(
        [property: JsonPropertyName("groupName")] string? GroupName,
        [property: JsonPropertyName("sellerName")] string? SellerName,
        [property: JsonPropertyName("body")] string? Body);

    public sealed record SendRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<SendMessage>? Messages,
        [property: JsonPropertyName("dryRun")] bool DryRun);

    // -----------------------------------------------------------------
    // Prepare
    // -----------------------------------------------------------------

    /// <summary>
    /// Applies the eligibility rule to the rows the dashboard is holding and groups the survivors by
    /// seller. The rule lives in <see cref="IncidentWarningBuilder"/>, not in the browser: the group
    /// allow-list on <c>send</c> is only a last line of defence, and who gets chased should not be
    /// decided by whatever the page happened to have filtered at the time.
    /// </summary>
    [HttpPost("prepare")]
    public IActionResult Prepare([FromBody] PrepareRequest? request)
    {
        var rows = request?.Rows ?? [];
        if (rows.Count == 0)
            return Failure("Generate the incidents dashboard first — there are no incidents to check.");

        try
        {
            var data = IncidentWarningBuilder.Build(rows, request!.ThresholdDays, _store.BuildMap());

            return Success(
                data.Funnel.Eligible == 0
                    ? $"No incident has been open for {data.ThresholdDays} day(s) with the seller still to reply."
                    : $"{data.Funnel.Eligible:N0} incident(s) across {data.Funnel.Sellers:N0} seller(s) are past the {data.ThresholdDays}-day threshold.",
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

        // Grouped by destination, not by seller: two seller names mapped to one group get one message,
        // not two in the same chat. Merging here rather than at send time keeps the preview cards, the
        // Excel export and the typed keystrokes identical. GroupBy preserves first-occurrence order,
        // so the seller with the oldest incident still sorts first.
        var rendered = sellers
            .Where(s => !string.IsNullOrWhiteSpace(s.GroupName))
            .GroupBy(s => s.GroupName!.Trim(), StringComparer.Ordinal)
            .Select(group => IncidentWarningMessageBuilder.Render(
                [.. group], request?.ReferenceTime ?? "", request?.Template, request?.LineTemplate))
            .ToList();

        // overLimit rather than a refusal: the send endpoint rejects an oversized body, and finding
        // that out after pressing Send — on a message the operator has already read and approved — is
        // the wrong moment. Flagged on the card instead, while the template is still editable.
        var messages = rendered.Select(m => new
        {
            groupName = m.GroupName,
            sellerId = m.SellerId,
            sellerName = m.SellerName,
            body = m.Body,
            orderCount = m.OrderCount,
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

        if (messages.Any(m => m.overLimit))
        {
            warnings.Add(
                $"Some messages are over the {WhatsAppMessageRunner.MaxMessageChars}-character limit and " +
                "will be refused. Shorten the template or raise the day threshold to list fewer incidents.");
        }

        return Success($"{messages.Count:N0} message(s) rendered.", new { messages, warnings });
    }

    /// <summary>
    /// A dedicated export rather than the generic <c>/api/export/xlsx</c>: that path reads cells back
    /// out of rendered HTML and collapses whitespace runs, which would flatten a multi-line message
    /// body into one long line. Its own workbook rather than the late-order one, because that sheet
    /// has a "Seller ID" column this module can never fill and calls the count "Orders".
    /// </summary>
    [HttpPost("messages/excel")]
    public IActionResult MessagesExcel([FromBody] MessagesExcelRequest? request)
    {
        var messages = request?.Messages ?? [];
        if (messages.Count == 0)
            return Failure("There is nothing to export.");

        return File(BuildMessagesWorkbook(messages), XlsxContentType,
            $"incident-warnings-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    // -----------------------------------------------------------------
    // Session + settings
    // -----------------------------------------------------------------

    /// <summary>
    /// Read-only. There is deliberately no login / check-session / clear-session here: those three
    /// mutate one global Chrome profile that both WhatsApp modules share, and a second set of buttons
    /// for it would let an operator wipe the session out from under the other panel. The badge this
    /// feeds points at Late Order Warnings when a sign-in is needed.
    /// </summary>
    [HttpGet("status")]
    public IActionResult Status() => Success("", new
    {
        hasProfile = _browser.HasProfile,
        signedIn = _browser.LastKnownSignedIn,
        lastCheckedUtc = _browser.LastCheckedUtc,
        isRunning = _bus.IsRunning,
        runningModule = _bus.RunningModule,
        maxGroupsPerRun = WhatsAppMessageRunner.MaxGroupsPerRun,
        maxMessageChars = WhatsAppMessageRunner.MaxMessageChars,
        maxIncidentLines = IncidentWarningBuilder.MaxIncidentLinesPerMessage,
    });

    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        var file = _store.Load();

        return Success("", new
        {
            template = file.IncidentMessageTemplate ?? IncidentWarningMessageBuilder.DefaultTemplate,
            lineTemplate = file.IncidentLineTemplate ?? IncidentWarningMessageBuilder.DefaultIncidentLineTemplate,
            defaultTemplate = IncidentWarningMessageBuilder.DefaultTemplate,
            defaultLineTemplate = IncidentWarningMessageBuilder.DefaultIncidentLineTemplate,
            placeholders = IncidentWarningMessageBuilder.KnownPlaceholders,
            thresholdDays = file.IncidentThresholdDays ?? IncidentWarningBuilder.DefaultThresholdDays,
            minThresholdDays = IncidentWarningBuilder.MinThresholdDays,
            maxThresholdDays = IncidentWarningBuilder.MaxThresholdDays,
            updatedUtc = file.UpdatedUtc,
        });
    }

    [HttpPut("settings")]
    public IActionResult SaveSettings([FromBody] SettingsRequest? request)
    {
        var threshold = Math.Clamp(
            request?.ThresholdDays ?? IncidentWarningBuilder.DefaultThresholdDays,
            IncidentWarningBuilder.MinThresholdDays,
            IncidentWarningBuilder.MaxThresholdDays);

        // SaveIncidentSettings, not Save: the same document carries the seller mapping and the
        // late-order templates, which this request knows nothing about.
        _store.SaveIncidentSettings(
            NullIfBlank(request?.Template),
            NullIfBlank(request?.LineTemplate),
            threshold);

        return Success("Incident warning settings saved.", new { thresholdDays = threshold });
    }

    // -----------------------------------------------------------------
    // Send
    // -----------------------------------------------------------------

    /// <summary>
    /// Runs the approved messages. The bodies come back from the browser rather than being re-rendered
    /// here, so the bytes the operator read are the bytes that get typed — re-rendering server-side
    /// would create two rendering paths that could disagree, and the one place they would disagree is
    /// between what was approved and what a seller receives.
    ///
    /// <para>The validation chain is deliberately the same one, in the same order, as
    /// <see cref="LateOrdersController.Send"/>. It is duplicated rather than shared because each check
    /// carries a message naming this module's own panel, and because a shared helper is a single place
    /// where relaxing a guard would silently relax it for both modules at once.</para>
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
                "Raise the day threshold and run it in batches — sending the first 40 silently would leave " +
                "you believing all of them went out.");
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
                return Failure("One of the messages has no WhatsApp group.");

            if (!map.HasGroup(group))
            {
                return Failure(
                    $"'{group}' is not in the seller/group mapping. Add it on the Late Order Warnings tab " +
                    "first — this app only sends to groups you have entered by hand.");
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

        // A backstop, not a gate the operator can walk into: /messages already merges every seller that
        // shares a group into one message. Bodies arrive from the browser and are not re-rendered here,
        // so this stays as the last thing standing between a stale page and two messages in one chat.
        var duplicate = messages
            .GroupBy(m => m.GroupName, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            return Failure(
                $"'{duplicate.Key}' appears twice in this run and each group is messaged once. " +
                "Re-render the messages and try again.");
        }

        if (!_runner.TryStart(messages, request!.DryRun, WhatsAppMessageRunner.IncidentWarningModule))
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

        string[] headers = ["WhatsApp group", "Seller", "Incidents", "Truncated", "Message"];
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
}
