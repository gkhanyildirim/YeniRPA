using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Submits a prepared batch's tracking numbers to 17track.net's public multi-tracking tool in groups
/// of <see cref="Track17Filter.MaxPerBatch"/>, and leaves the delivered subset in <see cref="Track17Store"/>.
///
/// <para><b>Why some numbers need a manual carrier pick.</b> 17track's auto-detect resolves PTT Kargo
/// numbers fine (PTT is a universal postal operator, easy to fingerprint), but it cannot fingerprint
/// Kolay Gelsin or Sürat Kargo — both small Turkish regional couriers — and shows those tracking
/// numbers as "Bulunamadı" (not found) with a manual carrier picker instead. Both carriers do exist in
/// 17track's own catalogue (found by name through that picker's search box), so this runner opens the
/// picker for exactly the rows whose <see cref="Track17Row.Carrier"/> is one of
/// <see cref="ManualCarrierSearchTerms"/> and forces the carrier explicitly, rather than trusting
/// auto-detect.</para>
///
/// <para><b>Why "Teslim edildi" is read as a tab, not parsed from free text.</b> 17track's own result
/// page already filters to exactly the delivered subset when that status tab is clicked — reading that
/// filtered list is far more reliable than pattern-matching a free-text status string per row, which
/// is also what the operator asked for.</para>
///
/// <para>Selectors below are pinned to 17track's current markup, discovered by driving the live site
/// (like the DOM/parse specifics in <see cref="ProductStatusRunner"/> are pinned to Mirakl's) — they
/// have no API contract behind them and can drift if 17track changes their front end.</para>
/// </summary>
public sealed class Track17Runner
{
    public const string ModuleName = "track17";

    const int MinBatchDelayMs = 3_000;
    const int MaxBatchDelayMs = 7_000;
    const int NavigationTimeoutMs = 45_000;
    const int InitialSettleBudgetMs = 30_000;
    const int SettlePollIntervalMs = 800;

    const string SearchPageUrl = "https://www.17track.net/tr";
    const string ResultHostFragment = "t.17track.net";
    const string CarrierSearchPlaceholder = "Posta hizmeti, kargo firması ya da ülke adı ara.";

    /// <summary>Carriers 17track's auto-detect cannot fingerprint, and the ASCII search term that
    /// finds each one's single entry in the carrier picker (accents dropped — the search box matches
    /// on them fine, and typing plain ASCII avoids any encoding surprise over the wire).</summary>
    static readonly Dictionary<string, string> ManualCarrierSearchTerms = new(StringComparer.Ordinal)
    {
        ["Kolay Gelsin"] = "kolay gelsin",
        ["Sürat Kargo"] = "surat kargo",
    };

    readonly AutomationJobBus _bus;
    readonly Track17Browser _browser;
    readonly Track17Store _store;
    readonly ILogger<Track17Runner> _logger;

    public Track17Runner(AutomationJobBus bus, Track17Browser browser, Track17Store store, ILogger<Track17Runner> logger)
    {
        _bus = bus;
        _browser = browser;
        _store = store;
        _logger = logger;
    }

    /// <summary>Claims the shared automation run slot and starts the batch in the background. False
    /// when another automation module already holds it.</summary>
    public bool TryStart(Track17Batch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (!_bus.TryBeginRun(ModuleName))
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(batch);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Track 17 run failed before it could read any batch.");
                _bus.Log($"Fatal error: {ex.Message}");
                _store.Put(new Track17RunResult(DateTimeOffset.Now, 0, [], 0, []));
                _bus.Done(0, ["run"]);
            }
            finally
            {
                _bus.EndRun();
            }
        });

        return true;
    }

    async Task RunAsync(Track17Batch batch)
    {
        var byTracking = batch.Rows
            .GroupBy(r => r.TrackingNumber, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Track17Row>)g.ToList(), StringComparer.Ordinal);

        var distinct = byTracking.Keys.ToList();
        var chunks = Chunk(distinct, Track17Filter.MaxPerBatch);

        _bus.Started(ModuleName, distinct.Count);
        _bus.Log($"Starting {distinct.Count} tracking number(s) across {chunks.Count} batch(es) of up to {Track17Filter.MaxPerBatch}.");

        var browser = await _browser.EnsureBrowserAsync();
        await using var context = await _browser.NewContextAsync(browser);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(NavigationTimeoutMs);

        var delivered = new List<Track17DeliveredRow>();
        var failedBatches = new List<int>();
        var processed = 0;

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            _bus.Log($"Batch {i + 1}/{chunks.Count}: submitting {chunk.Count} tracking number(s)");

            try
            {
                var rows = await RunBatchAsync(page, chunk, byTracking);
                delivered.AddRange(rows);
                _bus.Log($"  -> {rows.Count} delivered order line(s) in this batch");
            }
            catch (Exception ex)
            {
                failedBatches.Add(i + 1);
                var screenshotPath = await AutomationArtifacts.TryCaptureFailureScreenshotAsync(
                    _bus, page, ModuleName, $"batch-{i + 1}");
                var suffix = screenshotPath is null ? string.Empty : $" | screenshot: {screenshotPath}";
                _logger.LogWarning(ex, "Track 17 batch {BatchNumber} failed.", i + 1);
                _bus.Log($"  Batch {i + 1} failed: {ex.Message}{suffix}");
            }
            finally
            {
                processed += chunk.Count;
                _bus.Progress(processed, distinct.Count);
            }

            // A courtesy gap between requests to a free public tool, not a documented rate limit.
            if (i < chunks.Count - 1)
                await page.WaitForTimeoutAsync(Random.Shared.Next(MinBatchDelayMs, MaxBatchDelayMs));
        }

        try { await page.CloseAsync(); } catch { /* context disposal below cleans up regardless */ }

        _store.Put(new Track17RunResult(DateTimeOffset.Now, distinct.Count, delivered, chunks.Count, failedBatches));
        _bus.Done(distinct.Count, failedBatches.Select(b => $"batch {b}").ToList());
    }

    async Task<List<Track17DeliveredRow>> RunBatchAsync(
        IPage page, IReadOnlyList<string> trackingNumbers, IReadOnlyDictionary<string, IReadOnlyList<Track17Row>> byTracking)
    {
        await page.GotoAsync(SearchPageUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var textarea = page.Locator("textarea").First;
        await textarea.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await textarea.FillAsync(string.Join("\n", trackingNumbers));

        var takip = page.GetByText("Takip", new PageGetByTextOptions { Exact = true }).First;
        await takip.ClickAsync();

        await page.WaitForURLAsync(
            url => url.Contains(ResultHostFragment, StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = NavigationTimeoutMs });

        // First-ever visit to the results host in this browser context shows a react-joyride
        // onboarding tour whose overlay blocks every other click until closed.
        await DismissOnboardingTourAsync(page);

        await WaitForCardCountAsync(page, trackingNumbers.Count, InitialSettleBudgetMs);

        await ResolveAmbiguousCarriersAsync(page, trackingNumbers, byTracking);

        await OpenDeliveredTabAsync(page);

        return await ReadDeliveredRowsAsync(page, byTracking);
    }

    static async Task DismissOnboardingTourAsync(IPage page)
    {
        try
        {
            var close = page.Locator("button.tooltip__close");
            if (await close.CountAsync() > 0)
            {
                await close.First.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                await page.WaitForTimeoutAsync(500);
            }
        }
        catch
        {
            // No tour this run, or it dismissed itself in the meantime — either way, nothing to do.
        }
    }

    /// <summary>One result card per submitted tracking number. Waits for them all to render, since
    /// 17track fetches and paints each one asynchronously after the navigation completes.</summary>
    async Task WaitForCardCountAsync(IPage page, int expectedCount, int budgetMs)
    {
        var cards = ResultCards(page);
        var budget = Stopwatch.StartNew();
        var last = -1;

        while (budget.ElapsedMilliseconds < budgetMs)
        {
            var count = await cards.CountAsync();
            if (count >= expectedCount)
                return;

            last = count;
            await page.WaitForTimeoutAsync(SettlePollIntervalMs);
        }

        _bus.Log($"  Warning: expected {expectedCount} result card(s), only {last} rendered after {budgetMs / 1000}s.");
    }

    /// <summary>
    /// Opens the per-row carrier picker for every submitted number whose known carrier is one
    /// auto-detect cannot resolve, and forces that carrier explicitly.
    /// </summary>
    async Task ResolveAmbiguousCarriersAsync(
        IPage page, IReadOnlyList<string> trackingNumbers, IReadOnlyDictionary<string, IReadOnlyList<Track17Row>> byTracking)
    {
        foreach (var trackingNumber in trackingNumbers)
        {
            var carrier = byTracking[trackingNumber][0].Carrier;
            if (!ManualCarrierSearchTerms.TryGetValue(carrier, out var searchTerm))
                continue;

            var card = ResultCardFor(page, trackingNumber);
            var chooseCarrier = card.GetByText("Taşıyıcı seç", new LocatorGetByTextOptions { Exact = true });

            // Auto-detect occasionally does resolve one of these carriers on its own; nothing to do then.
            if (await chooseCarrier.CountAsync() == 0)
                continue;

            try
            {
                await chooseCarrier.First.ClickAsync(new LocatorClickOptions { Timeout = 8_000 });

                var modal = page.Locator("div[role='dialog']");
                var search = modal.GetByPlaceholder(CarrierSearchPlaceholder);
                await search.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 8_000 });
                await search.FillAsync(searchTerm);
                await page.WaitForTimeoutAsync(1_200);

                var option = modal.Locator("li").First;
                if (await option.CountAsync() == 0)
                {
                    _bus.Log($"  '{carrier}' not found in 17track's carrier catalogue for {trackingNumber} - left unresolved.");
                    await page.Keyboard.PressAsync("Escape");
                    continue;
                }

                await option.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                // The row starts re-querying the carrier's site the moment the picker closes.
                await page.WaitForTimeoutAsync(2_500);
            }
            catch (Exception ex)
            {
                _bus.Log($"  Could not set carrier for {trackingNumber}: {ex.Message}");
                try { await page.Keyboard.PressAsync("Escape"); } catch { /* best effort */ }
            }
        }
    }

    async Task OpenDeliveredTabAsync(IPage page)
    {
        var tab = page.GetByText("Teslim edildi", new PageGetByTextOptions { Exact = false }).First;
        await tab.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await tab.ClickAsync();
        await page.WaitForTimeoutAsync(1_500);
    }

    async Task<List<Track17DeliveredRow>> ReadDeliveredRowsAsync(
        IPage page, IReadOnlyDictionary<string, IReadOnlyList<Track17Row>> byTracking)
    {
        var cards = ResultCards(page);
        var count = await cards.CountAsync();
        var result = new List<Track17DeliveredRow>();

        for (var i = 0; i < count; i++)
        {
            try
            {
                var card = cards.Nth(i);
                var trackingNumber = (await card.Locator("span[title]").First.GetAttributeAsync("title"))?.Trim();

                if (string.IsNullOrEmpty(trackingNumber) || !byTracking.TryGetValue(trackingNumber, out var sourceRows))
                    continue;

                var text = await card.InnerTextAsync();
                var statusLine = ExtractStatusLine(text);
                var deliveredOn = ExtractDeliveredOn(text);

                foreach (var row in sourceRows)
                    result.Add(new Track17DeliveredRow(row.OrderNumber, trackingNumber, row.Carrier, statusLine, null, deliveredOn));
            }
            catch (Exception ex)
            {
                _bus.Log($"  Could not read one delivered row: {ex.Message}");
            }
        }

        return result;
    }

    static ILocator ResultCards(IPage page) =>
        page.Locator("div.rounded-xl.shadow-none").Filter(new LocatorFilterOptions { Has = page.Locator("span[title]") });

    /// <summary>The tracking number is all-digit (enforced by <c>TabularFile.ReadTracking</c> before
    /// it ever reaches this runner), so it is safe to interpolate directly into a CSS attribute
    /// selector without escaping.</summary>
    static ILocator ResultCardFor(IPage page, string trackingNumber) =>
        page.Locator($"div.rounded-xl.shadow-none:has(span[title='{trackingNumber}'])").First;

    static string ExtractStatusLine(string cardText)
    {
        foreach (var line in cardText.Split('\n'))
        {
            if (line.Contains("Teslim edildi", StringComparison.OrdinalIgnoreCase))
                return line.Trim();
        }
        return "Teslim edildi";
    }

    static readonly Regex DeliveredOnPattern = new(
        @"Time of delivery:\s*[\r\n]*\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);

    /// <summary>
    /// "Time of delivery: " and its date are two separate flex-item spans in the card markup
    /// (<c>&lt;span&gt;Time of delivery: &lt;/span&gt;&lt;span&gt;2026-08-11&lt;/span&gt;</c>), and
    /// Chromium's <c>innerText</c> puts a line break between flex-item text nodes even though they
    /// render on one visual line — so the date has to be found by pattern, not by taking whatever
    /// follows the label up to the next newline.
    /// </summary>
    static string? ExtractDeliveredOn(string cardText)
    {
        var match = DeliveredOnPattern.Match(cardText);
        return match.Success ? match.Groups[1].Value : null;
    }

    static List<List<string>> Chunk(IReadOnlyList<string> items, int size)
    {
        var chunks = new List<List<string>>();
        for (var i = 0; i < items.Count; i += size)
            chunks.Add([.. items.Skip(i).Take(size)]);
        return chunks;
    }
}
