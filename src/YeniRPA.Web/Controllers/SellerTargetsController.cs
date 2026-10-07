using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Seller Targets: tells each seller their monthly target, what they have reached so far and the
/// completion percentage, by Outlook mail and by WhatsApp group message.
///
/// <para>Same <c>prepare</c> → <c>messages</c> → <c>send</c> split as the other warning modules:
/// <c>prepare</c> parses the workbook once, and the browser re-posts the small row list when the
/// templates change. Sending reuses <see cref="OfferMailRunner"/> (no attachment) and
/// <see cref="WhatsAppMessageRunner"/> exactly as the sibling modules do.</para>
///
/// <para><b>Contacts are shared, not duplicated.</b> A seller's address is the hand-entered address in
/// <see cref="ICustomMailStore"/> and the group is the shared <see cref="ISellerGroupStore"/> mapping,
/// so one seller has one address and one group whichever module is writing to them. Both send
/// endpoints re-check the destination against those stores — the browser only chooses <em>who</em>
/// from the prepared list, never <em>where</em> a message goes.</para>
///
/// <para>Every JSON endpoint returns <c>{ success, message, data }</c>.</para>
/// </summary>
[ApiController]
[Route("api/seller-targets")]
[SupportedOSPlatform("windows")]
public sealed class SellerTargetsController : ControllerBase
{
    public const string MailModuleName = "seller-targets-mail";

    // The contacts save is a load-merge-save across two stores; one lock keeps two saves from
    // interleaving and losing each other's rows.
    static readonly object ContactsLock = new();

    // Addresses the last prepare resolved from the uploaded directory. In memory on purpose: like the
    // other prepare-then-send batches, it is meant to be invalidated by a restart.
    static readonly object PreparedLock = new();
    static HashSet<string> _preparedAddresses = new(StringComparer.Ordinal);

    readonly ISellerGroupStore _groups;
    readonly ICustomMailStore _mail;
    readonly OutlookMailSender _sender;
    readonly OfferMailRunner _mailRunner;
    readonly WhatsAppBrowser _browser;
    readonly WhatsAppMessageRunner _whatsAppRunner;
    readonly AutomationJobBus _bus;

    public SellerTargetsController(
        ISellerGroupStore groups,
        ICustomMailStore mail,
        OutlookMailSender sender,
        OfferMailRunner mailRunner,
        WhatsAppBrowser browser,
        WhatsAppMessageRunner whatsAppRunner,
        AutomationJobBus bus)
    {
        _groups = groups;
        _mail = mail;
        _sender = sender;
        _mailRunner = mailRunner;
        _browser = browser;
        _whatsAppRunner = whatsAppRunner;
        _bus = bus;
    }

    // -----------------------------------------------------------------
    // Request shapes
    // -----------------------------------------------------------------

    public sealed record MessagesRequest(
        [property: JsonPropertyName("rows")] IReadOnlyList<SellerTargetRow>? Rows,
        [property: JsonPropertyName("month")] string? Month,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("belowBody")] string? BelowBody,
        [property: JsonPropertyName("aboveBody")] string? AboveBody,
        [property: JsonPropertyName("threshold")] double? Threshold);

    public sealed record ContactEntry(
        [property: JsonPropertyName("sellerId")] string? SellerId,
        [property: JsonPropertyName("sellerName")] string? SellerName,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("groupName")] string? GroupName);

    public sealed record ContactsRequest(
        [property: JsonPropertyName("contacts")] IReadOnlyList<ContactEntry>? Contacts);

    public sealed record SendMailMessage(
        [property: JsonPropertyName("sellerId")] string? SellerId,
        [property: JsonPropertyName("sellerName")] string? SellerName,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("body")] string? Body);

    public sealed record SendMailRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<SendMailMessage>? Messages,
        [property: JsonPropertyName("dryRun")] bool DryRun,
        [property: JsonPropertyName("includeSignature")] bool IncludeSignature);

    public sealed record SendWhatsAppMessage(
        [property: JsonPropertyName("sellerId")] string? SellerId,
        [property: JsonPropertyName("sellerName")] string? SellerName,
        [property: JsonPropertyName("body")] string? Body);

    public sealed record SendWhatsAppRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<SendWhatsAppMessage>? Messages,
        [property: JsonPropertyName("dryRun")] bool DryRun);

    // -----------------------------------------------------------------
    // Status and templates
    // -----------------------------------------------------------------

    /// <summary>Deliberately does not probe Outlook — see <see cref="CustomMailController.Status"/>.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Success("", new
    {
        outlookAvailable = _sender.LastKnownAvailable,
        outlookError = _sender.LastError,
        hasProfile = _browser.HasProfile,
        signedIn = _browser.LastKnownSignedIn,
        isRunning = _bus.IsRunning,
        runningModule = _bus.RunningModule,
        maxMailsPerRun = OfferMailRunner.MaxMailsPerRun,
        maxGroupsPerRun = WhatsAppMessageRunner.MaxGroupsPerRun,
        maxMessageChars = WhatsAppMessageRunner.MaxMessageChars,
    });

    [HttpPost("check-outlook")]
    public async Task<IActionResult> CheckOutlook() =>
        Success("", new { available = await _sender.ProbeAsync(), error = _sender.LastError });

    [HttpPost("login")]
    public async Task<IActionResult> Login()
    {
        await _browser.OpenLoginAsync();
        return Success("", null);
    }

    [HttpPost("check-session")]
    public async Task<IActionResult> CheckSession() =>
        Success("", new { signedIn = await _browser.CheckSignedInAsync() });

    [HttpGet("templates")]
    public IActionResult Templates() => Success("", new
    {
        subject = SellerTargetMessageBuilder.DefaultMailSubject,
        belowBody = SellerTargetMessageBuilder.DefaultBelowBody,
        aboveBody = SellerTargetMessageBuilder.DefaultAboveBody,
        threshold = SellerTargetMessageBuilder.DefaultThreshold,
        placeholders = SellerTargetMessageBuilder.KnownPlaceholders,
    });

    // -----------------------------------------------------------------
    // Prepare
    // -----------------------------------------------------------------

    /// <summary>
    /// Reads the workbook and resolves every seller's address and WhatsApp group. A seller with no
    /// current revenue in the file is listed but flagged <c>hasData = false</c> and gets no message.
    /// </summary>
    [HttpPost("prepare")]
    public IActionResult Prepare(IFormFile? file, IFormFile? directory)
    {
        if (file is not { Length: > 0 })
            return Failure("Please upload the seller target workbook (.xlsx).");

        SellerTargetSheet sheet;
        SellerMailDirectory? addresses = null;
        try
        {
            using (var stream = file.OpenReadStream())
                sheet = SellerTargetReader.Read(stream, file.FileName);

            if (directory is { Length: > 0 })
            {
                using var stream = directory.OpenReadStream();
                addresses = SellerMailDirectory.Read(stream, directory.FileName, SellerMailDirectory.DefaultSheetName);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        var overrides = _mail.Load().Overrides;
        var map = _groups.BuildMap();

        var sellers = sheet.Rows.Select(row =>
        {
            // The hand-entered address wins over the directory, as in Custom Mail.
            var email = CustomMailStore.FindOverride(overrides, row.SellerId, row.SellerName);
            string? directoryProblem = null;
            if (email is null && addresses is not null)
            {
                var match = addresses.Find(row.SellerId, row.SellerName);
                email = match.Email;
                directoryProblem = match.Problem;
            }

            var emailOk = email is not null && SellerMailStore.SplitAddresses(email).Any(SellerMailStore.LooksLikeEmail);
            if (emailOk)
                email = SellerMailStore.SplitAddresses(email!).First(SellerMailStore.LooksLikeEmail);
            var group = map.Resolve(row.SellerId, row.SellerName);

            return new
            {
                sellerId = row.SellerId,
                sellerName = row.SellerName,
                target = row.Target,
                current = row.Current,
                days = row.Days,
                daysInMonth = row.DaysInMonth,
                hasData = row.Current.HasValue && row.Target > 0,
                forecastPercent = row.Current.HasValue ? SellerTargetMessageBuilder.ForecastPercent(row) : (double?)null,
                percent = row.Current.HasValue ? SellerTargetMessageBuilder.Percent(row.Target, row.Current.Value) : (double?)null,
                email = emailOk ? email : null,
                emailProblem = emailOk ? null : email is null ? (directoryProblem ?? "No e-mail address is saved for this seller.") : "The address does not look valid.",
                groupName = group.GroupName,
                groupProblem = group.Problem,
            };
        }).ToList();

        lock (PreparedLock)
        {
            _preparedAddresses = sellers
                .Where(s => s.email is not null)
                .Select(s => SellerMailStore.NormalizeEmail(s.email!))
                .ToHashSet(StringComparer.Ordinal);
        }

        var withData = sellers.Count(s => s.hasData);
        return Success(
            $"{sellers.Count:N0} seller(s) read, {withData:N0} with a current revenue figure.",
            new { month = sheet.Month, sellers, withData });
    }

    // -----------------------------------------------------------------
    // Messages
    // -----------------------------------------------------------------

    [HttpPost("messages")]
    public IActionResult Messages([FromBody] MessagesRequest? request)
    {
        var rows = (request?.Rows ?? []).Where(r => r.Current.HasValue && r.Target > 0).ToList();

        var rendered = rows
            .Select(row => SellerTargetMessageBuilder.Render(
                row, request?.Month ?? "", request?.Subject, request?.BelowBody, request?.AboveBody,
                Math.Clamp(request?.Threshold ?? SellerTargetMessageBuilder.DefaultThreshold, 0, 1000)))
            .ToList();

        var warnings = rendered
            .SelectMany(m => m.UnknownPlaceholders)
            .Distinct(StringComparer.Ordinal)
            .Select(token => $"'{token}' is not a placeholder and was left in the text as-is.")
            .ToList();

        if (rendered.Any(m => m.WhatsAppBody.Length > WhatsAppMessageRunner.MaxMessageChars))
            warnings.Add($"A WhatsApp message is over the {WhatsAppMessageRunner.MaxMessageChars}-character limit and will be refused.");

        var messages = rendered.Select(m => new
        {
            sellerId = m.SellerId,
            sellerName = m.SellerName,
            mailSubject = m.MailSubject,
            mailBody = m.MailBody,
            whatsAppBody = m.WhatsAppBody,
            belowThreshold = m.BelowThreshold,
        });

        return Success($"{rendered.Count:N0} notification(s) rendered.", new { messages, warnings });
    }

    // -----------------------------------------------------------------
    // Contacts
    // -----------------------------------------------------------------

    /// <summary>
    /// Saves the addresses and groups typed in the contacts dialog. A blank cell leaves what is
    /// already stored untouched — clearing a contact is done in the module that owns it.
    /// </summary>
    [HttpPut("contacts")]
    public IActionResult SaveContacts([FromBody] ContactsRequest? request)
    {
        var contacts = (request?.Contacts ?? [])
            .Select(c => new ContactEntry(
                SellerGroupMap.NormalizeSellerId(c.SellerId ?? ""),
                (c.SellerName ?? "").Trim(),
                SellerMailStore.JoinAddresses(SellerMailStore.SplitAddresses(c.Email)),
                (c.GroupName ?? "").Trim()))
            .Where(c => c.SellerId!.Length > 0 || c.SellerName!.Length > 0)
            .ToList();

        foreach (var contact in contacts)
        {
            var bad = SellerMailStore.SplitAddresses(contact.Email).FirstOrDefault(a => !SellerMailStore.LooksLikeEmail(a));
            if (bad is not null)
                return Failure($"'{bad}' does not look like an e-mail address ({contact.SellerName}).");
        }

        int mailSaved = 0, groupsSaved = 0;
        lock (ContactsLock)
        {
            var overrides = _mail.Load().Overrides.ToList();
            var entries = _groups.Load().Entries.ToList();

            foreach (var contact in contacts)
            {
                if (contact.Email!.Length > 0)
                {
                    var key = CustomMailSellerListReader.SellerKey(contact.SellerId!, contact.SellerName!);
                    var next = new CustomMailOverrideEntry(contact.SellerId!, contact.SellerName!, contact.Email);
                    var index = overrides.FindIndex(o => CustomMailSellerListReader.SellerKey(o.SellerId, o.SellerName) == key);
                    if (index >= 0) overrides[index] = next; else overrides.Add(next);
                    mailSaved++;
                }

                if (contact.GroupName!.Length > 0)
                {
                    var index = FindGroupEntry(entries, contact.SellerId!, contact.SellerName!);
                    var next = new SellerGroupEntry(contact.SellerId!, contact.SellerName!, contact.GroupName);
                    if (index >= 0) entries[index] = next; else entries.Add(next);
                    groupsSaved++;
                }
            }

            if (mailSaved > 0)
                _mail.Save(new CustomMailFile(null, overrides));

            if (groupsSaved > 0)
                _groups.SaveEntries(entries);
        }

        return Success($"{mailSaved:N0} address(es) and {groupsSaved:N0} group(s) saved.", new { mailSaved, groupsSaved });
    }

    // -----------------------------------------------------------------
    // Send
    // -----------------------------------------------------------------

    [HttpPost("send-mail")]
    public IActionResult SendMail([FromBody] SendMailRequest? request)
    {
        var raw = request?.Messages ?? [];
        if (raw.Count == 0)
            return Failure("There is nothing to send.");

        if (raw.Count > OfferMailRunner.MaxMailsPerRun)
            return Failure($"{raw.Count:N0} mails is over the {OfferMailRunner.MaxMailsPerRun:N0}-mail ceiling for one run. Untick part of the list.");

        var overrides = _mail.Load().Overrides;
        var mails = new List<OutgoingMail>(raw.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in raw)
        {
            var name = (message.SellerName ?? "").Trim();
            var subject = (message.Subject ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            var body = (message.Body ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            // The address must be one this server resolved itself: from the last prepare (workbook +
            // directory) or the hand-entered list. The browser only picks who, never where.
            var email = SellerMailStore.SplitAddresses(message.Email).FirstOrDefault(SellerMailStore.LooksLikeEmail);
            var saved = CustomMailStore.FindOverride(overrides, message.SellerId ?? "", name);
            bool known;
            lock (PreparedLock)
                known = email is not null && (_preparedAddresses.Contains(SellerMailStore.NormalizeEmail(email)) ||
                    (saved is not null && SellerMailStore.SplitAddresses(saved).Contains(email, StringComparer.OrdinalIgnoreCase)));

            if (email is null || !known)
                return Failure($"'{name}' has no usable address from the last upload or the saved list. Read the workbooks again, or add it under Contacts — this app only mails addresses it resolved itself.");

            if (!seen.Add(SellerMailStore.NormalizeEmail(email)))
                return Failure($"'{email}' appears twice in this run. Each address is mailed once.");

            if (subject.Length == 0)
                return Failure($"The mail for '{name}' has no subject.");

            if (body.Length == 0)
                return Failure($"The mail for '{name}' is empty.");

            mails.Add(new OutgoingMail(
                To: email,
                SellerId: message.SellerId ?? "",
                SellerName: name,
                Subject: subject,
                Body: body,
                AttachmentPath: null,
                AttachmentName: "",
                IncludeSignature: request!.IncludeSignature));
        }

        if (!_mailRunner.TryStart(mails, request!.DryRun, MailModuleName))
            return Failure("An automation run is already in progress. Wait for it to finish.");

        return Success(
            request.DryRun ? $"Dry run started for {mails.Count:N0} mail(s)." : $"Sending {mails.Count:N0} mail(s).",
            new { count = mails.Count, dryRun = request.DryRun });
    }

    [HttpPost("send-whatsapp")]
    public IActionResult SendWhatsApp([FromBody] SendWhatsAppRequest? request)
    {
        var raw = request?.Messages ?? [];
        if (raw.Count == 0)
            return Failure("There is nothing to send.");

        if (raw.Count > WhatsAppMessageRunner.MaxGroupsPerRun)
        {
            return Failure(
                $"{raw.Count} messages is over the {WhatsAppMessageRunner.MaxGroupsPerRun}-group limit for one run. " +
                "Untick part of the list and send in batches.");
        }

        // The group is resolved here from the saved mapping, not taken from the request: the only
        // chats this app can post to are ones the operator typed into the mapping by hand.
        var map = _groups.BuildMap();
        var messages = new List<WhatsAppMessage>(raw.Count);

        foreach (var message in raw)
        {
            var name = (message.SellerName ?? "").Trim();
            var body = (message.Body ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            var match = map.Resolve(message.SellerId ?? "", name);
            if (match.GroupName is null || !map.HasGroup(match.GroupName))
                return Failure($"'{name}': {match.Problem ?? "No WhatsApp group is mapped for this seller."}");

            if (body.Length == 0)
                return Failure($"The message for '{name}' is empty.");

            if (body.Length > WhatsAppMessageRunner.MaxMessageChars)
                return Failure($"The message for '{name}' is {body.Length} characters, over the {WhatsAppMessageRunner.MaxMessageChars} limit.");

            messages.Add(new WhatsAppMessage(match.GroupName, message.SellerId ?? "", name, body));
        }

        var duplicate = messages.GroupBy(m => m.GroupName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return Failure(
                $"'{duplicate.Key}' is the group of more than one selected seller and each group is messaged once. " +
                "Untick one of them.");
        }

        if (!_whatsAppRunner.TryStart(messages, request!.DryRun, WhatsAppMessageRunner.SellerTargetsModule))
            return Failure("An automation run is already in progress. Wait for it to finish.");

        return Success(
            request.DryRun ? $"Dry run started for {messages.Count:N0} group(s)." : $"Sending to {messages.Count:N0} group(s).",
            new { count = messages.Count, dryRun = request.DryRun });
    }

    // -----------------------------------------------------------------

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) =>
        BadRequest(new { success = false, message, data = (object?)null });

    /// <summary>Same precedence as <see cref="SellerGroupMap.Resolve"/>: the id when there is one,
    /// otherwise the folded name.</summary>
    static int FindGroupEntry(List<SellerGroupEntry> entries, string sellerId, string sellerName)
    {
        var id = SellerGroupMap.NormalizeSellerId(sellerId);
        if (id.Length > 0)
        {
            var byId = entries.FindIndex(e => SellerGroupMap.NormalizeSellerId(e.SellerId) == id);
            if (byId >= 0)
                return byId;
        }

        var name = SellerGroupMap.FoldName(sellerName);
        return name.Length == 0 ? -1 : entries.FindIndex(e => SellerGroupMap.FoldName(e.SellerName) == name);
    }
}

