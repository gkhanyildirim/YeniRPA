using Microsoft.AspNetCore.DataProtection;
using Microsoft.Playwright;
using System.Security.Cryptography;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Owns the single Playwright browser the automation modules drive, and the saved Mirakl login they
/// run under. Ported from the RPA project's <c>RpaService</c> infrastructure.
///
/// The browser runs <em>headed</em> on purpose: Mirakl signs in through Google SSO, which cannot be
/// scripted, so the operator has to be able to see and answer the login window once. After that the
/// cookies are saved and every run reuses them.
/// </summary>
public sealed class MiraklBrowser : IAsyncDisposable
{
    public const string OrdersBaseUrl = "https://mediamarktsaturn.mirakl.net/mmp/operator/order";

    const string DeploymentMessage =
        "Playwright runtime files could not be found. Deploy the whole build or publish folder, not just the executable.";
    const string BrowserInstallMessage =
        "Install the browser runtime by running `pwsh .\\playwright.ps1 install chromium` from the app folder.";

    readonly AutomationJobBus _bus;
    readonly IDataProtector _sessionProtector;
    readonly string _authFilePath;

    /// <summary>Serialises browser launch so two concurrent callers cannot start two Chromes.</summary>
    readonly SemaphoreSlim _launchGate = new(1, 1);

    IPlaywright? _playwright;
    IBrowser? _browser;
    IBrowserContext? _loginContext;

    public MiraklBrowser(AutomationJobBus bus, IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        _bus = bus;

        var sessionDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA",
            "Mirakl");
        Directory.CreateDirectory(sessionDirectory);

        // Session cookies grant full operator access to the marketplace, so they are never written
        // to disk in the clear.
        _authFilePath = Path.Combine(sessionDirectory, "auth.dat");
        _sessionProtector = dataProtectionProvider.CreateProtector("YeniRPA.Mirakl.SessionState");
    }

    public bool HasSavedSession => File.Exists(_authFilePath);

    /// <summary>True once Chrome is up. It is started on first use, not at app startup — the report
    /// modules must not pay for a browser nobody asked for.</summary>
    public bool IsBrowserReady => _browser is { IsConnected: true };

    /// <summary>Opens a browser window on the Mirakl order list so the operator can sign in.</summary>
    public async Task StartLoginAsync()
    {
        await CloseLoginContextAsync();

        var browser = await EnsureBrowserAsync();
        _loginContext = await CreateAuthContextAsync(browser);

        var page = await _loginContext.NewPageAsync();
        await page.GotoAsync($"{OrdersBaseUrl}/all");
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

    /// <summary>Deletes the saved session.</summary>
    public void ClearSession()
    {
        if (File.Exists(_authFilePath))
            File.Delete(_authFilePath);
    }

    /// <summary>A fresh browser context carrying the saved login, or a blank one when none is saved.</summary>
    public async Task<IBrowserContext> CreateAuthContextAsync(IBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        // Playwright only takes storage state as a file path, so the decrypted cookies land in a
        // temp file that is deleted again as soon as the context has read it.
        var storageStatePath = await CreateStorageStatePathAsync();

        try
        {
            return storageStatePath is null
                ? await browser.NewContextAsync()
                : await browser.NewContextAsync(new BrowserNewContextOptions { StorageStatePath = storageStatePath });
        }
        catch (PlaywrightException ex) when (storageStatePath is not null)
        {
            throw new InvalidOperationException("The saved session is no longer valid. Clear it and sign in again.", ex);
        }
        finally
        {
            if (storageStatePath is not null && File.Exists(storageStatePath))
                File.Delete(storageStatePath);
        }
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

    async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        // SlowMo is not politeness: the Mirakl forms re-render as each field is filled, and the
        // original automation needed the pause between actions to stay reliable.
        var options = new BrowserTypeLaunchOptions { Headless = false, SlowMo = 300 };

        try
        {
            // Installed Google Chrome first — it is what the operator signs into Google SSO with.
            return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = options.Headless,
                SlowMo = options.SlowMo,
                Channel = "chrome"
            });
        }
        catch (PlaywrightException chromeException)
        {
            _bus.Log($"Chrome could not be launched, falling back to bundled Chromium. {chromeException.Message}");

            try
            {
                return await playwright.Chromium.LaunchAsync(options);
            }
            catch (PlaywrightException ex)
            {
                throw new InvalidOperationException(
                    $"No compatible Chromium browser could be started. Install Google Chrome. {BrowserInstallMessage}",
                    ex);
            }
        }
    }

    /// <summary>
    /// Publishing only the executable leaves Playwright without its driver, which otherwise fails
    /// deep inside the first call with an unhelpful message.
    /// </summary>
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

    async Task<string?> CreateStorageStatePathAsync()
    {
        if (!File.Exists(_authFilePath))
            return null;

        string storageState;
        try
        {
            storageState = _sessionProtector.Unprotect(await File.ReadAllTextAsync(_authFilePath));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The saved session could not be read. Clear it and sign in again.", ex);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"yenirpa-auth-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempPath, storageState);
        return tempPath;
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
