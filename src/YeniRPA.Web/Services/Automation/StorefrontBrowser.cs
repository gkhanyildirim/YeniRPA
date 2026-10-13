using Microsoft.AspNetCore.DataProtection;
using Microsoft.Playwright;
using System.Security.Cryptography;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Owns the Playwright browser the payment-page check drives against mediamarkt.com.tr, and the saved
/// storefront login it runs under. One browser per target site, like <see cref="MiraklBrowser"/>.
///
/// <para>The storefront account signs in with Google, which cannot be scripted, so the operator signs in
/// once in a visible window and the cookies are saved encrypted (<c>auth.dat</c>, via
/// <see cref="IDataProtector"/>). Every run then seeds a fresh context from them, passed as a string so
/// they never touch disk in the clear. The browser is headed for the same reason as Mirakl's.</para>
/// </summary>
public sealed class StorefrontBrowser : IAsyncDisposable
{
    public const string AccountUrl = "https://www.mediamarkt.com.tr/tr/myaccount";

    const string DeploymentMessage =
        "Playwright runtime files could not be found. Deploy the whole build or publish folder, not just the executable.";
    const string BrowserInstallMessage =
        "Install the browser runtime by running `pwsh .\\playwright.ps1 install chromium` from the app folder.";

    readonly IDataProtector _sessionProtector;
    readonly string _authFilePath;
    readonly SemaphoreSlim _launchGate = new(1, 1);

    IPlaywright? _playwright;
    IBrowser? _browser;
    IBrowserContext? _loginContext;

    public StorefrontBrowser(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        var sessionDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA",
            "Storefront");
        Directory.CreateDirectory(sessionDirectory);

        _authFilePath = Path.Combine(sessionDirectory, "auth.dat");
        _sessionProtector = dataProtectionProvider.CreateProtector("YeniRPA.Storefront.SessionState");
    }

    public bool HasSavedSession => File.Exists(_authFilePath);

    public DateTime? SessionSavedUtc => File.Exists(_authFilePath) ? File.GetLastWriteTimeUtc(_authFilePath) : null;

    /// <summary>Opens a window on the account page so the operator can sign in with Google.</summary>
    public async Task StartLoginAsync()
    {
        await CloseLoginContextAsync();

        var browser = await EnsureBrowserAsync();
        _loginContext = await CreateContextAsync(browser);

        var page = await _loginContext.NewPageAsync();
        await page.GotoAsync(AccountUrl);
    }

    /// <summary>Saves the cookies from the open login window, then closes it.</summary>
    public async Task SaveSessionAsync()
    {
        if (_loginContext is null)
            throw new InvalidOperationException("No login window is open. Click 'Open login window' first.");

        try
        {
            var storageState = await _loginContext.StorageStateAsync();
            await File.WriteAllTextAsync(_authFilePath, _sessionProtector.Protect(storageState));
        }
        finally
        {
            await CloseLoginContextAsync();
        }
    }

    public void ClearSession()
    {
        if (File.Exists(_authFilePath))
            File.Delete(_authFilePath);
    }

    /// <summary>A fresh context carrying the saved login, or a blank one when none is saved.</summary>
    public async Task<IBrowserContext> CreateContextAsync(IBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        var options = new BrowserNewContextOptions
        {
            Locale = "tr-TR",
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            StorageState = await TryReadStorageStateAsync(),
        };

        return await browser.NewContextAsync(options);
    }

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

    static async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        // Headed, and without Chrome's automation banner/flag: Google refuses to sign in inside a
        // browser that announces itself as automated.
        var options = new BrowserTypeLaunchOptions
        {
            Headless = false,
            IgnoreDefaultArgs = ["--enable-automation"],
            Args = ["--disable-blink-features=AutomationControlled"],
        };

        try
        {
            options.Channel = "chrome";
            return await playwright.Chromium.LaunchAsync(options);
        }
        catch (PlaywrightException chromeException)
        {
            options.Channel = null;

            try
            {
                return await playwright.Chromium.LaunchAsync(options);
            }
            catch (PlaywrightException ex)
            {
                throw new InvalidOperationException(
                    $"No compatible Chromium browser could be started ({chromeException.Message}). {BrowserInstallMessage}", ex);
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

    async Task<string?> TryReadStorageStateAsync()
    {
        if (!File.Exists(_authFilePath))
            return null;

        try
        {
            return _sessionProtector.Unprotect(await File.ReadAllTextAsync(_authFilePath));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The saved storefront session could not be read. Clear it and sign in again.", ex);
        }
    }

    async Task CloseLoginContextAsync()
    {
        if (_loginContext is null)
            return;

        try { await _loginContext.CloseAsync(); } catch { /* window already gone */ }
        _loginContext = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseLoginContextAsync();
        try { if (_browser is not null) await _browser.CloseAsync(); } catch { /* already exited */ }
        _playwright?.Dispose();
        _launchGate.Dispose();
    }
}
