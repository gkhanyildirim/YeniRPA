using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Reads how many products each seller has in each catalogue status, from the Catalog Manager's own
/// "Durum" filter dropdown, and leaves the seller × status table in <see cref="ProductStatusStore"/>.
///
/// <para>Ported from the RPA project's <c>RpaService.ExportProductStatusAsync</c>. The URL and the
/// "label (1.204)" parse are unchanged, because those are pinned to Mirakl's markup rather than to
/// anything about this app. What changed is everything around it: the result is pivoted into a table
/// for the page instead of straight into a workbook, progress goes through
/// <see cref="AutomationJobBus"/> rather than SignalR, and a read is only believed once the figures
/// have stopped moving — see <see cref="ReadSettledStatusesAsync"/>.</para>
///
/// <para>The counts are only available from a rendered page: the dropdown is filled in by the page's own
/// script, so <c>MiraklBrowser.CreateAuthApiContextAsync</c> — which is how the bulk readers avoid paying
/// for Chromium — cannot see them. Hence a real page per seller, four at a time.</para>
/// </summary>
public sealed class ProductStatusRunner
{
    public const string ModuleName = "product-status";

    /// <summary>
    /// A refusal, not a truncation — same reasoning as <see cref="MarkAsReceivedRunner.MaxOrdersPerRun"/>.
    /// This module only reads, so the risk is not a bad write but a run nobody meant to start: at four
    /// pages at a time, a pasted spreadsheet column would tie up the browser for hours.
    /// </summary>
    public const int MaxSellersPerRun = 500;

    /// <summary>Four real Chrome pages at once, as the source module ran. Higher mostly buys timeouts:
    /// each page is loading the full Catalog Manager UI.</summary>
    const int Parallelism = 4;

    /// <summary>The inventory page is heavy and its own scripts keep polling, so the default page
    /// timeout is raised well past Playwright's 30 seconds.</summary>
    const int PageTimeoutMs = 90_000;

    /// <summary>How long one seller's counts are given to stop moving before the read is refused.
    /// A healthy seller pays almost none of this: the loop returns the moment the figures settle.</summary>
    const int CountsSettleBudgetMs = 30_000;

    /// <summary>Gap between two reads of the dropdown. The read itself is a round trip that
    /// <c>SlowMo</c> also charges for, so the real interval is somewhat longer than this.</summary>
    const int CountsPollIntervalMs = 400;

    /// <summary>How many identical consecutive reads mean the figures have stopped moving. Three, not
    /// two: the counts do not all land in one update, and two reads in a row can catch the same
    /// half-filled dropdown. Raise it if a wrong figure is ever seen again.</summary>
    const int CountsStableReads = 3;

    /// <summary>
    /// Off in normal runs. Turned on for a single diagnostic run — one seller, or four pages of
    /// traffic make the log unreadable — it names every request the inventory page makes, which is how
    /// the endpoint behind the status counts gets identified. Once that URL is known this module can
    /// wait on the response itself instead of watching the DOM, or skip Chromium altogether through
    /// the already-written <see cref="MiraklBrowser.CreateAuthApiContextAsync"/>.
    ///
    /// <para>A field rather than a <c>const</c> so that flipping it is a one-line edit: as a constant
    /// the compiler folds it away and warns that the switched-off branch is unreachable code.</para>
    /// </summary>
    static readonly bool LogDropdownRequests = false;

    /// <summary>The empty-list banner's text. Both languages are kept: the URL asks for the tr locale
    /// but the interface follows the signed-in operator's own profile.</summary>
    const string NoResultsTurkish = "Hiçbir sonuç bulunamadı";
    const string NoResultsEnglish = "No results found";

    /// <summary>"Online (1.204)" → label and count. The thousands separator is a dot in the tr locale
    /// the page is requested in.</summary>
    static readonly Regex StatusItemPattern = new(@"^(.+?)\s*\(([\d\.]+)\)$", RegexOptions.Compiled);

    readonly AutomationJobBus _bus;
    readonly MiraklBrowser _browser;
    readonly ProductStatusStore _store;
    readonly ILogger<ProductStatusRunner> _logger;

    public ProductStatusRunner(
        AutomationJobBus bus,
        MiraklBrowser browser,
        ProductStatusStore store,
        ILogger<ProductStatusRunner> logger)
    {
        _bus = bus;
        _browser = browser;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Claims the run slot and starts the batch in the background. False when another automation run
    /// already holds the slot — the caller turns that into the operator-facing error.
    /// </summary>
    /// <param name="intake">What the submitted list came from, carried through to the result so the
    /// table's row count can be reconciled with the file the operator uploaded.</param>
    public bool TryStart(IReadOnlyList<string> sellerNames, ProductStatusIntake intake)
    {
        ArgumentNullException.ThrowIfNull(sellerNames);
        ArgumentNullException.ThrowIfNull(intake);

        if (!_bus.TryBeginRun(ModuleName))
            return false;

        // Deliberately not awaited: the POST returns as soon as the batch is accepted, and progress
        // reaches the browser over the event stream instead of over this request.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(sellerNames, intake);
            }
            catch (Exception ex)
            {
                // Every per-seller failure is already handled inside the loop, so reaching here means
                // the browser or the session failed and no seller could have been read.
                _logger.LogError(ex, "Product Status run failed before it could read any seller.");
                _bus.Log($"Fatal error: {ex.Message}");

                // The held table has to go too: the page fetches it when the run reports done, and an
                // earlier run's figures under this run's timestamp would read as a result rather than
                // as a failure.
                _store.Put(ProductStatusResult.FromRows(sellerNames, [], [.. sellerNames], [], intake));
                _bus.Done(0, [.. sellerNames]);
            }
            finally
            {
                _bus.EndRun();
            }
        });

        return true;
    }

    async Task RunAsync(IReadOnlyList<string> sellerNames, ProductStatusIntake intake)
    {
        _bus.Started(ModuleName, sellerNames.Count);
        _bus.Log($"Starting {sellerNames.Count} seller(s).");

        // Without a session every page lands on the login screen and waits out the full 15-second
        // locator timeout before failing — several hundred times over. Refusing up front says what is
        // actually wrong instead of producing a long run of identical timeouts.
        if (!_browser.HasSavedSession)
        {
            throw new InvalidOperationException(
                "There is no saved Mirakl session. Sign in with 'Open login window' and save the session first.");
        }

        var browser = await _browser.EnsureBrowserAsync();
        await using var context = await _browser.CreateAuthContextAsync(browser);

        var scraped = new ConcurrentBag<ProductStatusRow>();
        var failedBag = new ConcurrentBag<string>();

        // Kept so an empty catalogue can be named in the result. Without it a seller that was read
        // perfectly well and simply has no products is missing from the table with nothing anywhere
        // to say why — indistinguishable, to whoever is counting rows, from one that was never read.
        var withoutProductsBag = new ConcurrentBag<string>();
        var completed = 0;

        await Parallel.ForEachAsync(
            sellerNames,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
            async (sellerName, cancellationToken) =>
            {
                var page = await context.NewPageAsync();
                page.SetDefaultTimeout(PageTimeoutMs);

                try
                {
                    var rows = await ScrapeSellerAsync(page, sellerName);
                    foreach (var row in rows)
                        scraped.Add(row);

                    if (rows.Count == 0)
                        withoutProductsBag.Add(sellerName);

                    _bus.Log(rows.Count == 0
                        ? $"Skipped: {sellerName} (no products)"
                        : $"Done: {sellerName} ({rows.Count} statuses)");
                }
                catch (Exception ex)
                {
                    failedBag.Add(sellerName);
                    _logger.LogWarning(ex, "Product Status failed for seller {SellerName}.", sellerName);

                    var screenshotPath = await AutomationArtifacts.TryCaptureFailureScreenshotAsync(
                        _bus, page, ModuleName, sellerName);
                    var suffix = screenshotPath is null ? string.Empty : $" | screenshot: {screenshotPath}";
                    _bus.Log($"Failed: {sellerName} - {ex.Message}{suffix}");
                }
                finally
                {
                    _bus.Progress(Interlocked.Increment(ref completed), sellerNames.Count);
                    try { await page.CloseAsync(); } catch { /* the page is already gone */ }
                }
            });

        // Ordered by the submitted list rather than by whichever page finished first — see
        // ProductStatusResult.FromRows.
        var failed = failedBag.ToList();
        _store.Put(ProductStatusResult.FromRows(
            sellerNames, [.. scraped], failed, [.. withoutProductsBag], intake));

        _bus.Done(sellerNames.Count - failed.Count, failed);
    }

    /// <summary>
    /// Opens one seller's inventory list, opens the status filter, and reads every "label (count)" the
    /// dropdown offers.
    /// </summary>
    async Task<IReadOnlyList<ProductStatusRow>> ScrapeSellerAsync(IPage page, string sellerName)
    {
        _bus.Log($"  [{sellerName}] Navigate to inventory page");

        // NetworkIdle can hang indefinitely here because the page polls in the background;
        // DOMContentLoaded is enough — the status button is waited for explicitly below.
        await page.GotoAsync(
            "https://mediamarktsaturn.mirakl.net/mcm/front/inventory/list" +
            $"?catalogLocale=tr&context=MMTR&contextType=CHANNEL&providers={Uri.EscapeDataString(sellerName)}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // The status button is rendered before the list itself, so it is what "the page is usable" means.
        var statusButton = page.Locator("button")
            .Filter(new LocatorFilterOptions { HasText = "Durum" })
            .First;
        await statusButton.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        var container = await OpenStatusDropdownAsync(page, statusButton, sellerName);
        return await ReadSettledStatusesAsync(page, container, sellerName);
    }

    /// <summary>
    /// Clicks the "Durum" filter and returns the popup it opened.
    ///
    /// <para>Every facet on this page renders the same <c>.mui-suggestions-container</c>, so taking the
    /// first one takes whichever sits earliest in the DOM rather than the one just clicked. Only the
    /// open popup is visible, which makes visibility exactly the question "which one was clicked".</para>
    /// </summary>
    async Task<ILocator> OpenStatusDropdownAsync(IPage page, ILocator statusButton, string sellerName)
    {
        _bus.Log($"  [{sellerName}] Open status dropdown");

        // Attached before the click, because the request that fills the counts is made in response to it.
        if (LogDropdownRequests)
            AttachRequestLogger(page, sellerName);

        await statusButton.ClickAsync();

        var open = page.Locator(".mui-suggestions-container")
            .Filter(new LocatorFilterOptions { Visible = true });

        await open.First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000
        });

        // Not an error: reading the wrong popup cannot produce a wrong figure, because a popup that is
        // not the status filter parses to no "label (count)" rows at all and the seller is reported as
        // unread. The line is here so that case is diagnosable rather than silent.
        var openCount = await open.CountAsync();
        if (openCount > 1)
            _bus.Log($"  [{sellerName}] {openCount} filter popups are open at once - reading the first.");

        return open.First;
    }

    /// <summary>
    /// Reads the open dropdown until its figures stop moving, and returns them.
    ///
    /// <para>The dropdown renders every count at "(0)" the instant it opens; the real figures land a
    /// moment later from a background call the page makes once the menu is shown, and not necessarily
    /// all at once. So a single read proves nothing, and "are they all zero?" is not enough of a
    /// question either: a dropdown showing two of six statuses filled in is neither the first render
    /// nor the answer. What settling means here is the same figures coming back several reads running,
    /// which is the reasoning <c>WhatsAppMessageRunner.VerifyHeaderAsync</c> applies to a header that
    /// renders a moment after the panel it sits in.</para>
    ///
    /// <para>When the budget runs out the zeroes are refused rather than recorded. A wrong zero is
    /// indistinguishable from a real one once it is in the table, whereas a seller left out of it is
    /// named in the operator's "could not be read" note.</para>
    /// </summary>
    async Task<IReadOnlyList<ProductStatusRow>> ReadSettledStatusesAsync(
        IPage page, ILocator container, string sellerName)
    {
        _bus.Log($"  [{sellerName}] Read status items");

        var banner = NoResultsBanner(page);
        var budget = Stopwatch.StartNew();
        var stable = 0;
        var reads = 0;
        List<ProductStatusRow>? previous = null;
        List<ProductStatusRow> rows = [];

        while (true)
        {
            rows = await ReadStatusItemsAsync(container, sellerName);
            reads++;

            stable = NextStableReadCount(rows, previous, stable);
            previous = rows;

            if (CountsHaveLanded(rows, stable))
            {
                _bus.Log($"  [{sellerName}] Counts settled after {reads} read(s) in {budget.ElapsedMilliseconds} ms");
                return rows;
            }

            // Only asked while the counts still say nothing, and only then: a seller with an empty
            // catalogue reads as all-zero for ever, and the banner saying so can render after the
            // dropdown does. Waiting for it up front instead would charge every full seller for it.
            if (!IsUsableRead(rows) && await IsVisibleSafeAsync(banner))
            {
                _bus.Log($"  [{sellerName}] No products found — skipped");
                return [];
            }

            if (budget.ElapsedMilliseconds >= CountsSettleBudgetMs)
                break;

            await page.WaitForTimeoutAsync(CountsPollIntervalMs);
        }

        var dropdownHtml = await TryCaptureDropdownHtmlAsync(page);

        throw new InvalidOperationException(
            $"The status counts never settled within {CountsSettleBudgetMs / 1000} seconds ({reads} reads). " +
            "Last read: " +
            (rows.Count == 0 ? "(nothing)" : string.Join(", ", rows.Select(r => $"{r.StatusLabel}={r.Count}"))) +
            ". Nothing was recorded for this seller, rather than recording the zeroes as figures." +
            (dropdownHtml is null ? "" : $" Dropdown HTML: {dropdownHtml}"));
    }

    /// <summary>One read of the currently open status dropdown's "label (count)" items.</summary>
    static async Task<List<ProductStatusRow>> ReadStatusItemsAsync(ILocator container, string sellerName)
    {
        // One round trip for the whole dropdown. Asking for the count and then each item's text in turn
        // is a dozen separate calls, and MiraklBrowser launches with SlowMo — which charges every one of
        // them, making a single read cost seconds. That is affordable once and not at all in a loop.
        //
        // This deliberately does not auto-wait: it reports whatever is on the page right now, because
        // the half-filled state is the thing the caller is watching for rather than something to hide.
        //
        // :not(.fa) drops the icon spans, which carry no text of their own.
        var texts = await container.Locator(".mui-suggestion-item span:not(.fa)").AllTextContentsAsync();

        var rows = new List<ProductStatusRow>();
        foreach (var raw in texts)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0)
                continue;

            var match = StatusItemPattern.Match(text);
            if (!match.Success)
                continue;

            // A status the page renders in a shape this does not recognise is skipped rather than
            // counted as zero — a missing column is visible, a wrong figure is not.
            if (int.TryParse(match.Groups[2].Value.Replace(".", string.Empty), out var count))
                rows.Add(new ProductStatusRow(sellerName, match.Groups[1].Value.Trim(), count));
        }

        return rows;
    }

    /// <summary>
    /// Whether a read could be the answer at all: it found statuses, and at least one of them is not
    /// zero. An all-zero dropdown is what the page shows before the figures arrive, and a seller whose
    /// catalogue really is empty is recognised by its banner instead — see
    /// <see cref="ReadSettledStatusesAsync"/>.
    /// </summary>
    internal static bool IsUsableRead(IReadOnlyList<ProductStatusRow> rows) =>
        rows.Count > 0 && rows.Any(r => r.Count > 0);

    /// <summary>
    /// How many reads in a row have now agreed, given the one before. Zero when the new read cannot be
    /// an answer yet, one when it disagrees with its predecessor and so starts a fresh run, otherwise
    /// one more than the run it continues.
    /// </summary>
    internal static int NextStableReadCount(
        IReadOnlyList<ProductStatusRow> rows,
        IReadOnlyList<ProductStatusRow>? previous,
        int stableReads)
    {
        if (!IsUsableRead(rows))
            return 0;

        return SameCounts(rows, previous) ? stableReads + 1 : 1;
    }

    /// <summary>Whether the figures can be believed yet.</summary>
    internal static bool CountsHaveLanded(IReadOnlyList<ProductStatusRow> rows, int stableReads) =>
        IsUsableRead(rows) && stableReads >= CountsStableReads;

    /// <summary>
    /// Whether two reads say exactly the same thing. A changed label set counts as a disagreement too:
    /// the dropdown gaining or reordering a status is the page still working, not two ways of saying
    /// the same figures.
    /// </summary>
    internal static bool SameCounts(
        IReadOnlyList<ProductStatusRow> rows,
        IReadOnlyList<ProductStatusRow>? previous) =>
        previous is not null
        && rows.Count == previous.Count
        && rows.Zip(previous).All(pair =>
            string.Equals(pair.First.StatusLabel, pair.Second.StatusLabel, StringComparison.Ordinal)
            && pair.First.Count == pair.Second.Count);

    /// <summary>
    /// The empty-list banner, in whichever language the page is showing.
    ///
    /// <para>This used to be <c>Locator("text=A, text=B")</c>, which never matched anything: the
    /// <c>text=</c> engine takes everything after the <c>=</c> as one string to look for, so the comma
    /// was part of the phrase rather than a separator, and the "no products" branch was dead. A CSS
    /// list of <c>:has-text()</c> would be worse than dead — that pseudo-class matches ancestors as
    /// well, so <c>html</c> would match and every seller would look empty.</para>
    /// </summary>
    static ILocator NoResultsBanner(IPage page) =>
        page.GetByText(NoResultsTurkish).Or(page.GetByText(NoResultsEnglish)).First;

    /// <summary>Asking whether something is on screen is a question, not a wait: a page that has gone
    /// away answers "no" and lets the caller's own failure be the one reported.</summary>
    static async Task<bool> IsVisibleSafeAsync(ILocator locator)
    {
        try
        {
            return await locator.IsVisibleAsync();
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            // A locator timeout arrives as System.TimeoutException in this version of the package —
            // see WhatsAppSelectors.IsCandidateMiss, which documents why both have to be caught.
            return false;
        }
    }

    /// <summary>
    /// Best-effort snapshot of the dropdown that would not settle, for the failure message — the same
    /// reasoning as <c>WhatsAppMessageRunner.TryCaptureHeaderHtmlAsync</c>: a repeat of this failure
    /// should be diagnosable from the log rather than needing a live repro. It looks for the visible
    /// container, the same one <see cref="OpenStatusDropdownAsync"/> chose, so the markup shown is the
    /// markup that was actually being read. Truncated because this lands in a log line, not a file.
    /// </summary>
    static async Task<string?> TryCaptureDropdownHtmlAsync(IPage page)
    {
        try
        {
            var html = await page.EvaluateAsync<string?>(
                """
                () => [...document.querySelectorAll('.mui-suggestions-container')]
                        .find(el => el.getClientRects().length > 0)?.outerHTML ?? null
                """);

            if (string.IsNullOrEmpty(html))
                return null;

            return html.Length > 2000 ? html[..2000] + "…" : html;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Names every request the page makes from here on, so the call that fills the counts can
    /// be identified. The handler belongs to the page and dies with it.</summary>
    void AttachRequestLogger(IPage page, string sellerName)
    {
        page.Request += (_, request) =>
        {
            if (request.ResourceType is "xhr" or "fetch")
                _bus.Log($"  [{sellerName}] {request.Method} {request.Url}");
        };
    }
}
