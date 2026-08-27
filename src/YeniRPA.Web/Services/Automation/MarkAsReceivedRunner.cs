using Microsoft.Playwright;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Clicks every "Mark as received" button on each order's Mirakl page, one order at a time,
/// streaming progress to the browser through <see cref="AutomationJobBus"/>.
///
/// <para>Ported from the RPA project's <c>RpaService.ProcessOrderAsync</c>, with one change: the old
/// version read <c>markButtons.CountAsync()</c> once and then clicked <c>Nth(0)</c>, <c>Nth(1)</c>, …
/// against that fixed count. A click that changes the page — removing or reordering the button, which
/// Mirakl's UI does — shifts what a later <c>Nth(i)</c> resolves to; it only ever worked because most
/// orders have exactly one button. This version re-resolves <c>.First</c> against the live locator and
/// re-checks the count on every loop turn instead, so "click whatever currently matches, repeat until
/// none match" is the actual loop invariant rather than an accidental one.</para>
/// </summary>
public sealed class MarkAsReceivedRunner
{
    public const string ModuleName = "mark-received";

    /// <summary>
    /// A refusal, not a truncation — same reasoning as <c>LateOrderWhatsAppRunner.MaxGroupsPerRun</c>,
    /// but the risk here is a fat-fingered paste (a whole column copied out of a spreadsheet) rather
    /// than an external-party blast radius. 500 orders at Mirakl's own page-load pace is roughly the
    /// size of a batch an operator can plausibly have reviewed before pressing Start.
    /// </summary>
    public const int MaxOrdersPerRun = 500;

    /// <summary>
    /// Escape hatch the old fixed-count loop never needed: re-checking a live locator instead of a
    /// count taken up front means a button that a click never actually removes would otherwise be
    /// clicked forever. This many clicks on the same order is already far past anything real.
    /// </summary>
    const int MaxMarkClicksPerOrder = 20;

    readonly AutomationJobBus _bus;
    readonly MiraklBrowser _browser;
    readonly ILogger<MarkAsReceivedRunner> _logger;

    public MarkAsReceivedRunner(AutomationJobBus bus, MiraklBrowser browser, ILogger<MarkAsReceivedRunner> logger)
    {
        _bus = bus;
        _browser = browser;
        _logger = logger;
    }

    /// <summary>
    /// Claims the run slot and starts the batch in the background. False when another automation run
    /// already holds the slot — the caller turns that into the operator-facing error.
    /// </summary>
    public bool TryStart(IReadOnlyList<string> orderIds)
    {
        ArgumentNullException.ThrowIfNull(orderIds);

        if (!_bus.TryBeginRun(ModuleName))
            return false;

        // Deliberately not awaited: the POST returns as soon as the batch is accepted, and progress
        // reaches the browser over the event stream instead of over this request.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(orderIds);
            }
            catch (Exception ex)
            {
                // Everything a single order can throw is already handled per row, so reaching here
                // means the browser or the session failed and no order can succeed.
                _logger.LogError(ex, "Mark as Received run failed before it could process any order.");
                _bus.Log($"Fatal error: {ex.Message}");
                _bus.Done(0, [.. orderIds]);
            }
            finally
            {
                _bus.EndRun();
            }
        });

        return true;
    }

    async Task RunAsync(IReadOnlyList<string> orderIds)
    {
        _bus.Started(ModuleName, orderIds.Count);
        _bus.Log($"Starting {orderIds.Count} order(s).");

        var browser = await _browser.EnsureBrowserAsync();
        await using var context = await _browser.CreateAuthContextAsync(browser);

        var page = await context.NewPageAsync();
        var processed = 0;
        var failed = new List<string>();

        foreach (var orderId in orderIds)
        {
            try
            {
                await ProcessOrderAsync(page, orderId);
                processed++;
                _bus.Log($"Done: {orderId}");
            }
            catch (Exception ex)
            {
                failed.Add(orderId);
                _logger.LogWarning(ex, "Mark as Received failed for order {OrderId}.", orderId);

                var screenshotPath = await AutomationArtifacts.TryCaptureFailureScreenshotAsync(
                    _bus, page, ModuleName, orderId);
                var suffix = screenshotPath is null ? string.Empty : $" | screenshot: {screenshotPath}";
                _bus.Log($"Failed: {orderId} - {ex.Message}{suffix}");
            }

            _bus.Progress(processed + failed.Count, orderIds.Count);
        }

        _bus.Done(processed, failed);
    }

    async Task ProcessOrderAsync(IPage page, string orderId)
    {
        _bus.Log($"  [{orderId}] Open order page");
        await page.GotoAsync(
            $"{MiraklBrowser.OrdersBaseUrl}/{orderId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var markButtons = page.Locator("button:has-text('Mark as received')");
        await markButtons.First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        var clicked = 0;
        while (await markButtons.CountAsync() > 0)
        {
            if (++clicked > MaxMarkClicksPerOrder)
            {
                throw new InvalidOperationException(
                    $"'Mark as received' is still present after {MaxMarkClicksPerOrder} clicks — the button is " +
                    "probably not being removed by the click, so the loop was stopped rather than clicking forever.");
            }

            // Re-resolved against the live locator every time, not indexed by a position captured
            // before the first click — see the class doc for why that matters.
            var button = markButtons.First;
            await button.ScrollIntoViewIfNeededAsync();
            await button.ClickAsync();

            var confirmButton = page
                .Locator("button:has-text('Confirm'), button:has-text('Yes'), button:has-text('OK')")
                .First;
            if (await confirmButton.IsVisibleAsync())
                await confirmButton.ClickAsync();

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        _bus.Log($"  [{orderId}] Clicked 'Mark as received' {clicked} time(s)");
    }

}
