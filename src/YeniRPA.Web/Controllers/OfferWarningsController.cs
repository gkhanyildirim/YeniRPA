using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Seller Offer Warnings: owns the seller → e-mail → attachment mapping, renders one warning mail
/// per seller, and hands the approved batch to Outlook.
///
/// <para><c>prepare</c> resolves the attachments and renders the text from the saved table, so
/// editing the template is a re-render rather than a re-upload — the same split as
/// <c>late-orders/prepare</c> → <c>late-orders/messages</c>, minus the export, because here the
/// mapping table <em>is</em> the input.</para>
///
/// <para>Every entry point validates synchronously and lets <c>ReportExceptionFilter</c> turn a
/// builder's <see cref="InvalidOperationException"/> into <c>400 { error }</c>.</para>
/// </summary>
[ApiController]
[Route("api/offer-warnings")]
[SupportedOSPlatform("windows")]
public sealed class OfferWarningsController : ControllerBase
{
    const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    readonly SellerMailStore _store;
    readonly OutlookMailSender _sender;
    readonly OfferMailRunner _runner;
    readonly AutomationJobBus _bus;
    readonly MiraklSellerUserScraper _scraper;

    public OfferWarningsController(
        SellerMailStore store,
        OutlookMailSender sender,
        OfferMailRunner runner,
        AutomationJobBus bus,
        MiraklSellerUserScraper scraper)
    {
        _store = store;
        _sender = sender;
        _runner = runner;
        _bus = bus;
        _scraper = scraper;
    }

    // -----------------------------------------------------------------
    // Request shapes
    // -----------------------------------------------------------------

    public sealed record MappingRequest(
        [property: JsonPropertyName("entries")] IReadOnlyList<SellerMailEntry>? Entries,
        [property: JsonPropertyName("subjectTemplate")] string? SubjectTemplate,
        [property: JsonPropertyName("bodyTemplate")] string? BodyTemplate,
        [property: JsonPropertyName("attachmentFolder")] string? AttachmentFolder);

    /// <summary>
    /// The table as it stands in the browser, plus whether to leave filled rows alone. The table
    /// travels rather than being read from disk so unsaved edits are not lost by a fetch.
    /// </summary>
    public sealed record FetchEmailsRequest(
        [property: JsonPropertyName("entries")] IReadOnlyList<SellerMailEntry>? Entries,
        [property: JsonPropertyName("onlyMissing")] bool? OnlyMissing);

    public sealed record MappingExcelRequest(
        [property: JsonPropertyName("entries")] IReadOnlyList<SellerMailEntry>? Entries);

    /// <summary>
    /// Template overrides for the preview, so wording can be tried out without saving it first.
    ///
    /// <para>The attachment folder is deliberately <b>not</b> overridable here. It decides which file
    /// ends up attached to which seller's mail, and <c>send</c> re-derives it from the saved table —
    /// letting the preview run against a different folder would put those two out of step in exactly
    /// the place where being out of step means the wrong seller gets the wrong file.</para>
    /// </summary>
    public sealed record PrepareRequest(
        [property: JsonPropertyName("subjectTemplate")] string? SubjectTemplate,
        [property: JsonPropertyName("bodyTemplate")] string? BodyTemplate);

    public sealed record MailsExcelRequest(
        [property: JsonPropertyName("mails")] IReadOnlyList<RenderedMail>? Mails);

    /// <summary>
    /// One approved mail coming back from the browser. It is keyed by <b>seller</b>, not by address:
    /// a seller has several users who all belong on one mail, and the same address can legitimately
    /// belong to several sellers. Recipients and attachment travel only so a mismatch against the
    /// saved table can be caught and named — neither is used as given.
    /// </summary>
    public sealed record SendMail(
        [property: JsonPropertyName("sellerId")] string? SellerId,
        [property: JsonPropertyName("sellerName")] string? SellerName,
        [property: JsonPropertyName("recipients")] IReadOnlyList<string>? Recipients,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("attachmentName")] string? AttachmentName);

    public sealed record SendRequest(
        [property: JsonPropertyName("mails")] IReadOnlyList<SendMail>? Mails,
        [property: JsonPropertyName("dryRun")] bool DryRun);

    // -----------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------

    /// <summary>
    /// Deliberately does <b>not</b> probe Outlook. Resolving the application object starts the client
    /// when it is not running, and a status call that silently launches Outlook every time the panel
    /// is opened is a side effect nobody asked for. <c>check-outlook</c> is the explicit version.
    /// </summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var file = _store.Load();
        var folder = _store.ResolveAttachmentFolder(file);

        return Ok(new
        {
            outlookAvailable = _sender.LastKnownAvailable,
            outlookError = _sender.LastError,
            isRunning = _bus.IsRunning,
            runningModule = _bus.RunningModule,
            attachmentFolder = folder,
            folderExists = Directory.Exists(folder),
            filesInFolder = CountFiles(folder),
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
    // Mapping
    // -----------------------------------------------------------------

    [HttpGet("mapping")]
    public IActionResult GetMapping()
    {
        var file = _store.Load();

        return Ok(new
        {
            entries = file.Entries,
            subjectTemplate = file.SubjectTemplate ?? OfferMailBuilder.DefaultSubjectTemplate,
            bodyTemplate = file.BodyTemplate ?? OfferMailBuilder.DefaultBodyTemplate,
            defaultSubjectTemplate = OfferMailBuilder.DefaultSubjectTemplate,
            defaultBodyTemplate = OfferMailBuilder.DefaultBodyTemplate,
            placeholders = OfferMailBuilder.Placeholders,
            attachmentFolder = _store.ResolveAttachmentFolder(file),
            defaultAttachmentFolder = _store.DefaultAttachmentFolder,
            path = _store.FilePath,
            updatedUtc = file.UpdatedUtc,
            warnings = SellerMailStore.FindTableProblems(file.Entries)
        });
    }

    [HttpPut("mapping")]
    public IActionResult SaveMapping([FromBody] MappingRequest? request)
    {
        var entries = Clean(request?.Entries);

        _store.Save(new SellerMailFile(
            Version: 0,                       // stamped by the store
            UpdatedUtc: null,                 // stamped by the store
            SubjectTemplate: NullIfBlank(request?.SubjectTemplate),
            BodyTemplate: NullIfBlank(request?.BodyTemplate),
            AttachmentFolder: NullIfBlank(request?.AttachmentFolder),
            Entries: entries));

        return Ok(new
        {
            saved = entries.Count,
            path = _store.FilePath,
            warnings = SellerMailStore.FindTableProblems(entries)
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
        var imported = SellerMailStore.ReadWorkbook(stream, file.FileName);

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

            // A blank cell in the import means "not stated", not "clear this". The lead-time counts
            // are the exception: they are a fresh measurement every month, and a 0 there is a real
            // reading rather than a gap.
            var next = new SellerMailEntry(
                SellerId: entry.SellerId.Length > 0 ? entry.SellerId : existing.SellerId,
                SellerName: entry.SellerName.Length > 0 ? entry.SellerName : existing.SellerName,
                Email: entry.Email.Length > 0 ? entry.Email : existing.Email,
                FileName: entry.FileName.Length > 0 ? entry.FileName : existing.FileName,
                LeadTime0: entry.LeadTime0,
                LeadTime1: entry.LeadTime1);

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

    /// <summary>
    /// Fills the address column from the Mirakl back office, one seller page per row.
    ///
    /// <para>Same contract as <c>mapping/import</c>: it returns the merged table for review and
    /// <b>does not save</b>. An endpoint that silently rewrote 190 addresses would only be
    /// recoverable from the backup generation, and the operator would have no way to see what had
    /// changed before it did.</para>
    ///
    /// <para>Synchronous rather than a background run: four pages at a time clears the whole table in
    /// well under a minute, and the result has to land back in the table anyway — the progress bus
    /// carries log lines, not data. It also means a scrape does not hold the automation slot that
    /// Create Return needs.</para>
    /// </summary>
    [HttpPost("mapping/fetch-emails")]
    public IActionResult FetchEmails([FromBody] FetchEmailsRequest? request)
    {
        var entries = Clean(request?.Entries);
        if (entries.Count == 0)
            return BadRequest(new { error = "The mapping table is empty. Import your matching workbook first." });

        if (entries.Count > MiraklSellerUserScraper.MaxSellersPerRun)
        {
            return BadRequest(new
            {
                error = $"{entries.Count} rows is over the {MiraklSellerUserScraper.MaxSellersPerRun}-seller limit " +
                        "for one fetch. Narrow the table and run it in batches."
            });
        }

        if (!_scraper.TryStart(entries, request?.OnlyMissing ?? true))
            return BadRequest(new { error = "An automation run is already in progress. Wait for it to finish." });

        return Ok(new { started = true, rows = entries.Count });
    }

    /// <summary>
    /// The table the last fetch produced. Collected by the panel when the run reports done — the
    /// progress bus streams log lines, and a 190-row table is not a log line.
    /// </summary>
    [HttpGet("mapping/fetch-result")]
    public IActionResult FetchResult()
    {
        var result = _scraper.LastResult;
        if (result is null)
            return Ok(new { available = false });

        return Ok(new
        {
            available = true,
            result.Entries,
            result.Filled,
            result.Unchanged,
            result.NoSellerId,
            result.SkippedDisabled,
            result.Problems,
            result.Error,
            result.CompletedUtc
        });
    }

    [HttpPost("mapping/excel")]
    public IActionResult MappingExcel([FromBody] MappingExcelRequest? request)
    {
        var entries = Clean(request?.Entries);
        if (entries.Count == 0)
            return BadRequest(new { error = "The mapping table is empty." });

        return File(SellerMailStore.BuildWorkbook(entries), XlsxContentType, "satici-mail-eslesme.xlsx");
    }

    // -----------------------------------------------------------------
    // Prepare
    // -----------------------------------------------------------------

    /// <summary>
    /// Renders every row and resolves its attachment. Rows that cannot be sent come back with a
    /// <c>problem</c> rather than being dropped — a seller who silently never got warned is the
    /// failure this module is here to prevent.
    /// </summary>
    [HttpPost("prepare")]
    public IActionResult Prepare([FromBody] PrepareRequest? request)
    {
        var file = _store.Load();
        var folder = _store.ResolveAttachmentFolder(file);
        var entries = file.Entries;

        var subjectTemplate = NullIfBlank(request?.SubjectTemplate) ?? file.SubjectTemplate;
        var bodyTemplate = NullIfBlank(request?.BodyTemplate) ?? file.BodyTemplate;

        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                "The seller/e-mail mapping is empty. Import your matching workbook or add rows above first.");
        }

        var date = DateTime.Now.ToString("yyyy-MM-dd");

        // Every row of a duplicated seller is flagged, not just the second one: marking one of a pair
        // sends the operator hunting for a duplicate that is not visibly a duplicate.
        var repeatedSellers = entries
            .Select(SellerKey)
            .Where(key => key.Length > 0)
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var mails = new List<RenderedMail>(entries.Count);
        int noEmail = 0, invalidEmail = 0, noFileName = 0, fileNotFound = 0, duplicateSeller = 0, ready = 0;

        foreach (var entry in entries)
        {
            string? problem = null;
            var path = "";
            long size = 0;

            var addresses = SellerMailStore.SplitAddresses(entry.Email);
            var badAddress = addresses.FirstOrDefault(a => !SellerMailStore.LooksLikeEmail(a));

            if (addresses.Count == 0)
            {
                problem = "No e-mail address is entered for this seller.";
                noEmail++;
            }
            else if (badAddress is not null)
            {
                // Names the offending address rather than the whole cell: a seller with four users has
                // a cell too long to eyeball, and "one of these is wrong" is not an actionable message.
                problem = $"'{badAddress}' does not look like an e-mail address.";
                invalidEmail++;
            }
            else if (repeatedSellers.Contains(SellerKey(entry)))
            {
                problem = "This seller is on more than one row. Remove one — the run cannot tell which is authoritative.";
                duplicateSeller++;
            }
            else if (entry.FileName.Trim().Length == 0)
            {
                problem = "No attachment file name is entered for this seller.";
                noFileName++;
            }
            else
            {
                var match = OfferMailBuilder.ResolveAttachment(folder, entry.FileName);
                if (match.Problem is not null)
                {
                    problem = match.Problem;
                    fileNotFound++;
                }
                else if (!System.IO.File.Exists(match.Path))
                {
                    problem = $"'{entry.FileName.Trim()}' was not found in the attachment folder.";
                    fileNotFound++;
                }
                else
                {
                    path = match.Path;
                    size = new FileInfo(match.Path).Length;
                    ready++;
                }
            }

            mails.Add(OfferMailBuilder.Render(
                entry, date, subjectTemplate, bodyTemplate, path, size, problem));
        }

        var warnings = new List<string>(SellerMailStore.FindTableProblems(entries));

        if (!Directory.Exists(folder))
            warnings.Add($"The attachment folder '{folder}' does not exist, so no attachment can be found.");

        warnings.AddRange(mails
            .SelectMany(m => m.UnknownPlaceholders)
            .Distinct(StringComparer.Ordinal)
            .Select(token => $"'{token}' is not a placeholder and was left in the text as-is."));

        return Ok(new OfferMailData(
            Date: date,
            AttachmentFolder: folder,
            FilesInFolder: CountFiles(folder),
            Mails: mails,
            Funnel: new OfferMailFunnel(
                EntriesInTable: entries.Count,
                Ready: ready,
                NoEmail: noEmail,
                InvalidEmail: invalidEmail,
                NoFileName: noFileName,
                FileNotFound: fileNotFound,
                DuplicateSeller: duplicateSeller),
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

        return File(BuildMailsWorkbook(mails), XlsxContentType, $"offer-warnings-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    // -----------------------------------------------------------------
    // Send
    // -----------------------------------------------------------------

    /// <summary>
    /// Runs the approved mails.
    ///
    /// <para>The subject and body come back from the browser rather than being re-rendered here, so
    /// the text the operator read is the text a seller receives — re-rendering server-side would
    /// create two rendering paths that could disagree, and the one place they would disagree is
    /// between what was approved and what was sent.</para>
    ///
    /// <para>The <b>recipients and the attachment do not</b>. Each mail names a seller; that seller is
    /// resolved to exactly one row of the saved table, and both the To line and the file come from that
    /// row. What the browser sent is compared to it and a difference is refused by name — it never
    /// wins. Trusting a client-supplied address or path is how an automation ends up mailing one
    /// seller's price list to another.</para>
    ///
    /// <para>Keyed by seller rather than by address because neither side is unique on its own any
    /// more: a seller has several users on one mail, and one agency address can be a recipient for
    /// several sellers. Only the seller identifies a mail.</para>
    /// </summary>
    [HttpPost("send")]
    public IActionResult Send([FromBody] SendRequest? request)
    {
        var raw = request?.Mails ?? [];
        if (raw.Count == 0)
            return BadRequest(new { error = "There is nothing to send." });

        if (raw.Count > OfferMailRunner.MaxMailsPerRun)
        {
            return BadRequest(new
            {
                error = $"{raw.Count} mails is over the {OfferMailRunner.MaxMailsPerRun}-mail limit for one run. " +
                        "Narrow the list and run it in batches — sending the first " +
                        $"{OfferMailRunner.MaxMailsPerRun} silently would leave you believing all of them went out."
            });
        }

        var file = _store.Load();
        var folder = _store.ResolveAttachmentFolder(file);

        var mails = new List<OutgoingMail>(raw.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mail in raw)
        {
            var label = Describe(mail);

            // The allow-list that matters: a mail can only be built for a seller the operator entered
            // in the mapping table by hand, and only from that row's own data.
            var matches = FindMatches(file.Entries, mail.SellerId, mail.SellerName);

            if (matches.Count == 0)
            {
                return BadRequest(new
                {
                    error = $"{label} is not in the seller/e-mail mapping. Add the seller there first — this app " +
                            "only mails sellers you have entered by hand."
                });
            }

            if (matches.Count > 1)
            {
                return BadRequest(new
                {
                    error = $"{label} matches {matches.Count} rows in the mapping. Remove the duplicate — nothing " +
                            "is guessed at when two rows could be the intended one."
                });
            }

            var entry = matches[0];

            if (!seen.Add(SellerKey(entry)))
                return BadRequest(new { error = $"{label} appears twice in this run. Each seller is mailed once." });

            // Recipients come from the table, exactly like the attachment. The posted list is compared
            // to it so a difference is named rather than silently resolved one way or the other.
            var recipients = SellerMailStore.SplitAddresses(entry.Email);

            if (recipients.Count == 0)
                return BadRequest(new { error = $"{label} has no recipient in the mapping." });

            var badAddress = recipients.FirstOrDefault(a => !SellerMailStore.LooksLikeEmail(a));
            if (badAddress is not null)
                return BadRequest(new { error = $"'{badAddress}' on {label} does not look like an e-mail address." });

            var claimedRecipients = (mail.Recipients ?? []).Select(SellerMailStore.NormalizeEmail).ToList();
            var tableRecipients = recipients.Select(SellerMailStore.NormalizeEmail).ToList();

            if (!claimedRecipients.SequenceEqual(tableRecipients, StringComparer.Ordinal))
            {
                return BadRequest(new
                {
                    error = $"The recipients for {label} were approved as " +
                            $"'{string.Join("; ", mail.Recipients ?? [])}' but the mapping now says " +
                            $"'{SellerMailStore.JoinAddresses(recipients)}'. Build the mails again and read the " +
                            "list before sending."
                });
            }

            var subject = (mail.Subject ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            var body = (mail.Body ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            if (subject.Length == 0)
                return BadRequest(new { error = $"The mail for {label} has no subject." });

            if (body.Length == 0)
                return BadRequest(new { error = $"The mail for {label} is empty." });

            // The posted attachment name is checked against the table rather than used: it exists so a
            // mismatch is caught and named, instead of the run quietly attaching something other than
            // what the preview showed.
            var expected = entry.FileName.Trim();
            var claimed = (mail.AttachmentName ?? "").Trim();

            if (!string.Equals(expected, claimed, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    error = $"The attachment for {label} was approved as '{claimed}' but the mapping now says " +
                            $"'{expected}'. Build the mails again and read the list before sending."
                });
            }

            var match = OfferMailBuilder.ResolveAttachment(folder, expected);
            if (match.Problem is not null)
                return BadRequest(new { error = $"The attachment for {label} cannot be used: {match.Problem}" });

            if (!System.IO.File.Exists(match.Path))
                return BadRequest(new { error = $"The attachment for {label} is not in the folder: {match.Path}" });

            mails.Add(new OutgoingMail(
                To: SellerMailStore.JoinAddresses(recipients),
                SellerId: entry.SellerId,
                SellerName: entry.SellerName,
                Subject: subject,
                Body: body,
                AttachmentPath: match.Path,
                AttachmentName: expected));
        }

        if (!_runner.TryStart(mails, request!.DryRun))
            return BadRequest(new { error = "An automation run is already in progress. Wait for it to finish." });

        return Ok(new { count = mails.Count, dryRun = request.DryRun });
    }

    // -----------------------------------------------------------------

    /// <summary>How many files sit in the attachment folder, so "0 sellers matched" can be told apart
    /// from "you are pointing at the wrong folder".</summary>
    static int CountFiles(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Count() : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// What identifies a row. The seller id wins when there is one — ids are stable, while a display
    /// name changes whenever a seller edits their storefront — and the folded name is the fallback for
    /// rows typed in without an id. Same precedence as <see cref="SellerGroupMap.Resolve"/>.
    /// </summary>
    static string SellerKey(SellerMailEntry entry)
    {
        var id = SellerGroupMap.NormalizeSellerId(entry.SellerId);
        return id.Length > 0 ? "id:" + id : "name:" + SellerGroupMap.FoldName(entry.SellerName);
    }

    /// <summary>
    /// Every row a send request could be referring to. Returns all of them rather than the first:
    /// the caller refuses an ambiguous match instead of picking one, because picking one here would
    /// mean guessing which seller's price list to attach.
    /// </summary>
    static List<SellerMailEntry> FindMatches(IReadOnlyList<SellerMailEntry> entries, string? sellerId, string? sellerName)
    {
        var id = SellerGroupMap.NormalizeSellerId(sellerId ?? "");
        if (id.Length > 0)
            return [.. entries.Where(e => SellerGroupMap.NormalizeSellerId(e.SellerId) == id)];

        var name = SellerGroupMap.FoldName(sellerName ?? "");
        if (name.Length == 0)
            return [];

        // Only rows that also have no id: a row carrying an id was matched by id or not at all, so
        // falling back to its name would let a nameless request reach it sideways.
        return [.. entries.Where(e =>
            SellerGroupMap.NormalizeSellerId(e.SellerId).Length == 0 &&
            SellerGroupMap.FoldName(e.SellerName) == name)];
    }

    /// <summary>How a seller is named in an error message: the id when there is one, since that is
    /// what the operator searches the table by.</summary>
    static string Describe(SendMail mail)
    {
        var id = SellerGroupMap.NormalizeSellerId(mail.SellerId ?? "");
        var name = (mail.SellerName ?? "").Trim();

        if (id.Length > 0)
            return name.Length > 0 ? $"'{name}' (id {id})" : $"seller id {id}";

        return name.Length > 0 ? $"'{name}'" : "one of the mails";
    }

    /// <summary>Matches on the normalized seller id when there is one, otherwise on the folded name —
    /// the same precedence <see cref="SellerGroupMap.Resolve"/> applies.</summary>
    static int FindExisting(List<SellerMailEntry> entries, SellerMailEntry candidate)
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

    /// <summary>Trims every field and drops rows with nothing identifying a seller. A row with a
    /// seller but no address is kept — that is "seen but not finished", not junk.</summary>
    static List<SellerMailEntry> Clean(IReadOnlyList<SellerMailEntry>? entries)
    {
        if (entries is null)
            return [];

        return [.. entries
            .Select(e => new SellerMailEntry(
                SellerGroupMap.NormalizeSellerId(e.SellerId ?? ""),
                (e.SellerName ?? "").Trim(),
                // Canonicalised on the way in, so the stored cell always uses one separator and holds
                // no repeats — whatever the operator pasted or the fetch wrote.
                SellerMailStore.JoinAddresses(SellerMailStore.SplitAddresses(e.Email)),
                (e.FileName ?? "").Trim(),
                Math.Max(0, e.LeadTime0),
                Math.Max(0, e.LeadTime1)))
            .Where(e => e.SellerId.Length > 0 || e.SellerName.Length > 0)];
    }

    static byte[] BuildMailsWorkbook(IReadOnlyList<RenderedMail> mails)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Mails");

        string[] headers = ["Seller ID", "Seller", "E-mail", "Attachment", "Lead time 0", "Lead time 1", "Problem", "Subject", "Body"];
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
            sheet.Cell(row, 4).SetValue(mail.AttachmentName);
            sheet.Cell(row, 5).SetValue(mail.LeadTime0);
            sheet.Cell(row, 6).SetValue(mail.LeadTime1);
            sheet.Cell(row, 7).SetValue(mail.Problem ?? "");
            sheet.Cell(row, 8).SetValue(mail.Subject);
            sheet.Cell(row, 9).SetValue(mail.Body);
        }

        // Ids and the body are text: an id loses its leading zeros as a number, and the body has to
        // keep the line breaks that make it a message rather than a paragraph.
        sheet.Column(1).Style.NumberFormat.Format = "@";
        sheet.Column(9).Style.NumberFormat.Format = "@";
        sheet.Column(9).Style.Alignment.WrapText = true;
        sheet.Column(9).Width = 80;
        sheet.Columns(1, 8).AdjustToContents();

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
