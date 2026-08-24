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
    string AttachmentName);

/// <summary>
/// Sends the approved batch of seller warnings through <see cref="OutlookMailSender"/>, reporting on
/// the shared <see cref="AutomationJobBus"/>.
///
/// <para>Far smaller than <c>LateOrderWhatsAppRunner</c> because the risky part is elsewhere: there,
/// the danger is finding the right chat in a UI that re-sorts under the click, so the guards are
/// read-backs against the live page. Here, the address and the attachment are decided before the run
/// starts and re-validated by the controller, so this class only has to send them, pace itself, and
/// account for every row.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OfferMailRunner
{
    public const string ModuleName = "offer-warnings";

    /// <summary>
    /// A refusal, not a truncation: over this the request is rejected so the operator narrows the run
    /// rather than being left believing all of them went out. Sized to clear the ~190 sellers in the
    /// current table in one pass.
    /// </summary>
    public const int MaxMailsPerRun = 250;

    /// <summary>Live sends are paced so a batch does not arrive as one burst — Exchange throttles,
    /// and 190 mails in ten seconds looks like a compromised mailbox to anyone watching.</summary>
    const int MinDelayMs = 2_000;
    const int MaxDelayMs = 5_000;

    /// <summary>A dry run writes drafts locally; there is nothing outbound to pace.</summary>
    const int DryRunDelayMs = 250;

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

    async Task RunAsync(IReadOnlyList<OutgoingMail> mails, bool dryRun, string moduleName)
    {
        _bus.Started(moduleName, mails.Count);
        _bus.Log(dryRun
            ? $"DRY RUN — composing {mails.Count} mail(s) into Outlook's Drafts folder. Nothing will be sent."
            : $"LIVE — sending {mails.Count} mail(s). A sent mail cannot be recalled.");
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

        for (var i = 0; i < mails.Count; i++)
        {
            var mail = mails[i];

            try
            {
                // Re-checked here rather than trusting the prepare step: minutes can pass between the
                // preview and the click, and an attachment that has been moved or replaced in the
                // meantime must stop this row rather than travel as a stale price list.
                if (!File.Exists(mail.AttachmentPath))
                    throw new FileNotFoundException($"The attachment is no longer at {mail.AttachmentPath}.");

                await _sender.SendAsync(mail.To, mail.Subject, mail.Body, mail.AttachmentPath, dryRun);

                processed++;
                _bus.Log($"{(dryRun ? "Drafted" : "Sent")} → {mail.SellerName} · {mail.To} · {mail.AttachmentName}");
            }
            catch (Exception ex)
            {
                failed.Add(mail.SellerName);
                _logger.LogWarning(ex, "The warning mail for seller {SellerName} could not be composed.", mail.SellerName);
                _bus.Log($"FAILED → {mail.SellerName} ({mail.To}): {OutlookMailSender.Describe(ex)}");
            }

            _bus.Progress(i + 1, mails.Count);

            if (i < mails.Count - 1)
                await Task.Delay(dryRun ? DryRunDelayMs : random.Next(MinDelayMs, MaxDelayMs));
        }

        _bus.Done(processed, failed);
    }
}
