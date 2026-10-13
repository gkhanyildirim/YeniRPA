using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Services.PaymentCheck;

/// <summary>
/// Walks the real checkout up to the card-entry page and reports whether it opened: account page (is
/// the saved login still good?) → <c>/checkout/summary</c> (tick the terms checkbox, "Devam edin ve
/// ödeyin") → <c>odeme.mediamarkt.com.tr</c>.
///
/// <para>It stops the moment the payment page shows. It never types card data and never presses
/// "Siparişi Onayla", so no order is placed and nothing is charged. The test account's cart has to
/// hold an item; an empty cart is reported as such rather than as a payment failure.</para>
///
/// <para>Once the login is good and the cart is not empty, any step that does not complete counts as
/// the payment page being unreachable. Only a failure of the check itself (browser would not start)
/// is <see cref="PaymentCheckStatus.Error"/>.</para>
/// </summary>
public sealed partial class PaymentCheckRunner(StorefrontBrowser browser)
{
    const string SummaryUrl = "https://www.mediamarkt.com.tr/tr/checkout/summary";
    const string PaymentHost = "odeme.mediamarkt.com.tr";

    static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan PaymentTimeout = TimeSpan.FromSeconds(45);

    // EmptyCartText and ContinueButtonText are handed to Playwright, which rejects any RegexOptions
    // other than IgnoreCase/Multiline/Singleline ("Unsupported RegularExpression flags") — so no
    // CultureInvariant on these two.
    [GeneratedRegex("sepetiniz\\s+boş", RegexOptions.IgnoreCase)]
    private static partial Regex EmptyCartText();

    [GeneratedRegex("devam\\s+edin\\s+ve\\s+öde", RegexOptions.IgnoreCase)]
    private static partial Regex ContinueButtonText();

    [GeneratedRegex("ön\\s+bilgilendirme|sözleşme|cayma|okudum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TermsText();

    [GeneratedRegex("tercih\\s+edilen\\s+adres", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressPreferenceText();

    public async Task<PaymentCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        PaymentCheckResult Done(string status, string? step, string? detail) =>
            new(status, step, detail, stopwatch.ElapsedMilliseconds);

        if (!browser.HasSavedSession)
            return Done(PaymentCheckStatus.NotLoggedIn, "login", "No storefront session is saved. Sign in from the Payment Check panel.");

        try
        {
            var chrome = await browser.EnsureBrowserAsync();
            await using var context = await browser.CreateContextAsync(chrome);
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);

            // 1. Account page: is the saved login still good?
            try
            {
                await page.GotoAsync(StorefrontBrowser.AccountUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                if (!await IsSignedInAsync(page, cancellationToken))
                    return Done(PaymentCheckStatus.NotLoggedIn, "account", "The storefront asked for a login. The saved session has expired.");
            }
            catch (PlaywrightException ex)
            {
                return Done(PaymentCheckStatus.Unreachable, "account", $"The account page did not load: {FirstLine(ex.Message)}");
            }

            // 2. Checkout summary.
            try
            {
                await page.GotoAsync(SummaryUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 15000 });
                }
                catch (TimeoutException)
                {
                    // A chatty page that never idles is still usable.
                }

                if (await IsCartEmptyAsync(page))
                    return Done(PaymentCheckStatus.CartEmpty, "summary", "The cart is empty, so the payment page could not be tested.");
            }
            catch (PlaywrightException ex)
            {
                return Done(PaymentCheckStatus.Unreachable, "summary", $"The checkout summary did not load: {FirstLine(ex.Message)}");
            }

            // 3. Terms checkbox + "Devam edin ve ödeyin".
            try
            {
                await TickTermsAsync(page);
                await ClickContinueAsync(page);
            }
            catch (PlaywrightException ex)
            {
                return Done(PaymentCheckStatus.Unreachable, "summary", $"Could not continue to payment: {FirstLine(ex.Message)} (URL: {page.Url})");
            }

            // 4. The payment page itself.
            try
            {
                await page.WaitForURLAsync(
                    url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Equals(PaymentHost, StringComparison.OrdinalIgnoreCase),
                    new PageWaitForURLOptions { Timeout = (float)PaymentTimeout.TotalMilliseconds, WaitUntil = WaitUntilState.DOMContentLoaded });
            }
            catch (PlaywrightException)
            {
                return Done(PaymentCheckStatus.Unreachable, "payment", $"The payment page did not open. Stayed on: {page.Url}");
            }

            try
            {
                // The card form is what proves the page is usable, not just that the host answered.
                await page.GetByPlaceholder("Kart numarası").Or(page.GetByText("Kart bilgilerinizi giriniz"))
                    .First.WaitForAsync(new LocatorWaitForOptions { Timeout = 20000 });
            }
            catch (PlaywrightException)
            {
                return Done(PaymentCheckStatus.Unreachable, "payment", "The payment address opened but the card form did not appear.");
            }

            return Done(PaymentCheckStatus.Ok, "payment", "The card-entry page opened normally.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Whatever goes wrong inside the check is a result to report, not a 500 for the caller.
            return Done(PaymentCheckStatus.Error, "browser", $"{ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>Waits for whichever shows first: the account landing page or the login form.</summary>
    static async Task<bool> IsSignedInAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + StepTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (page.Url.Contains("/auth/login", StringComparison.OrdinalIgnoreCase)
                || await page.Locator("input[type=password]").CountAsync() > 0)
            {
                return false;
            }

            if (await page.GetByText("Siparişlerim").First.IsVisibleAsync())
                return true;

            await Task.Delay(500, cancellationToken);
        }

        // Neither appeared: treat as not signed in rather than guessing the page is fine.
        return false;
    }

    static async Task<bool> IsCartEmptyAsync(IPage page)
    {
        if (!page.Url.Contains("/checkout/summary", StringComparison.OrdinalIgnoreCase))
        {
            // Redirected away from the summary: an empty cart bounces back to the cart page.
            var text = await page.Locator("body").InnerTextAsync();
            return EmptyCartText().IsMatch(text);
        }

        return await page.GetByText(EmptyCartText()).First.IsVisibleAsync();
    }

    /// <summary>
    /// Ticks the "Ön bilgilendirme formu, Satış Sözleşmesi ve Cayma Hakkını okudum, kabul ediyorum"
    /// checkbox. The summary page also has an unrelated "Tercih edilen adres" checkbox, so each
    /// checkbox is judged by the text of its own label (or direct parent) only — matching on a wider
    /// ancestor would also match the address one, which comes first in the page.
    ///
    /// <para>The checkbox is ticked directly, never by clicking its label: the label text is a link to
    /// the form and would open it.</para>
    /// </summary>
    static async Task TickTermsAsync(IPage page)
    {
        var boxes = page.Locator("input[type=checkbox]");
        await boxes.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });

        ILocator? target = null;
        var count = await boxes.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var box = boxes.Nth(i);
            var ownText = await box.EvaluateAsync<string>(
                "el => ((el.closest('label') ?? el.parentElement)?.innerText ?? '')");

            if (TermsText().IsMatch(ownText) && !AddressPreferenceText().IsMatch(ownText))
            {
                target = box;
                break;
            }
        }

        if (target is null)
            throw new PlaywrightException("The terms checkbox (Ön bilgilendirme formu) could not be found on the summary page.");

        if (await target.IsCheckedAsync())
            return;

        try
        {
            await target.CheckAsync(new LocatorCheckOptions { Timeout = 5000 });
        }
        catch (PlaywrightException)
        {
            // Custom-styled checkboxes hide the real input behind a label.
            await target.CheckAsync(new LocatorCheckOptions { Force = true });
        }
    }

    static async Task ClickContinueAsync(IPage page)
    {
        var button = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = ContinueButtonText() })
            .Or(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { NameRegex = ContinueButtonText() }));

        await button.First.ClickAsync();
    }

    static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
