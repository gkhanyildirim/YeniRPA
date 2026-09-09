using Microsoft.Playwright;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Owns the single Playwright browser <see cref="Track17Runner"/> drives against 17track.net.
///
/// <para>Deliberately much smaller than <see cref="MiraklBrowser"/>: 17track's public tracking tool
/// needs no sign-in, so there is no <c>StorageState</c>, no <see cref="System.Security.Cryptography"/>,
/// no saved-session file.</para>
///
/// <para>Runs <b>headed</b>, not headless. 17track sits behind Cloudflare's bot-check ("Güvenlik
/// doğrulaması yapılıyor" / a Turnstile checkbox) — confirmed by running this module headless against
/// the live site, which was challenged on every request and never reached the search page at all.
/// Headed Chromium with the real "chrome" channel was not challenged in the same testing. This is the
/// same trade-off <see cref="MiraklBrowser"/> and <see cref="WhatsAppBrowser"/> already made for their
/// own reasons (SSO, QR scan) — here the reason is Cloudflare rather than a login screen.</para>
/// </summary>
public sealed class Track17Browser : IAsyncDisposable
{
    const string DeploymentMessage =
        "Playwright runtime files could not be found. Deploy the whole build or publish folder, not just the executable.";
    const string BrowserInstallMessage =
        "Install the browser runtime by running `pwsh .\\playwright.ps1 install chromium` from the app folder.";

    /// <summary>Wide enough that 17track's status-filter tabs render their text labels instead of
    /// collapsing to icon-only — the tabs use a `min-[1860px]:w-auto` breakpoint, below which
    /// "Teslim edildi" and friends have zero rendered width and cannot be clicked by text.</summary>
    const int ViewportWidth = 1920;
    const int ViewportHeight = 1000;

    readonly SemaphoreSlim _launchGate = new(1, 1);
    IPlaywright? _playwright;
    IBrowser? _browser;

    public async Task<IBrowser> EnsureBrowserAsync()
    {
        if (_browser is { IsConnected: true } ready)
            return ready;

        await _launchGate.WaitAsync();
        try
        {
            if (_browser is { IsConnected: true } stillReady)
                return stillReady;

            EnsurePlaywrightRuntimeFilesPresent();
            _playwright ??= await CreatePlaywrightAsync();
            _browser = await LaunchAsync(_playwright);
            return _browser;
        }
        finally
        {
            _launchGate.Release();
        }
    }

    /// <summary>A fresh, blank context — no saved state to seed it with.</summary>
    public Task<IBrowserContext> NewContextAsync(IBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        return browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
            Locale = "tr-TR",
        });
    }

    async Task<IPlaywright> CreatePlaywrightAsync()
    {
        try
        {
            return await Playwright.CreateAsync();
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                $"Playwright could not start on this machine. {DeploymentMessage} {BrowserInstallMessage}", ex);
        }
    }

    async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        // Headed — see the class doc comment for why (Cloudflare challenges the headless path).
        var options = new BrowserTypeLaunchOptions { Headless = false };

        try
        {
            return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = options.Headless,
                Channel = "chrome"
            });
        }
        catch (PlaywrightException chromeException)
        {
            try
            {
                return await playwright.Chromium.LaunchAsync(options);
            }
            catch (PlaywrightException ex)
            {
                throw new InvalidOperationException(
                    $"No compatible Chromium browser could be started ({chromeException.Message}). {BrowserInstallMessage}",
                    ex);
            }
        }
    }

    static void EnsurePlaywrightRuntimeFilesPresent()
    {
        var appDirectory = AppContext.BaseDirectory;

        if (File.Exists(Path.Combine(appDirectory, "playwright.ps1"))
            && Directory.Exists(Path.Combine(appDirectory, ".playwright")))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{DeploymentMessage} Expected `playwright.ps1` and `.playwright` beside the executable.");
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_browser is not null) await _browser.CloseAsync(); } catch { /* already exited */ }
        _playwright?.Dispose();
        _launchGate.Dispose();
    }
}
