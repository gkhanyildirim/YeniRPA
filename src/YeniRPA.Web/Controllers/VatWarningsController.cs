using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Seller VAT Warnings: splits the "offers with no VAT rate" export into one workbook per seller,
/// finds each seller's address in an uploaded list, and mails every seller their own file.
///
/// <para>The twin of <see cref="OfferWarningsController"/> and structurally identical to it: the app
/// computes the seller → address → file pairing from two uploads, <see cref="VatBatchStore"/> holds it,
/// and <c>send</c> takes both the recipients and the file from there. The posted values are compared to
/// it and a difference is named, never resolved.</para>
///
/// <para>Every entry point validates synchronously and lets <c>ReportExceptionFilter</c> turn a
/// builder's <see cref="InvalidOperationException"/> into <c>400 { error }</c>.</para>
/// </summary>
[ApiController]
[Route("api/vat-warnings")]
[SupportedOSPlatform("windows")]
public sealed class VatWarningsController : ControllerBase
{
    /// <summary>What this module's runs report themselves as on the shared automation bus.</summary>
    public const string ModuleName = "vat-warnings";

    const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    readonly VatMailStore _store;
    readonly VatBatchStore _batches;
    readonly OutlookMailSender _sender;
    readonly OfferMailRunner _runner;
    readonly AutomationJobBus _bus;

    public VatWarningsController(
        VatMailStore store,
        VatBatchStore batches,
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

    public sealed record SettingsRequest(
        [property: JsonPropertyName("subjectTemplate")] string? SubjectTemplate,
        [property: JsonPropertyName("bodyTemplate")] string? BodyTemplate,
        [property: JsonPropertyName("outputFolder")] string? OutputFolder,
        [property: JsonPropertyName("minOfferCount")] int? MinOfferCount,
        [property: JsonPropertyName("ccAddresses")] string? CcAddresses,
        [property: JsonPropertyName("includeSignature")] bool? IncludeSignature,
        [property: JsonPropertyName("overrides")] IReadOnlyList<VatOverrideEntry>? Overrides);

    public sealed record MailsExcelRequest(
        [property: JsonPropertyName("mails")] IReadOnlyList<VatSellerMail>? Mails,
        [property: JsonPropertyName("cc")] string? Cc);

    /// <summary>
    /// One approved mail coming back from the browser, keyed by the seller key the prepare handed out.
    /// Recipients and the attachment name travel only so a mismatch against the held batch can be
    /// caught and named — neither is used as given.
    /// </summary>
    public sealed record SendMail(
        [property: JsonPropertyName("sellerKey")] string? SellerKey,
        [property: JsonPropertyName("sellerName")] string? SellerName,
        [property: JsonPropertyName("recipients")] IReadOnlyList<string>? Recipients,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("attachmentName")] string? AttachmentName);

    public sealed record SendRequest(
        [property: JsonPropertyName("batchId")] string? BatchId,
        [property: JsonPropertyName("mails")] IReadOnlyList<SendMail>? Mails,
        [property: JsonPropertyName("dryRun")] bool DryRun);

    // -----------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------

    /// <summary>
    /// Deliberately does <b>not</b> probe Outlook, for the reason
    /// <see cref="OfferWarningsController.Status"/> gives: resolving the application object starts the
    /// client. <c>check-outlook</c> is the explicit version.
    /// </summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var file = _store.Load();
        var batch = _batches.Current;

        return Ok(new
        {
            outlookAvailable = _sender.LastKnownAvailable,
            outlookError = _sender.LastError,
            isRunning = _bus.IsRunning,
            runningModule = _bus.RunningModule,
            outputFolder = _store.ResolveOutputFolder(file),
            batchId = batch?.BatchId,
            batchFolder = batch?.OutputFolder,
            batchSellers = batch?.BySellerKey.Count ?? 0,
            maxMailsPerRun = OfferMailRunner.MaxMailsPerRun
        });
    }

    /// <summary>Resolves Outlook, starting it if it is not running. Blocks until it answers.</summary>
    [HttpPost("check-outlook")]
    public async Task<IActionResult> CheckOutlook() => Ok(new
    {
        available = await _sender.ProbeAsync(),
        error = _sender.LastError
    });

    // -----------------------------------------------------------------
    // Settings
    // -----------------------------------------------------------------

    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        var file = _store.Load();

        return Ok(new
        {
            subjectTemplate = file.SubjectTemplate ?? VatMailBuilder.DefaultSubjectTemplate,
            bodyTemplate = file.BodyTemplate ?? VatMailBuilder.DefaultBodyTemplate,
            defaultSubjectTemplate = VatMailBuilder.DefaultSubjectTemplate,
            defaultBodyTemplate = VatMailBuilder.DefaultBodyTemplate,
            placeholders = VatMailBuilder.Placeholders,
            outputFolder = _store.ResolveOutputFolder(file),
            defaultOutputFolder = _store.DefaultOutputFolder,
            minOfferCount = file.MinOfferCount ?? 0,
            ccAddresses = file.CcAddresses ?? "",
            includeSignature = file.IncludeSignature ?? false,
            defaultSheetName = SellerMailDirectory.DefaultSheetName,
            overrides = file.Overrides,
            path = _store.FilePath,
            updatedUtc = file.UpdatedUtc,
            warnings = VatMailStore.FindOverrideProblems(file.Overrides)
        });
    }

    [HttpPut("settings")]
    public IActionResult SaveSettings([FromBody] SettingsRequest? request)
    {
        var overrides = Clean(request?.Overrides);

        // Refused rather than saved and quietly ignored later: this is the moment the operator typed the
        // address, and it is the only moment they are looking at it.
        var (cc, ccProblem) = VatMailStore.NormalizeCc(request?.CcAddresses);
        if (ccProblem is not null)
            return BadRequest(new { error = $"The CC address was not saved: {ccProblem}" });

        _store.Save(new VatMailFile(
            Version: 0,                       // stamped by the store
            UpdatedUtc: null,                 // stamped by the store
            SubjectTemplate: NullIfBlank(request?.SubjectTemplate),
            BodyTemplate: NullIfBlank(request?.BodyTemplate),
            OutputFolder: NullIfBlank(request?.OutputFolder),
            MinOfferCount: VatMailStore.NormalizeMinimum(request?.MinOfferCount),
            CcAddresses: cc,
            IncludeSignature: request?.IncludeSignature ?? false,
            Overrides: overrides));

        // The cleaned list comes back so the table shows what is actually stored: Clean collapses rows
        // that describe one seller, and a panel still rendering the pre-collapse list would post the
        // duplicates straight back on the next save.
        return Ok(new
        {
            saved = overrides.Count,
            overrides,
            minOfferCount = VatMailStore.NormalizeMinimum(request?.MinOfferCount) ?? 0,
            ccAddresses = cc ?? "",
            includeSignature = request?.IncludeSignature ?? false,
            path = _store.FilePath,
            warnings = VatMailStore.FindOverrideProblems(overrides)
        });
    }

    // -----------------------------------------------------------------
    // Prepare
    // -----------------------------------------------------------------

    /// <summary>
    /// Reads both uploads, writes one workbook per seller and renders every mail.
    ///
    /// <para>Sellers who cannot be mailed come back with a <c>problem</c> rather than being dropped —
    /// a seller who silently never got warned is the failure this module exists to prevent. Their
    /// workbook is still written, so the operator can send it by hand if they want to.</para>
    /// </summary>
    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare(
        IFormFile? offers,
        IFormFile? directory,
        [FromForm] string? sheetName,
        [FromForm] string? subjectTemplate,
        [FromForm] string? bodyTemplate,
        [FromForm] int? minOfferCount,
        CancellationToken cancellationToken)
    {
        if (offers is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the offer export (.xlsx or .csv)." });

        if (directory is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the seller address list (.xlsx or .csv)." });

        var settings = _store.Load();
        var subject = NullIfBlank(subjectTemplate) ?? settings.SubjectTemplate;
        var body = NullIfBlank(bodyTemplate) ?? settings.BodyTemplate;

        // What the panel sent wins over what is saved, so the operator can try a threshold for one run
        // without committing to it. 0 — and anything below it — means every seller is worth a mail.
        var minimum = Math.Max(0, minOfferCount ?? settings.MinOfferCount ?? 0);

        // Fixed into the batch here and read back at send time, like the recipients and the attachment.
        // Editing the settings box after this point changes nothing until the mails are built again, so
        // the address on the cards is the address that goes out.
        //
        // A malformed CC stops the build. Save refuses one already, so this only fires on a hand-edited
        // settings file — and building 130 mails whose copy silently goes nowhere is worse than saying so.
        var (cc, ccProblem) = VatMailStore.NormalizeCc(settings.CcAddresses);
        if (ccProblem is not null)
            throw new InvalidOperationException($"The saved CC address cannot be used: {ccProblem}");

        // Fixed into the batch alongside the CC, for the same reason.
        var includeSignature = settings.IncludeSignature ?? false;

        VatSplitBuilder.SplitResult split;
        using (var stream = await CopyToSeekableStreamAsync(offers, cancellationToken))
            split = VatSplitBuilder.Read(stream, offers.FileName);

        SellerMailDirectory addresses;
        using (var stream = await CopyToSeekableStreamAsync(directory, cancellationToken))
        {
            addresses = SellerMailDirectory.Read(
                stream,
                directory.FileName,
                // Blank means "this file is a purpose-built single-sheet list"; the onboarding workbook
                // needs its sheet named because its first sheet holds no addresses at all.
                NullIfBlank(sheetName) ?? SellerMailDirectory.DefaultSheetName);
        }

        if (split.Sellers.Count == 0)
        {
            // The filter emptying the file is a different situation from an export with no sellers in
            // it, and saying "names no sellers" about a file full of sellers sends the operator
            // looking for a fault that is not there.
            throw new InvalidOperationException(split.OffersFilteredOut > 0
                ? $"No row in the export carries '{VatSplitBuilder.VatRateMissing}' on its own — " +
                  $"all {split.OffersFilteredOut:N0} row(s) also carry another state reason, so there is nothing to send."
                : "The offer export names no sellers, so there is nothing to split.");
        }

        var date = DateTime.Now.ToString("yyyy-MM-dd");

        // A folder per run. Last month's files can then never be picked up by this month's send, and
        // the operator can compare two runs without one having overwritten the other.
        var folder = Path.Combine(
            _store.ResolveOutputFolder(settings),
            DateTime.Now.ToString("yyyy-MM-dd-HHmm"));

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The output folder '{folder}' could not be created: {ex.Message}", ex);
        }

        var clashes = VatSplitBuilder.FindFileNameClashes(split.Sellers);

        var mails = new List<VatSellerMail>(split.Sellers.Count);
        var unmatched = new List<VatUnmatchedSeller>();
        var batchMails = new List<VatBatchMail>();

        int ready = 0, belowMinimum = 0, noEmail = 0, invalidEmail = 0, ambiguousEmail = 0,
            fileNameClash = 0, writeFailed = 0;

        foreach (var seller in split.Sellers)
        {
            var fileName = VatSplitBuilder.FileNameFor(seller);

            string? problem = null;
            var matchedBy = "";
            IReadOnlyList<string> recipients = [];
            long size = 0;
            var underMinimum = false;

            if (clashes.Contains(seller.SellerKey))
            {
                // Neither of the two is written and neither is sent: the second write would overwrite
                // the first, and then one of these sellers receives the other's list.
                //
                // Checked before the minimum on purpose: a name collision is a fault in the export and
                // is worth naming even for a seller the threshold would have dropped anyway.
                problem = $"'{fileName}' is also another seller's file name. Neither seller is mailed — " +
                          "give one of them a distinct name in the export.";
                fileNameClash++;
            }
            else if (seller.Offers.Count < minimum)
            {
                // Below the threshold nothing is written at all — an unsent workbook full of a seller's
                // products is a file that exists for no reason.
                problem = $"{seller.Offers.Count:N0} product(s) — under the minimum of {minimum:N0}. Not mailed.";
                underMinimum = true;
                belowMinimum++;
            }
            else if (!TryWrite(folder, fileName, seller, date, out size, out var writeError))
            {
                problem = writeError;
                writeFailed++;
            }
            else
            {
                // The hand-entered address wins: it is the operator's answer to a seller the uploaded
                // list does not cover, and it must not be overruled by whatever the list says next month.
                var overrideEmail = VatMailStore.FindOverride(settings.Overrides, seller.SellerId, seller.SellerName);
                string? email = null;

                if (overrideEmail is not null)
                {
                    email = overrideEmail;
                    matchedBy = "override";
                }
                else
                {
                    var match = addresses.Find(seller.SellerId, seller.SellerName);
                    if (match.Email is not null)
                    {
                        email = match.Email;
                        matchedBy = "directory";
                    }
                    else
                    {
                        problem = match.Problem;

                        // "Two different addresses" is a different failure from "not in the list": the
                        // first is a row to correct in the file, the second is an address to enter here.
                        if (match.Problem is not null && match.Problem.Contains("two different", StringComparison.OrdinalIgnoreCase))
                            ambiguousEmail++;
                        else
                            noEmail++;
                    }
                }

                if (email is not null)
                {
                    recipients = SellerMailStore.SplitAddresses(email);
                    var bad = recipients.FirstOrDefault(a => !SellerMailStore.LooksLikeEmail(a));

                    if (recipients.Count == 0)
                    {
                        problem = "No e-mail address is entered for this seller.";
                        noEmail++;
                    }
                    else if (bad is not null)
                    {
                        // Names the offending address rather than the whole cell: a seller with four
                        // users has a cell too long to eyeball.
                        problem = $"'{bad}' does not look like an e-mail address.";
                        invalidEmail++;
                    }
                    else
                    {
                        ready++;
                    }
                }
            }

            var mail = VatMailBuilder.Render(
                seller, recipients, fileName, size, date, subject, body, matchedBy, problem);

            mails.Add(mail);

            if (problem is null)
            {
                batchMails.Add(new VatBatchMail(
                    SellerKey: seller.SellerKey,
                    SellerId: seller.SellerId,
                    SellerName: seller.SellerName,
                    Recipients: recipients,
                    AttachmentPath: Path.Combine(folder, fileName),
                    AttachmentName: fileName));
            }
            else if (matchedBy.Length == 0 && !clashes.Contains(seller.SellerKey) && !underMinimum)
            {
                // The editable list: sellers whose file is ready and waiting on nothing but an address.
                // A seller under the threshold is not waiting on anything, so entering an address for
                // them would answer a question nobody asked.
                unmatched.Add(new VatUnmatchedSeller(
                    SellerId: seller.SellerId,
                    SellerName: seller.SellerName,
                    SellerKey: seller.SellerKey,
                    OfferCount: seller.Offers.Count,
                    Reason: problem));
            }
        }

        var batch = _batches.Put(folder, cc, includeSignature, batchMails);

        var warnings = new List<string>(split.Warnings);
        warnings.AddRange(addresses.Warnings);
        warnings.AddRange(VatMailStore.FindOverrideProblems(settings.Overrides));
        warnings.AddRange(mails
            .SelectMany(m => m.UnknownPlaceholders)
            .Distinct(StringComparer.Ordinal)
            .Select(token => $"'{token}' is not a placeholder and was left in the text as-is."));

        return Ok(new VatPrepareData(
            BatchId: batch.BatchId,
            Date: date,
            OutputFolder: folder,
            Cc: cc,
            IncludeSignature: includeSignature,
            OffersInFile: split.OffersInFile,
            OffersFilteredOut: split.OffersFilteredOut,
            DirectoryRows: addresses.RowCount,
            Mails: mails,
            Unmatched: unmatched,
            Funnel: new VatFunnel(
                SellersInFile: split.Sellers.Count,
                Ready: ready,
                BelowMinimum: belowMinimum,
                NoEmail: noEmail,
                InvalidEmail: invalidEmail,
                AmbiguousEmail: ambiguousEmail,
                FileNameClash: fileNameClash,
                WriteFailed: writeFailed),
            Warnings: warnings));
    }

    /// <summary>
    /// A dedicated export rather than the generic <c>/api/export/xlsx</c>: that path reads cells back
    /// out of rendered HTML and collapses whitespace runs, which would flatten a multi-line body into
    /// one long line.
    /// </summary>
    [HttpPost("mails/excel")]
    public IActionResult MailsExcel([FromBody] MailsExcelRequest? request)
    {
        var mails = request?.Mails ?? [];
        if (mails.Count == 0)
            return BadRequest(new { error = "There is nothing to export." });

        return File(
            BuildMailsWorkbook(mails, request?.Cc),
            XlsxContentType,
            $"vat-warnings-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    // -----------------------------------------------------------------
    // Send
    // -----------------------------------------------------------------

    /// <summary>
    /// Runs the approved mails.
    ///
    /// <para>The subject and body come back from the browser rather than being re-rendered here, so the
    /// text the operator read is the text a seller receives — re-rendering server-side would create two
    /// rendering paths that could disagree, and the one place they would disagree is between what was
    /// approved and what was sent.</para>
    ///
    /// <para>The <b>recipients and the attachment do not</b>. Each mail names a seller key; that key is
    /// resolved in the batch this server prepared, and both the To line and the file come from there.
    /// What the browser sent is compared to it and a difference is refused by name — it never wins.</para>
    /// </summary>
    [HttpPost("send")]
    public IActionResult Send([FromBody] SendRequest? request)
    {
        var raw = request?.Mails ?? [];
        if (raw.Count == 0)
            return BadRequest(new { error = "There is nothing to send." });

        var batch = _batches.Get(request?.BatchId);
        if (batch is null)
        {
            return BadRequest(new
            {
                error = "This batch is no longer the prepared one — the files were rebuilt, or the app " +
                        "restarted. Build the mails again and read the list before sending."
            });
        }

        if (raw.Count > OfferMailRunner.MaxMailsPerRun)
        {
            return BadRequest(new
            {
                error = $"{raw.Count} mails is over the {OfferMailRunner.MaxMailsPerRun}-mail limit for one run. " +
                        "Narrow the list and run it in batches — sending the first " +
                        $"{OfferMailRunner.MaxMailsPerRun} silently would leave you believing all of them went out."
            });
        }

        var mails = new List<OutgoingMail>(raw.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mail in raw)
        {
            var label = Describe(mail);
            var key = (mail.SellerKey ?? "").Trim();

            // The allow-list that matters: a mail can only be built for a seller this server put in the
            // batch, and only from that entry's own data.
            if (key.Length == 0 || !batch.BySellerKey.TryGetValue(key, out var entry))
            {
                return BadRequest(new
                {
                    error = $"{label} is not in the prepared batch. Build the mails again — this app only " +
                            "mails sellers it resolved itself."
                });
            }

            if (!seen.Add(key))
                return BadRequest(new { error = $"{label} appears twice in this run. Each seller is mailed once." });

            // Recipients come from the batch, exactly like the attachment. The posted list is compared
            // to it so a difference is named rather than silently resolved one way or the other.
            var claimed = (mail.Recipients ?? []).Select(SellerMailStore.NormalizeEmail).ToList();
            var held = entry.Recipients.Select(SellerMailStore.NormalizeEmail).ToList();

            if (!claimed.SequenceEqual(held, StringComparer.Ordinal))
            {
                return BadRequest(new
                {
                    error = $"The recipients for {label} were approved as '{string.Join("; ", mail.Recipients ?? [])}' " +
                            $"but the prepared batch says '{SellerMailStore.JoinAddresses(entry.Recipients)}'. " +
                            "Build the mails again and read the list before sending."
                });
            }

            var bad = entry.Recipients.FirstOrDefault(a => !SellerMailStore.LooksLikeEmail(a));
            if (bad is not null)
                return BadRequest(new { error = $"'{bad}' on {label} does not look like an e-mail address." });

            // The posted attachment name is checked against the batch rather than used.
            var expected = entry.AttachmentName.Trim();
            var claimedName = (mail.AttachmentName ?? "").Trim();

            if (!string.Equals(expected, claimedName, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    error = $"The attachment for {label} was approved as '{claimedName}' but the prepared " +
                            $"batch says '{expected}'. Build the mails again and read the list before sending."
                });
            }

            // Re-derived inside the batch folder rather than taken from the entry's stored path: this is
            // the same containment rule Offer Warnings applies, and it is what stops any name from
            // reaching a file outside the folder this run wrote.
            var match = OfferMailBuilder.ResolveAttachment(batch.OutputFolder, expected);
            if (match.Problem is not null)
                return BadRequest(new { error = $"The attachment for {label} cannot be used: {match.Problem}" });

            if (!System.IO.File.Exists(match.Path))
                return BadRequest(new { error = $"The attachment for {label} is not in the folder: {match.Path}" });

            var subject = (mail.Subject ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            var body = (mail.Body ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            if (subject.Length == 0)
                return BadRequest(new { error = $"The mail for {label} has no subject." });

            if (body.Length == 0)
                return BadRequest(new { error = $"The mail for {label} is empty." });

            mails.Add(new OutgoingMail(
                To: SellerMailStore.JoinAddresses(entry.Recipients),
                SellerId: entry.SellerId,
                SellerName: entry.SellerName,
                Subject: subject,
                Body: body,
                AttachmentPath: match.Path,
                AttachmentName: expected,
                // From the batch, never from the request — the same rule the recipients and the
                // attachment follow. Nothing the browser posts can add a reader to these mails.
                Cc: batch.Cc,
                IncludeSignature: batch.IncludeSignature));
        }

        if (!_runner.TryStart(mails, request!.DryRun, ModuleName))
            return BadRequest(new { error = "An automation run is already in progress. Wait for it to finish." });

        return Ok(new { count = mails.Count, dryRun = request.DryRun });
    }

    // -----------------------------------------------------------------

    /// <summary>
    /// Writes one seller's workbook. Returns false with a message rather than throwing: one seller
    /// whose file could not be written must not stop the other 130 from being prepared.
    /// </summary>
    static bool TryWrite(
        string folder, string fileName, VatSellerGroup seller, string date, out long size, out string? error)
    {
        size = 0;
        error = null;

        // The same containment rule the send path applies, run here too — the file name is derived
        // from a seller name that came out of an uploaded spreadsheet.
        var match = OfferMailBuilder.ResolveAttachment(folder, fileName);
        if (match.Problem is not null)
        {
            error = $"The file for this seller could not be written: {match.Problem}";
            return false;
        }

        try
        {
            System.IO.File.WriteAllBytes(match.Path, VatSellerWorkbook.Build(seller, date));
            size = new FileInfo(match.Path).Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"'{fileName}' could not be written: {ex.Message}";
            return false;
        }
    }

    /// <summary>How a seller is named in an error message.</summary>
    static string Describe(SendMail mail)
    {
        var name = (mail.SellerName ?? "").Trim();
        if (name.Length > 0)
            return $"'{name}'";

        var key = (mail.SellerKey ?? "").Trim();
        return key.Length > 0 ? $"seller {key}" : "one of the mails";
    }

    /// <summary>
    /// Trims every field, drops rows with nothing identifying a seller, and collapses rows that
    /// describe the same seller.
    ///
    /// <para>The collapsing is what lets the browser stay out of the key rule. The unmatched-seller
    /// card appends whatever was typed and posts the whole list; deduplicating there would mean
    /// recomputing <see cref="VatSplitBuilder.SellerKey"/> in JavaScript, and a browser-side fold that
    /// disagreed with this one by a single character would append a second row for a seller instead of
    /// replacing the first — leaving a stale address in front of the one just entered.</para>
    ///
    /// <para>The later row wins and keeps the earlier one's position: later is the one the operator
    /// just typed, and a stable position keeps the table from reshuffling under them on every save.</para>
    /// </summary>
    static List<VatOverrideEntry> Clean(IReadOnlyList<VatOverrideEntry>? entries)
    {
        if (entries is null)
            return [];

        var cleaned = new List<VatOverrideEntry>();
        var positionOf = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var raw in entries)
        {
            var entry = new VatOverrideEntry(
                SellerGroupMap.NormalizeSellerId(raw.SellerId ?? ""),
                (raw.SellerName ?? "").Trim(),
                // Canonicalised on the way in, so the stored cell always uses one separator and holds
                // no repeats — whatever the operator pasted.
                SellerMailStore.JoinAddresses(SellerMailStore.SplitAddresses(raw.Email)));

            // A row with a seller but no address is kept — that is "seen but not finished", not junk.
            if (entry.SellerId.Length == 0 && entry.SellerName.Length == 0)
                continue;

            var key = VatSplitBuilder.SellerKey(entry.SellerId, entry.SellerName);

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

    static byte[] BuildMailsWorkbook(IReadOnlyList<VatSellerMail> mails, string? cc)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Mails");

        // CC is one value for the whole run, but it is repeated on every row: this sheet is the record
        // of what was sent, and a reader filtering it down to one seller still has to see who was copied.
        string[] headers =
            ["Seller ID", "Seller", "E-mail", "CC", "Attachment", "Products", "Address from", "Problem", "Subject", "Body"];

        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).Value = headers[c];
        sheet.Row(1).Style.Font.Bold = true;

        for (var i = 0; i < mails.Count; i++)
        {
            var mail = mails[i];
            var row = i + 2;

            sheet.Cell(row, 1).SetValue(mail.SellerId);
            sheet.Cell(row, 2).SetValue(mail.SellerName);
            sheet.Cell(row, 3).SetValue(mail.Email);
            sheet.Cell(row, 4).SetValue(mail.Problem is null ? cc ?? "" : "");
            sheet.Cell(row, 5).SetValue(mail.AttachmentName);
            sheet.Cell(row, 6).SetValue(mail.OfferCount);
            sheet.Cell(row, 7).SetValue(mail.MatchedBy);
            sheet.Cell(row, 8).SetValue(mail.Problem ?? "");
            sheet.Cell(row, 9).SetValue(mail.Subject);
            sheet.Cell(row, 10).SetValue(mail.Body);
        }

        // Ids and the body are text: an id loses its leading zeros as a number, and the body has to
        // keep the line breaks that make it a message rather than a paragraph.
        sheet.Column(1).Style.NumberFormat.Format = "@";
        sheet.Column(10).Style.NumberFormat.Format = "@";
        sheet.Column(10).Style.Alignment.WrapText = true;
        sheet.Column(10).Width = 80;
        sheet.Columns(1, 9).AdjustToContents();

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
