using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Custom Mail: a free-form subject and body the operator writes themselves, with one shared
/// attachment, sent to whichever sellers they tick out of an uploaded seller list.
///
/// <para>The seller list names <em>who</em> to reach; their addresses are looked up in a second,
/// separate directory upload via <see cref="SellerMailDirectory"/> — exactly the "offers export +
/// address directory" shape <see cref="Controllers.OfferWarningsController"/> uses, minus the
/// per-seller attachment split, since every recipient here gets the identical file. The seller list's
/// own e-mail column, if it has one, is deliberately never read: the operator wants every address to
/// come from the directory, which is treated as the more current/authoritative source.</para>
///
/// <para>Reuses <see cref="OutlookMailSender"/> and <see cref="OfferMailRunner"/> exactly as Seller
/// Offer/VAT Warnings do — this module only differs in what it hands the runner, not in how the runner
/// sends or paces it. <see cref="CustomMailBatchStore"/> holds the resolved recipient list the way
/// <see cref="OfferBatchStore"/> holds the seller pairing, so <c>send</c> reads addresses back from
/// there instead of trusting whatever the browser echoes.</para>
///
/// <para><b>Every JSON endpoint here returns <c>{ success, message, data }</c>.</b> This is a new
/// endpoint set, so CLAUDE.md's envelope rule applies — including failures, which is why
/// <see cref="CustomMailSellerListReader"/>'s and <see cref="SellerMailDirectory"/>'s
/// <see cref="InvalidOperationException"/>s are caught locally rather than left to
/// <c>ReportExceptionFilter</c>. Same call as <see cref="Controllers.StockoutWarningsController"/>.</para>
/// </summary>
[ApiController]
[Route("api/custom-mail")]
[SupportedOSPlatform("windows")]
public sealed class CustomMailController : ControllerBase
{
    public const string ModuleName = "custom-mail";

    readonly ICustomMailStore _store;
    readonly CustomMailBatchStore _batches;
    readonly OutlookMailSender _sender;
    readonly OfferMailRunner _runner;
    readonly AutomationJobBus _bus;

    public CustomMailController(
        ICustomMailStore store,
        CustomMailBatchStore batches,
        OutlookMailSender sender,
        OfferMailRunner runner,
        AutomationJobBus bus)
    {
        _store = store;
        _batches = batches;
        _sender = sender;
        _runner = runner;
        _bus = bus;
    }

    // -----------------------------------------------------------------
    // Request shapes
    // -----------------------------------------------------------------

    public sealed record SaveOverridesRequest(
        [property: JsonPropertyName("overrides")] IReadOnlyList<CustomMailOverrideEntry>? Overrides);

    // -----------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------

    /// <summary>Deliberately does <b>not</b> probe Outlook — see
    /// <see cref="Controllers.OfferWarningsController.Status"/> for why. <c>check-outlook</c> is the
    /// explicit version.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Success("", new
    {
        outlookAvailable = _sender.LastKnownAvailable,
        outlookError = _sender.LastError,
        isRunning = _bus.IsRunning,
        runningModule = _bus.RunningModule,
        stopRequested = _bus.StopRequested,
        maxMailsPerRun = OfferMailRunner.MaxMailsPerRun,
        mailsPerPass = OfferMailRunner.MailsPerPass
    });

    [HttpPost("check-outlook")]
    public async Task<IActionResult> CheckOutlook() =>
        Success("", new { available = await _sender.ProbeAsync(), error = _sender.LastError });

    // -----------------------------------------------------------------
    // Hand-entered addresses
    // -----------------------------------------------------------------

    [HttpGet("overrides")]
    public IActionResult GetOverrides()
    {
        var file = _store.Load();

        return Success("", new
        {
            overrides = file.Overrides,
            path = _store.FilePath,
            updatedUtc = file.UpdatedUtc,
            warnings = CustomMailStore.FindOverrideProblems(file.Overrides)
        });
    }

    [HttpPut("overrides")]
    public IActionResult SaveOverrides([FromBody] SaveOverridesRequest? request)
    {
        var overrides = Clean(request?.Overrides);
        _store.Save(new CustomMailFile(null, overrides));

        return Success($"{overrides.Count:N0} address(es) saved.", new
        {
            saved = overrides.Count,
            overrides,
            path = _store.FilePath,
            warnings = CustomMailStore.FindOverrideProblems(overrides)
        });
    }

    // -----------------------------------------------------------------
    // Prepare
    // -----------------------------------------------------------------

    /// <summary>
    /// Reads the seller list and looks every seller up in the address directory.
    ///
    /// <para>A seller who cannot be resolved to an address stays out of the recipient list rather
    /// than being silently dropped — see <see cref="CustomMailUnmatchedSeller"/> — the same rule
    /// <see cref="Controllers.OfferWarningsController.Prepare"/> applies to sellers its own directory
    /// does not cover.</para>
    /// </summary>
    [HttpPost("prepare")]
    public IActionResult Prepare(IFormFile? sellers, IFormFile? directory)
    {
        if (sellers is not { Length: > 0 })
            return Failure("Please upload the seller list (.xlsx or .csv).");

        if (directory is not { Length: > 0 })
            return Failure("Please upload the seller address directory (.xlsx or .csv).");

        IReadOnlyList<CustomMailSellerListReader.SellerRow> sellerRows;
        SellerMailDirectory addresses;
        try
        {
            using (var stream = sellers.OpenReadStream())
                sellerRows = CustomMailSellerListReader.Read(stream, sellers.FileName);

            using (var stream = directory.OpenReadStream())
            {
                addresses = SellerMailDirectory.Read(
                    stream,
                    directory.FileName,
                    // Only a hint: SellerMailDirectory finds the sheet that actually has the address
                    // columns regardless of what its tab is named.
                    SellerMailDirectory.DefaultSheetName);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }

        if (sellerRows.Count == 0)
            return Failure("The seller list has no seller to look up.");

        var overrides = _store.Load().Overrides;

        // First seller to resolve to a given address wins the display name; a second seller sharing
        // that address (an agency running several storefronts) is folded into the same recipient
        // rather than mailed twice.
        var byKey = new Dictionary<string, CustomMailRecipientDto>(StringComparer.Ordinal);
        var order = new List<string>();
        var unmatched = new List<CustomMailUnmatchedSeller>();

        foreach (var seller in sellerRows)
        {
            // The hand-entered address wins: it is the operator's answer to a seller the uploaded
            // directory does not cover, and it must not be overruled by whatever the directory says
            // next time it is re-exported.
            var overrideEmail = CustomMailStore.FindOverride(overrides, seller.SellerId, seller.SellerName);
            string? email;
            string matchedBy;

            if (overrideEmail is not null)
            {
                email = overrideEmail;
                matchedBy = "override";
            }
            else
            {
                var match = addresses.Find(seller.SellerId, seller.SellerName);
                email = match.Email;
                matchedBy = "directory";

                if (email is null)
                {
                    unmatched.Add(new CustomMailUnmatchedSeller(
                        seller.SellerId, seller.SellerName, match.Problem ?? "Not in the directory.",
                        CustomMailSellerListReader.SellerKey(seller.SellerId, seller.SellerName)));
                    continue;
                }
            }

            // One mail per seller, not one per person in their back office: a cell listing several
            // users picks the first one rather than mailing all of them separately. The first address
            // is who the seller (or the directory) put first on the row, the closest thing to "the
            // main contact" this data offers.
            var address = SellerMailStore.SplitAddresses(email).FirstOrDefault(SellerMailStore.LooksLikeEmail);
            if (address is null)
            {
                unmatched.Add(new CustomMailUnmatchedSeller(
                    seller.SellerId, seller.SellerName, "No usable e-mail address for this seller.",
                    CustomMailSellerListReader.SellerKey(seller.SellerId, seller.SellerName)));
                continue;
            }

            var key = SellerMailStore.NormalizeEmail(address);
            if (byKey.ContainsKey(key))
                continue;

            byKey[key] = new CustomMailRecipientDto(key, seller.SellerId, seller.SellerName, address, matchedBy);
            order.Add(key);
        }

        var recipientList = order.Select(key => byKey[key]).ToList();
        if (recipientList.Count == 0)
            return Failure("No seller in the list could be resolved to an address in the directory.");

        var batch = _batches.Put(
            recipientList.Select(r => new CustomMailRecipient(r.SellerName, r.Email)));

        var warnings = new List<string>(addresses.Warnings);
        warnings.AddRange(CustomMailStore.FindOverrideProblems(overrides));

        if (unmatched.Count > 0)
        {
            warnings.Add(
                $"{unmatched.Count:N0} of {sellerRows.Count:N0} seller(s) have no address in the " +
                "directory and are not in the recipient list below.");
        }

        // Said here rather than left for send to refuse — the operator should read this while still
        // looking at the list, not after ticking every row.
        if (recipientList.Count > OfferMailRunner.MaxMailsPerRun)
        {
            warnings.Add(
                $"{recipientList.Count:N0} recipients resolved, over the " +
                $"{OfferMailRunner.MaxMailsPerRun:N0}-mail ceiling for one run. Untick part of the " +
                "list before sending.");
        }
        else if (recipientList.Count > OfferMailRunner.MailsPerPass)
        {
            var passes = OfferMailRunner.PlanPasses(recipientList.Count, OfferMailRunner.MailsPerPass).Count;
            warnings.Add(
                $"{recipientList.Count:N0} recipients resolved. Sending to all of them goes out in " +
                $"{passes} passes of {OfferMailRunner.MailsPerPass}, with a break between them.");
        }

        return Success($"{recipientList.Count:N0} of {sellerRows.Count:N0} seller(s) resolved to an address.",
            new CustomMailPrepareData(
                BatchId: batch.BatchId,
                Recipients: recipientList,
                SellersInFile: sellerRows.Count,
                Unmatched: unmatched,
                DirectoryRows: addresses.RowCount,
                MaxMailsPerRun: OfferMailRunner.MaxMailsPerRun,
                MailsPerPass: OfferMailRunner.MailsPerPass,
                Warnings: warnings));
    }

    // -----------------------------------------------------------------
    // Send
    // -----------------------------------------------------------------

    /// <summary>
    /// Runs the approved mail. Multipart rather than JSON, because the attachment itself has to travel
    /// with the click — there is nothing per-recipient to pre-stage on the server the way Offer
    /// Warnings pre-stages each seller's workbook, since every recipient here gets the identical file.
    ///
    /// <para>The one thing that still needs the server's own record rather than the browser's word is
    /// <em>who</em> may be mailed: every posted address must already be a key in the batch
    /// <c>prepare</c> built, or the request is refused by name.</para>
    /// </summary>
    [HttpPost("send")]
    public async Task<IActionResult> Send(
        IFormFile? attachment,
        [FromForm] string? batchId,
        [FromForm] string? subject,
        [FromForm] string? body,
        [FromForm] string? cc,
        [FromForm] string? bcc,
        [FromForm] string? recipientEmailsJson,
        [FromForm] bool dryRun,
        [FromForm] bool includeSignature,
        CancellationToken cancellationToken)
    {
        var batch = _batches.Get(batchId);
        if (batch is null)
        {
            return Failure(
                "This batch is no longer the prepared one — the files were rebuilt, or the app " +
                "restarted. Build the recipient list again before sending.");
        }

        List<string>? emails;
        try
        {
            emails = string.IsNullOrWhiteSpace(recipientEmailsJson)
                ? null
                : JsonSerializer.Deserialize<List<string>>(recipientEmailsJson);
        }
        catch (JsonException)
        {
            return Failure("The recipient list could not be read.");
        }

        if (emails is not { Count: > 0 })
            return Failure("There is nothing to send — tick at least one recipient.");

        if (emails.Count > OfferMailRunner.MaxMailsPerRun)
        {
            return Failure(
                $"{emails.Count:N0} recipients is over the {OfferMailRunner.MaxMailsPerRun:N0}-mail " +
                $"ceiling for one run. Untick part of the list — sending the first " +
                $"{OfferMailRunner.MaxMailsPerRun:N0} silently would leave you believing all of them went out.");
        }

        var trimmedSubject = (subject ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (trimmedSubject.Length == 0)
            return Failure("The mail has no subject.");

        // The body box is a rich-text editor, so this is HTML, not a plain-text template — stripping
        // tags before checking for emptiness catches a box that looks empty but still holds the markup
        // an empty contenteditable div leaves behind (e.g. "<div><br></div>").
        var bodyHtml = (body ?? "").Trim();
        if (System.Text.RegularExpressions.Regex.Replace(bodyHtml, "<[^>]*>", "").Trim().Length == 0)
            return Failure("The mail body is empty.");

        // The browser's contenteditable markup carries no font of its own — bold/italic/underline
        // tags, nothing else. Handed to Outlook as-is, that renders in whatever bare default the mail
        // item falls back to, which is a serif font, not the Calibri/Aptos sans-serif every normal
        // Outlook compose window uses. Wrapping in one div with that font is what makes the sent mail
        // look like a mail someone typed in Outlook rather than a raw HTML page.
        var mailBodyHtml =
            $"<div style=\"font-family:Calibri,Aptos,'Segoe UI',Arial,sans-serif;font-size:11pt;\">{bodyHtml}</div>";

        var (cleanCc, ccProblem) = NormalizeAddressField(cc);
        if (ccProblem is not null)
            return Failure($"The CC address was not used: {ccProblem}");

        var (cleanBcc, bccProblem) = NormalizeAddressField(bcc);
        if (bccProblem is not null)
            return Failure($"The BCC address was not used: {bccProblem}");

        if (attachment is not { Length: > 0 })
            return Failure("Please attach a file — every recipient gets the same one.");

        // The allow-list that matters: an address can only be mailed if this server put it in the
        // batch itself, and the name that goes on the run log comes from there too.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var recipients = new List<CustomMailRecipient>(emails.Count);

        foreach (var raw in emails)
        {
            var key = SellerMailStore.NormalizeEmail(raw);
            if (key.Length == 0 || !batch.ByEmailKey.TryGetValue(key, out var entry))
            {
                return Failure(
                    $"'{raw}' is not in the prepared list. Build the recipient list again — this app " +
                    "only mails addresses it resolved itself.");
            }

            if (!seen.Add(key))
                return Failure($"'{entry.Email}' appears twice in this run. Each recipient is mailed once.");

            recipients.Add(entry);
        }

        // A fresh, timestamped folder per send: the attachment has to sit on disk for the whole run —
        // OfferMailRunner re-checks File.Exists before every mail, and a run this size can span several
        // paced passes — so nothing here may reuse or clean up a folder while a run could still need it.
        var runFolder = Path.Combine(_batches.AttachmentRoot, DateTime.Now.ToString("yyyy-MM-dd-HHmm"));

        string attachmentPath;
        var fileName = Path.GetFileName(attachment.FileName);
        try
        {
            Directory.CreateDirectory(runFolder);

            var match = OfferMailBuilder.ResolveAttachment(runFolder, fileName);
            if (match.Problem is not null)
                return Failure($"The attachment cannot be used: {match.Problem}");

            attachmentPath = match.Path;

            await using var fileStream = System.IO.File.Create(attachmentPath);
            await attachment.CopyToAsync(fileStream, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure($"The attachment could not be saved: {ex.Message}");
        }

        var mails = recipients.Select(r => new OutgoingMail(
            To: r.Email,
            SellerId: "",
            SellerName: string.IsNullOrWhiteSpace(r.Name) ? r.Email : r.Name,
            Subject: trimmedSubject,
            Body: mailBodyHtml,
            AttachmentPath: attachmentPath,
            AttachmentName: fileName,
            Cc: cleanCc,
            Bcc: cleanBcc,
            IncludeSignature: includeSignature,
            IsHtmlBody: true)).ToList();

        if (!_runner.TryStart(mails, dryRun, ModuleName))
            return Failure("An automation run is already in progress. Wait for it to finish.");

        return Success(
            dryRun
                ? $"Dry run started for {mails.Count:N0} recipient(s)."
                : $"Sending to {mails.Count:N0} recipient(s).",
            new { count = mails.Count, dryRun });
    }

    // -----------------------------------------------------------------

    /// <summary>
    /// A CC/BCC field, cleaned, or the reason it cannot be used.
    ///
    /// <para>Split, validated and re-joined by the same three <see cref="SellerMailStore"/> helpers
    /// every other address field in this app uses, so <c>a@x.com; b@x.com</c> works here exactly as
    /// it does on a seller's own address cell. Kept as Custom Mail's own local copy rather than a call
    /// into <see cref="OfferMailStore.NormalizeCc"/> — see <see cref="CustomMailSellerListReader.SellerKey"/>'s
    /// doc comment for why sibling mail modules in this app deliberately don't share this kind of logic.</para>
    /// </summary>
    static (string? Value, string? Problem) NormalizeAddressField(string? raw)
    {
        var addresses = SellerMailStore.SplitAddresses(raw);
        if (addresses.Count == 0)
            return (null, null);

        var bad = addresses.FirstOrDefault(a => !SellerMailStore.LooksLikeEmail(a));
        if (bad is not null)
            return (null, $"'{bad}' does not look like an e-mail address.");

        return (SellerMailStore.JoinAddresses(addresses), null);
    }

    /// <summary>
    /// Trims every field, drops rows with nothing identifying a seller, and collapses rows that
    /// describe the same seller — the later row wins and keeps the earlier one's position. Mirrors
    /// <see cref="Controllers.OfferWarningsController.Clean"/> exactly, keyed on
    /// <see cref="CustomMailSellerListReader.SellerKey"/> instead of <c>OfferSplitBuilder.SellerKey</c>.
    /// </summary>
    static List<CustomMailOverrideEntry> Clean(IReadOnlyList<CustomMailOverrideEntry>? entries)
    {
        if (entries is null)
            return [];

        var cleaned = new List<CustomMailOverrideEntry>();
        var positionOf = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var raw in entries)
        {
            var entry = new CustomMailOverrideEntry(
                SellerGroupMap.NormalizeSellerId(raw.SellerId ?? ""),
                (raw.SellerName ?? "").Trim(),
                SellerMailStore.JoinAddresses(SellerMailStore.SplitAddresses(raw.Email)));

            if (entry.SellerId.Length == 0 && entry.SellerName.Length == 0)
                continue;

            var key = CustomMailSellerListReader.SellerKey(entry.SellerId, entry.SellerName);

            if (positionOf.TryGetValue(key, out var index))
                cleaned[index] = entry;
            else
            {
                positionOf[key] = cleaned.Count;
                cleaned.Add(entry);
            }
        }

        return cleaned;
    }

    IActionResult Success(string message, object? data) => Ok(new { success = true, message, data });

    IActionResult Failure(string message) =>
        BadRequest(new { success = false, message, data = (object?)null });
}
