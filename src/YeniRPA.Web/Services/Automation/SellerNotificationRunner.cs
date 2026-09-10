using Microsoft.Playwright;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Sends one message through Mirakl's own order-conversation dialog per order, one order at a
/// time, streaming progress to the browser through <see cref="AutomationJobBus"/>.
///
/// <para>Ported from the RPA project's <c>RpaService.SendSellerNotificationAsync</c> /
/// <c>FillConversationTopicAsync</c> / <c>SelectComboboxOptionAsync</c> — those selectors already
/// work against the live Mirakl UI, so they are carried over as-is. The one behavioural change from
/// the source feature: the old app let an operator pick one of two fixed Mirakl topics
/// ("Return the order") or fall back to "Other reason" for a free-text one. This version always
/// selects "Other reason" and always fills the free-text field — the topic Mirakl shows the seller
/// is whatever the operator typed into the template, never a value this class has to keep in sync
/// with Mirakl's own dropdown wording.</para>
/// </summary>
public sealed class SellerNotificationRunner
{
    public const string ModuleName = "seller-notification";

    /// <summary>Same reasoning and the same figure as <see cref="MarkAsReceivedRunner.MaxOrdersPerRun"/>.</summary>
    public const int MaxOrdersPerRun = 500;

    /// <summary>The only option ever selected in Mirakl's Topic combobox — see the class doc.</summary>
    const string MiraklOtherReasonTopic = "Other reason";

    readonly AutomationJobBus _bus;
    readonly MiraklBrowser _browser;
    readonly ILogger<SellerNotificationRunner> _logger;

    public SellerNotificationRunner(AutomationJobBus bus, MiraklBrowser browser, ILogger<SellerNotificationRunner> logger)
    {
        _bus = bus;
        _browser = browser;
        _logger = logger;
    }

    /// <summary>
    /// Claims the run slot and starts the batch in the background. False when another automation run
    /// already holds the slot — the caller turns that into the operator-facing error.
    /// </summary>
    public bool TryStart(IReadOnlyList<string> orderIds, string topic, string message)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (!_bus.TryBeginRun(ModuleName))
            return false;

        // Deliberately not awaited: the POST returns as soon as the batch is accepted, and progress
        // reaches the browser over the event stream instead of over this request.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(orderIds, topic, message);
            }
            catch (Exception ex)
            {
                // Everything a single order can throw is already handled per row, so reaching here
                // means the browser or the session failed and no order can succeed.
                _logger.LogError(ex, "Seller Notification run failed before it could process any order.");
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

    async Task RunAsync(IReadOnlyList<string> orderIds, string topic, string message)
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
                await ProcessOrderAsync(page, orderId, topic, message);
                processed++;
                _bus.Log($"Done: {orderId}");
            }
            catch (Exception ex)
            {
                failed.Add(orderId);
                _logger.LogWarning(ex, "Seller Notification failed for order {OrderId}.", orderId);

                var screenshotPath = await AutomationArtifacts.TryCaptureFailureScreenshotAsync(
                    _bus, page, ModuleName, orderId);
                var suffix = screenshotPath is null ? string.Empty : $" | screenshot: {screenshotPath}";
                _bus.Log($"Failed: {orderId} - {ex.Message}{suffix}");
            }

            _bus.Progress(processed + failed.Count, orderIds.Count);
        }

        _bus.Done(processed, failed);
    }

    async Task ProcessOrderAsync(IPage page, string orderId, string topic, string message)
    {
        _bus.Log($"  [{orderId}] Open message page");
        await page.GotoAsync(
            $"{MiraklBrowser.OrdersBaseUrl}/{orderId}/message",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.WaitForTimeoutAsync(1500);

        var startConversationButton = page.GetByRole(AriaRole.Button, new() { Name = "Start conversation", Exact = true }).First;
        await startConversationButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await startConversationButton.ClickAsync();

        var dialog = page.Locator("xpath=//*[.//*[normalize-space()='Send a message'] and .//button[normalize-space()='Send']]").Last;
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        await SelectTopicAsync(page, dialog, orderId);
        await FillFreeTopicAsync(page, dialog, orderId, topic);

        _bus.Log($"  [{orderId}] Fill message");
        var messageInput = dialog.Locator("textarea").First;
        await messageInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await messageInput.ClickAsync();
        await messageInput.FillAsync(message);

        _bus.Log($"  [{orderId}] Send message");
        var sendButton = dialog.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }).First;
        await sendButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await sendButton.ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
        await page.WaitForTimeoutAsync(1000);
    }

    async Task SelectTopicAsync(IPage page, ILocator dialog, string orderId)
    {
        _bus.Log($"  [{orderId}] Select topic: {MiraklOtherReasonTopic}");

        var combobox = dialog.Locator("xpath=.//label[contains(normalize-space(),'Topic')]/following::*[@role='combobox'][1]").First;
        await combobox.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await combobox.ScrollIntoViewIfNeededAsync();
        await combobox.ClickAsync();

        var expanded = await combobox.GetAttributeAsync("aria-expanded");
        if (!string.Equals(expanded, "true", StringComparison.OrdinalIgnoreCase))
        {
            await combobox.FocusAsync();
            await combobox.PressAsync("ArrowDown");
        }

        var option = page.GetByRole(AriaRole.Option, new() { Name = MiraklOtherReasonTopic, Exact = true }).First;
        await option.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await option.ScrollIntoViewIfNeededAsync();
        await option.ClickAsync();
        await page.WaitForTimeoutAsync(500);
    }

    async Task FillFreeTopicAsync(IPage page, ILocator dialog, string orderId, string topic)
    {
        _bus.Log($"  [{orderId}] Fill free topic");

        var topicInput = dialog.Locator("input[placeholder*='topic' i]").First;
        await topicInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await topicInput.ScrollIntoViewIfNeededAsync();
        await topicInput.ClickAsync(new LocatorClickOptions { ClickCount = 3 });
        await topicInput.FillAsync(topic);
        await page.WaitForTimeoutAsync(300);
    }
}
