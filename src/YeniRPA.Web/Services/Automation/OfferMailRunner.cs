using System.Runtime.Versioning;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// One approved mail, validated by the controller and about to be handed to Outlook.
/// <paramref name="To"/> is every user the seller has, joined the way Outlook expects — one mail
/// addressed to all of them, not one mail each.
/// </summary>
public sealed record OutgoingMail(
    string To,
    string SellerId,
    string SellerName,
    string Subject,
    string Body,
    string AttachmentPath,
    string AttachmentName,

    /// <summary>Who is copied, visibly, or <c>null</c> for nobody. Optional and last so a module that
    /// copies no one — Seller Offer Warnings — carries on constructing this record unchanged.</summary>
    string? Cc = null,

    /// <summary>Who is copied, invisibly to every other recipient, or <c>null</c> for nobody. Optional
    /// for the same reason <paramref name="Cc"/> is — only Custom Mail sets this today.</summary>
    string? Bcc = null,

    /// <summary>Whether to put the operator's own Outlook signature under the body, which also makes
    /// the mail HTML rather than plain text. Optional and off by default for the same reason.</summary>
    bool IncludeSignature = false,

    /// <summary>Whether <see cref="Body"/> is already HTML markup rather than a plain-text template —
    /// true for Custom Mail's rich-text body, false (the default) for every plain-text template, which
    /// still needs converting before it can carry a signature. Optional for the same reason as
    /// <paramref name="Cc"/>.</summary>
    bool IsHtmlBody = false);

/// <summary>
/// Sends the approved batch of seller warnings through <see cref="OutlookMailSender"/>, reporting on
/// the shared <see cref="AutomationJobBus"/>.
///
/// <para>Far smaller than <c>WhatsAppMessageRunner</c> because the risky part is elsewhere: there,
/// the danger is finding the right chat in a UI that re-sorts under the click, so the guards are
/// read-backs against the live page. Here, the address and the attachment are decided before the run
/// starts and re-validated by the controller, so this class only has to send them, split them into
/// paced passes, and account for every row — including the ones a stopped run never reached.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OfferMailRunner
{
    public const string ModuleName = "offer-warnings";

    /// <summary>
    /// One pass's worth of mail. A run larger than this is split into passes of this size with a
    /// break between them, rather than refused: the export routinely produces more sellers than one
    /// pass can carry, and splitting it by hand left the operator remembering which half went out.
    /// </summary>
    public const int MailsPerPass = 250;

    /// <summary>
    /// A refusal, not a truncation: over this the request is rejected so the operator narrows the run
    /// rather than being left believing all of them went out. The passes above make a large run
    /// possible; this ceiling is what stops a mis-built 5,000-row export from going out on one click.
    /// </summary>
    public const int MaxMailsPerRun = 1_000;

    /// <summary>Live sends are paced so a batch does not arrive as one burst — Exchange throttles,
    /// and 190 mails in ten seconds looks like a compromised mailbox to anyone watching.</summary>
    const int MinDelayMs = 2_000;
    const int MaxDelayMs = 5_000;

    /// <summary>A dry run writes drafts locally; there is nothing outbound to pace.</summary>
    const int DryRunDelayMs = 250;

    /// <summary>
    /// The pause between passes. Not pacing — the per-mail delay already does that — but a visible
    /// seam in the log and a breather for the mailbox. A dry run only needs the seam.
    /// </summary>
    const int PassBreakMs = 120_000;
    const int DryRunPassBreakMs = 1_000;

    readonly OutlookMailSender _sender;
    readonly AutomationJobBus _bus;
    readonly ILogger<OfferMailRunner> _logger;

    public OfferMailRunner(OutlookMailSender sender, AutomationJobBus bus, ILogger<OfferMailRunner> logger)
    {
        _sender = sender;
        _bus = bus;
        _logger = logger;
    }

    /// <summary>
    /// Claims the app-wide run slot and starts the batch in the background. False when another
    /// automation run already holds it.
    ///
    /// <para><paramref name="moduleName"/> is what the run reports itself as on the shared bus, so the
    /// panel that started it can tell its own log lines from another module's. It is a parameter rather
    /// than a second copy of this class because both callers want exactly the same behaviour — hand the
    /// approved batch to Outlook, pace it, account for every row — and a change to the pacing or the
    /// failure handling should reach both. Only the label differs.</para>
    /// </summary>
    public bool TryStart(IReadOnlyList<OutgoingMail> mails, bool dryRun, string moduleName = ModuleName)
    {
        ArgumentNullException.ThrowIfNull(mails);

        if (!_bus.TryBeginRun(moduleName))
            return false;

        // Deliberately not awaited: the POST returns as soon as the batch is accepted, and progress
        // reaches the browser over the event stream instead of over this request.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(mails, dryRun, moduleName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The Outlook run failed before it could send anything.");
                _bus.Log($"Fatal error: {OutlookMailSender.Describe(ex)}");
                _bus.Done(0, [.. mails.Select(m => m.SellerName)]);
            }
            finally
            {
                _bus.EndRun();
            }
        });

        return true;
    }

    /// <summary>
    /// The <c>[Start, Start + Count)</c> range each pass covers, in order.
    ///
    /// <para>Split out as a static function so the arithmetic can be tested on its own: everything
    /// else in this class needs a live Outlook, and "did the last pass carry the remainder" is
    /// exactly the kind of off-by-one that would otherwise only surface on a 251-mail run.</para>
    /// </summary>
    public static IReadOnlyList<(int Start, int Count)> PlanPasses(int total, int perPass)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perPass, 1);

        if (total <= 0)
            return [];

        var passes = new List<(int, int)>((total + perPass - 1) / perPass);

        for (var start = 0; start < total; start += perPass)
            passes.Add((start, Math.Min(perPass, total - start)));

        return passes;
    }

    async Task RunAsync(IReadOnlyList<OutgoingMail> mails, bool dryRun, string moduleName)
    {
        var token = _bus.RunToken;
        var passes = PlanPasses(mails.Count, MailsPerPass);
        var breakMs = dryRun ? DryRunPassBreakMs : PassBreakMs;

        _bus.Started(moduleName, mails.Count);
        _bus.Log(dryRun
            ? $"DRY RUN — composing {mails.Count} mail(s) into Outlook's Drafts folder. Nothing will be sent."
            : $"LIVE — sending {mails.Count} mail(s). A sent mail cannot be recalled.");

        // Said up front, before anything goes out: a run that pauses for two minutes in the middle
        // looks like a run that has hung to anyone who was not told to expect it.
        if (passes.Count > 1)
        {
            _bus.Log($"This goes out in {passes.Count} passes of at most {MailsPerPass}, " +
                     $"with a {breakMs / 1000}-second break between them.");
        }

        _bus.Log("");

        if (!await _sender.ProbeAsync())
        {
            // A run-level failure, not a per-row one: every remaining mail would fail identically and
            // 190 copies of the same line buries the one sentence that explains it.
            _bus.Log($"Outlook could not be reached: {_sender.LastError}");
            _bus.Log("Start Outlook, sign in to the mailbox, and run this again.");
            _bus.Done(0, [.. mails.Select(m => m.SellerName)]);
            return;
        }

        var random = new Random();
        var failed = new List<string>();
        var processed = 0;
        var attempted = 0;
        var stopped = false;

        for (var p = 0; p < passes.Count && !stopped; p++)
        {
            var (start, count) = passes[p];

            if (p > 0)
            {
                _bus.Log("");
                _bus.Log($"Pausing {breakMs / 1000} second(s) before the next pass.");

                if (!await DelayAsync(breakMs, token))
                {
                    stopped = true;
                    break;
                }

                // Re-probed rather than trusted: minutes separate this pass from the last one, and an
                // Outlook that was closed in between would otherwise fail every remaining row with
                // the same line instead of the one sentence that explains it.
                if (!await _sender.ProbeAsync())
                {
                    _bus.Log($"Outlook could not be reached for this pass: {_sender.LastError}");
                    _bus.Log($"{mails.Count - attempted} mail(s) were not sent. Start Outlook and run them again.");
                    failed.AddRange(mails.Skip(start).Select(m => m.SellerName));
                    break;
                }
            }

            if (passes.Count > 1)
                _bus.Log($"Pass {p + 1}/{passes.Count} — mails {start + 1}-{start + count} of {mails.Count}.");

            for (var i = start; i < start + count; i++)
            {
                // Checked between rows rather than inside one: a mail that has reached Outlook is
                // already on its way and cannot be taken back, so stopping means stopping before the
                // next one rather than abandoning the one in flight.
                if (token.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                var mail = mails[i];
                attempted++;

                try
                {
                    // Re-checked here rather than trusting the prepare step: minutes can pass between the
                    // preview and the click, and an attachment that has been moved or replaced in the
                    // meantime must stop this row rather than travel as a stale price list.
                    if (!File.Exists(mail.AttachmentPath))
                        throw new FileNotFoundException($"The attachment is no longer at {mail.AttachmentPath}.");

                    await _sender.SendAsync(
                        mail.To, mail.Cc, mail.Bcc, mail.Subject, mail.Body, mail.IsHtmlBody,
                        mail.AttachmentPath, dryRun, mail.IncludeSignature);

                    processed++;

                    // The CC/BCC are named in the log, not counted: the run record has to say who else
                    // received a copy of a seller's list, the same way it names the seller and the file.
                    // Naming the BCC here is not a leak — this is the operator's own audit trail, never
                    // seen by any recipient, which is the whole point of a blind copy.
                    var copiedTo = string.IsNullOrWhiteSpace(mail.Cc) ? "" : $" · cc {mail.Cc}";
                    var blindCopiedTo = string.IsNullOrWhiteSpace(mail.Bcc) ? "" : $" · bcc {mail.Bcc}";
                    _bus.Log($"{(dryRun ? "Drafted" : "Sent")} → {mail.SellerName} · {mail.To}{copiedTo}{blindCopiedTo} · {mail.AttachmentName}");
                }
                catch (Exception ex)
                {
                    failed.Add(mail.SellerName);
                    _logger.LogWarning(ex, "The warning mail for seller {SellerName} could not be composed.", mail.SellerName);
                    _bus.Log($"FAILED → {mail.SellerName} ({mail.To}): {OutlookMailSender.Describe(ex)}");
                }

                _bus.Progress(i + 1, mails.Count);

                // Only within the pass — the break between passes covers the seam, and stacking the
                // two would pause for the break plus a mail's worth of pacing.
                if (i < start + count - 1 && !await DelayAsync(dryRun ? DryRunDelayMs : random.Next(MinDelayMs, MaxDelayMs), token))
                {
                    stopped = true;
                    break;
                }
            }
        }

        if (stopped)
        {
            // The mails that were never attempted are not counted as failures — nothing was tried for
            // them — but they are spelled out, because a run that ends early must never read as a run
            // that covered the whole list.
            _bus.Log("");
            _bus.Log($"Stopped by the operator at {attempted} of {mails.Count}. " +
                     $"{mails.Count - attempted} mail(s) were not attempted — tick them on the cards and run again.");
        }

        _bus.Done(processed, failed);
    }

    /// <summary>Waits, or gives up the moment the operator asks the run to stop. False means stop.</summary>
    static async Task<bool> DelayAsync(int milliseconds, CancellationToken token)
    {
        try
        {
            await Task.Delay(milliseconds, token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
