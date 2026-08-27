using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Reads how many products each seller has in each catalogue status, from the Catalog Manager's own
/// "Durum" filter dropdown, and leaves the seller × status table in <see cref="ProductStatusStore"/>.
///
/// <para>Ported from the RPA project's <c>RpaService.ExportProductStatusAsync</c>. The scraping is
/// unchanged — same URL, same selectors, same "label (1.204)" parse — because those are pinned to
/// Mirakl's markup rather than to anything about this app. What changed is everything around it: the
/// result is pivoted into a table for the page instead of straight into a workbook, and progress goes
/// through <see cref="AutomationJobBus"/> rather than SignalR.</para>
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
    public bool TryStart(IReadOnlyList<string> sellerNames)
    {
        ArgumentNullException.ThrowIfNull(sellerNames);

        if (!_bus.TryBeginRun(ModuleName))
            return false;

        // Deliberately not awaited: the POST returns as soon as the batch is accepted, and progress
        // reaches the browser over the event stream instead of over this request.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(sellerNames);
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
                _store.Put(ProductStatusResult.FromRows(sellerNames, [], [.. sellerNames]));
                _bus.Done(0, [.. sellerNames]);
            }
            finally
            {
                _bus.EndRun();
            }
        });

        return true;
    }

    async Task RunAsync(IReadOnlyList<string> sellerNames)
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
        _store.Put(ProductStatusResult.FromRows(sellerNames, [.. scraped], failed));

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

        var noResults = page.Locator("text=No results found, text=Hiçbir sonuç bulunamadı").First;
        if (await noResults.IsVisibleAsync())
        {
            _bus.Log($"  [{sellerName}] No products found — skipped");
            return [];
        }

        _bus.Log($"  [{sellerName}] Open status dropdown");
        await statusButton.ClickAsync();

        _bus.Log($"  [{sellerName}] Read status items");
        var container = page.Locator(".mui-suggestions-container").First;
        await container.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000
        });

        // :not(.fa) drops the icon spans, which carry no text of their own.
        var items = container.Locator(".mui-suggestion-item span:not(.fa)");
        var itemCount = await items.CountAsync();

        var rows = new List<ProductStatusRow>();
        for (var i = 0; i < itemCount; i++)
        {
            var text = (await items.Nth(i).TextContentAsync() ?? string.Empty).Trim();
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
}
