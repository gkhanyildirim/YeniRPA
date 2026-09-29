using Microsoft.Playwright;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Sends one message through Mirakl's order-conversation dialog on a page the caller already owns.
///
/// <para>Split out of <see cref="SellerNotificationRunner"/> so <see cref="CreateReturnRunner"/> can
/// send the return notification inside its own run: the automation run slot is held by whichever
/// module started, so a second <c>TryStart</c> from there would be refused. Depends only on the bus
/// (for log lines) — it never claims a run slot or opens a browser itself.</para>
///
/// <para>Ported from the RPA project's <c>RpaService.SendSellerNotificationAsync</c> /
/// <c>FillConversationTopicAsync</c> / <c>SelectComboboxOptionAsync</c> — those selectors already
/// work against the live Mirakl UI, so they are carried over as-is. A null free topic picks Mirakl's
/// own "Return / Cancel the order" topic; a value picks "Other reason" and fills it in.</para>
/// </summary>
public sealed class SellerNotificationSender
{
    /// <summary>Mirakl Topic option used by the return and undelivered kinds.</summary>
    const string MiraklReturnTopic = "Return / Cancel the order";

    /// <summary>Mirakl Topic option used by the custom kind, which then needs the free-text topic.</summary>
    const string MiraklOtherReasonTopic = "Other reason";

    readonly AutomationJobBus _bus;

    public SellerNotificationSender(AutomationJobBus bus) => _bus = bus;

    public async Task SendAsync(IPage page, string orderId, string? freeTopic, string message)
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

        await SelectTopicAsync(page, dialog, orderId, freeTopic is null ? MiraklReturnTopic : MiraklOtherReasonTopic);
        if (freeTopic is not null)
            await FillFreeTopicAsync(page, dialog, orderId, freeTopic);

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

    async Task SelectTopicAsync(IPage page, ILocator dialog, string orderId, string topicOption)
    {
        _bus.Log($"  [{orderId}] Select topic: {topicOption}");

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

        var option = page.GetByRole(AriaRole.Option, new() { Name = topicOption, Exact = true }).First;
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
