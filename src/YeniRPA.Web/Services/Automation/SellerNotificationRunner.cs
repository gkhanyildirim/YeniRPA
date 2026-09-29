using Microsoft.Playwright;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Sends one message through Mirakl's own order-conversation dialog per order, one order at a
/// time, streaming progress to the browser through <see cref="AutomationJobBus"/>.
///
/// <para>The per-order Mirakl dialog steps live in <see cref="SellerNotificationSender"/>, shared with
/// <see cref="CreateReturnRunner"/>. Like the source feature, the return and undelivered kinds use
/// Mirakl's own "Return / Cancel the order" topic and fill no free-text topic; the custom kind uses
/// "Other reason" and fills the operator's own topic.</para>
/// </summary>
public sealed class SellerNotificationRunner
{
    public const string ModuleName = "seller-notification";

    /// <summary>Same reasoning and the same figure as <see cref="MarkAsReceivedRunner.MaxOrdersPerRun"/>.</summary>
    public const int MaxOrdersPerRun = 500;

    readonly AutomationJobBus _bus;
    readonly MiraklBrowser _browser;
    readonly SellerNotificationSender _sender;
    readonly ILogger<SellerNotificationRunner> _logger;

    public SellerNotificationRunner(
        AutomationJobBus bus, MiraklBrowser browser, SellerNotificationSender sender, ILogger<SellerNotificationRunner> logger)
    {
        _bus = bus;
        _browser = browser;
        _sender = sender;
        _logger = logger;
    }

    /// <summary>
    /// Claims the run slot and starts the batch in the background. False when another automation run
    /// already holds the slot — the caller turns that into the operator-facing error.
    /// </summary>
    public bool TryStart(IReadOnlyList<string> orderIds, string kind, string? topic, string message)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var isCustom = SellerNotificationKinds.Normalize(kind) == SellerNotificationKinds.Custom;
        if (isCustom) ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        var freeTopic = isCustom ? topic : null;

        if (!_bus.TryBeginRun(ModuleName))
            return false;

        // Deliberately not awaited: the POST returns as soon as the batch is accepted, and progress
        // reaches the browser over the event stream instead of over this request.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(orderIds, freeTopic, message);
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

    /// <summary>A null <paramref name="freeTopic"/> means the return/undelivered path.</summary>
    async Task RunAsync(IReadOnlyList<string> orderIds, string? freeTopic, string message)
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
                await _sender.SendAsync(page, orderId, freeTopic, message);
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
}
