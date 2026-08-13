using Microsoft.Playwright;

namespace YeniRPA.Web.Services.Automation;

/// <summary>One line of the uploaded workbook: the order to return, and the parcel coming back.</summary>
public sealed record ReturnRow(string OrderId, string TrackingNumber);

/// <summary>
/// Creates a return on the Mirakl back office for every row of the uploaded workbook, one order at
/// a time, streaming progress to the browser through <see cref="AutomationJobBus"/>.
///
/// A verbatim port of <c>RpaService.CreateReturnsAsync</c> from the RPA project: same page, same
/// field order, same fixed values. A failing order is screenshotted and skipped rather than
/// aborting the run, because a batch is usually mostly good rows.
/// </summary>
public sealed class CreateReturnRunner
{
    public const string ModuleName = "create-return";

    // ---------------------------------------------------------------------------
    // What the form is filled in with. Every return currently goes in as the same kind of return —
    // these are the values to change when the module is tailored further.
    // ---------------------------------------------------------------------------

    const string ReturnMethod = "By mail";
    const string ReturnReason = "Other reason";
    const string Carrier = "Other";
    const string CarrierName = "Yurtici";
    const string TrackingUrlFormat = "https://www.yurticikargo.com/tr/online-servisler/gonderi-sorgula?code={0}";

    const int Quantity = 1;

    readonly AutomationJobBus _bus;
    readonly MiraklBrowser _browser;
    readonly ILogger<CreateReturnRunner> _logger;

    public CreateReturnRunner(AutomationJobBus bus, MiraklBrowser browser, ILogger<CreateReturnRunner> logger)
    {
        _bus = bus;
        _browser = browser;
        _logger = logger;
    }

    /// <summary>
    /// Claims the run slot and starts the batch in the background. False when another automation run
    /// already holds the slot — the caller turns that into the operator-facing error.
    /// </summary>
    public bool TryStart(IReadOnlyList<ReturnRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (!_bus.TryBeginRun(ModuleName))
            return false;

        // Deliberately not awaited: the POST returns as soon as the batch is accepted, and progress
        // reaches the browser over the event stream instead of over this request.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(rows);
            }
            catch (Exception ex)
            {
                // Everything a single order can throw is already handled per row, so reaching here
                // means the browser or the session failed and no row can succeed.
                _logger.LogError(ex, "Create Return run failed before it could process any order.");
                _bus.Log($"Fatal error: {ex.Message}");
                _bus.Done(0, [.. rows.Select(row => row.OrderId)]);
            }
            finally
            {
                _bus.EndRun();
            }
        });

        return true;
    }

    async Task RunAsync(IReadOnlyList<ReturnRow> rows)
    {
        _bus.Started(ModuleName, rows.Count);
        _bus.Log($"Starting {rows.Count} return(s).");

        var browser = await _browser.EnsureBrowserAsync();
        await using var context = await _browser.CreateAuthContextAsync(browser);

        var page = await context.NewPageAsync();
        var processed = 0;
        var failed = new List<string>();

        foreach (var row in rows)
        {
            try
            {
                await CreateReturnAsync(page, row);
                processed++;
                _bus.Log($"Done: {row.OrderId}");
            }
            catch (Exception ex)
            {
                failed.Add(row.OrderId);
                _logger.LogWarning(ex, "Create Return failed for order {OrderId}.", row.OrderId);

                var screenshotPath = await TryCaptureFailureScreenshotAsync(page, row.OrderId);
                var suffix = screenshotPath is null ? string.Empty : $" | screenshot: {screenshotPath}";
                _bus.Log($"Failed: {row.OrderId} - {ex.Message}{suffix}");
            }

            _bus.Progress(processed + failed.Count, rows.Count);
        }

        _bus.Done(processed, failed);
    }

    async Task CreateReturnAsync(IPage page, ReturnRow row)
    {
        _bus.Log($"  [{row.OrderId}] Open create return page");
        await page.GotoAsync(
            $"{MiraklBrowser.OrdersBaseUrl}/{row.OrderId}/create-return",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await WaitForFormAsync(page);

        await SetQuantityAsync(page, row.OrderId);
        await SelectComboboxOptionAsync(page, ReturnMethodCombobox(page), ReturnMethod, row.OrderId, "return method");
        await SelectReturnReasonAsync(page, row.OrderId, ReturnReason);
        await SelectComboboxOptionAsync(page, CarrierCombobox(page), Carrier, row.OrderId, "carrier");
        await FillInputAfterLabelAsync(page, "Name", CarrierName, row.OrderId);
        await FillInputAfterLabelAsync(page, "Tracking number", row.TrackingNumber, row.OrderId);
        await FillInputAfterLabelAsync(
            page,
            "Tracking address",
            string.Format(TrackingUrlFormat, row.TrackingNumber),
            row.OrderId,
            required: false);

        _bus.Log($"  [{row.OrderId}] Click Create");
        var createButton = page.GetByRole(AriaRole.Button, new() { Name = "Create", Exact = true }).First;
        await createButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await createButton.ScrollIntoViewIfNeededAsync();
        await createButton.ClickAsync();

        // The page carries a second "Create" button once the confirmation opens, so the dialog is
        // matched on its whole shape (title + Cancel + Create) rather than on the button alone.
        var confirmDialog = page
            .Locator("xpath=//*[.//*[normalize-space()='Create return'] and .//button[normalize-space()='Cancel'] and .//button[normalize-space()='Create']]")
            .Last;
        var confirmCreateButton = confirmDialog.GetByRole(AriaRole.Button, new() { Name = "Create", Exact = true }).First;
        await confirmCreateButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        _bus.Log($"  [{row.OrderId}] Confirm Create in dialog");
        await confirmCreateButton.ScrollIntoViewIfNeededAsync();
        await confirmCreateButton.ClickAsync(new LocatorClickOptions { Force = true });

        await page.WaitForTimeoutAsync(1000);
    }

    // ---------------------------------------------------------------------------
    // Form helpers. The Mirakl form has no stable ids or test hooks, so every field is found
    // through its visible label — which is also why the label text is the thing that breaks when
    // the platform changes its UI.
    // ---------------------------------------------------------------------------

    static async Task WaitForFormAsync(IPage page)
    {
        await QuantityInput(page).WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });
        await page.WaitForTimeoutAsync(1000);
    }

    async Task SetQuantityAsync(IPage page, string orderId)
    {
        _bus.Log($"  [{orderId}] Set quantity to {Quantity}");

        var quantityInput = QuantityInput(page);
        await quantityInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await quantityInput.ScrollIntoViewIfNeededAsync();
        await quantityInput.ClickAsync(new LocatorClickOptions { ClickCount = 3 });
        await quantityInput.FillAsync(Quantity.ToString());
        await quantityInput.PressAsync("Tab");
        await page.WaitForTimeoutAsync(400);
    }

    async Task SelectComboboxOptionAsync(IPage page, ILocator combobox, string optionText, string orderId, string fieldName)
    {
        _bus.Log($"  [{orderId}] Select {fieldName}: {optionText}");

        await combobox.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await combobox.ScrollIntoViewIfNeededAsync();
        await combobox.ClickAsync();

        // Some of these comboboxes swallow the opening click, so the keyboard is used as a fallback.
        var expanded = await combobox.GetAttributeAsync("aria-expanded");
        if (!string.Equals(expanded, "true", StringComparison.OrdinalIgnoreCase))
        {
            await combobox.FocusAsync();
            await combobox.PressAsync("ArrowDown");
        }

        var option = page.GetByRole(AriaRole.Option, new() { Name = optionText, Exact = true }).First;
        await option.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await option.ScrollIntoViewIfNeededAsync();
        await option.ClickAsync();
        await page.WaitForTimeoutAsync(500);
    }

    async Task SelectReturnReasonAsync(IPage page, string orderId, string reasonText)
    {
        _bus.Log($"  [{orderId}] Select reason: {reasonText}");

        var selectButton = page.GetByRole(AriaRole.Button, new() { Name = "Select", Exact = true }).First;
        await selectButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await selectButton.ScrollIntoViewIfNeededAsync();
        await selectButton.ClickAsync();

        // The reason picker is a radio list on most orders, but renders as plain rows on some.
        var reasonRadio = page.GetByRole(AriaRole.Radio, new() { Name = reasonText, Exact = true }).First;
        if (await reasonRadio.CountAsync() > 0)
        {
            await reasonRadio.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
            await reasonRadio.ClickAsync();
        }
        else
        {
            var reasonTextLocator = page.Locator($"text={reasonText}").First;
            await reasonTextLocator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
            await reasonTextLocator.ClickAsync();
        }

        var doneButton = page.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true }).First;
        await doneButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await doneButton.ClickAsync();
        await page.WaitForTimeoutAsync(500);
    }

    async Task FillInputAfterLabelAsync(IPage page, string labelText, string value, string orderId, bool required = true)
    {
        _bus.Log($"  [{orderId}] Fill {labelText}");

        var input = page.Locator($"xpath=//label[contains(normalize-space(),'{labelText}')]/following::input[1]").First;

        // "Tracking address" is not rendered for every carrier, so its absence is not a failure.
        if (!required && await input.CountAsync() == 0)
            return;

        await input.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await input.ScrollIntoViewIfNeededAsync();
        await input.ClickAsync(new LocatorClickOptions { ClickCount = 3 });
        await input.FillAsync(value);
        await page.WaitForTimeoutAsync(300);
    }

    static ILocator ReturnMethodCombobox(IPage page) =>
        page.Locator("xpath=//label[contains(normalize-space(),'Return method')]/following::*[@role='combobox'][1]").First;

    static ILocator CarrierCombobox(IPage page) =>
        page.Locator("xpath=//label[contains(normalize-space(),'Carrier')]/following::*[@role='combobox'][1]").First;

    static ILocator QuantityInput(IPage page) =>
        page.Locator("xpath=//label[contains(normalize-space(),'Quantity')]/following::input[1]").First;

    // ---------------------------------------------------------------------------

    /// <summary>
    /// A screenshot of the page as it was left is usually the only way to tell a missing button from
    /// an expired session, so failing to take one must not itself fail the row.
    /// </summary>
    async Task<string?> TryCaptureFailureScreenshotAsync(IPage page, string orderId)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "artifacts", ModuleName);
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"{SanitizeFileName(orderId)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            return path;
        }
        catch (Exception ex)
        {
            _bus.Log($"Screenshot failed: {orderId} - {ex.Message}");
            return null;
        }
    }

    static string SanitizeFileName(string value)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidChar, '_');

        return value;
    }
}
