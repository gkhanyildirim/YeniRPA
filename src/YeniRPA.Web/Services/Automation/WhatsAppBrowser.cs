using Microsoft.Playwright;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Owns the Chrome profile the WhatsApp Web automation signs in with.
///
/// <para><b>Why this is a separate class from <see cref="MiraklBrowser"/> and must stay one.</b>
/// Four things differ, and a shared base would have to be parameterised on all four:</para>
/// <list type="table">
///   <item><description>Persistence: storage state in an encrypted file vs. a persistent profile directory.</description></item>
///   <item><description>Context lifetime: a fresh context per run vs. one shared context for the process.</description></item>
///   <item><description><c>SlowMo</c>: 300 (load-bearing there) vs. 0 (load-bearing here).</description></item>
///   <item><description>Sign-out: delete a file vs. close the browser and delete a locked directory.</description></item>
/// </list>
///
/// <para><b>The trap that makes the difference non-negotiable:</b> WhatsApp Web keeps its
/// authentication material in <b>IndexedDB</b>, which <c>IBrowserContext.StorageStateAsync()</c> does
/// not capture — it captures cookies and localStorage. The vicious part is that it does not fail. It
/// writes a perfectly valid state file, a "session saved" badge goes green, and then every single run
/// lands on a QR code. The symptom presents as "the session keeps expiring", which sends the
/// maintainer looking in entirely the wrong place. A persistent user-data directory is the only thing
/// that actually keeps a WhatsApp Web login.</para>
///
/// <para><b>Encryption at rest, stated honestly.</b> Unlike the Mirakl session, this app does not
/// encrypt the profile; Chrome encrypts its own credential material with DPAPI bound to the Windows
/// account, and that is what protects it. The residual risk is real: anything running as that Windows
/// user can drive this profile, and a live WhatsApp session grants read and write access to
/// <em>every</em> chat the operator is in, not just the seller groups. Treat the directory as a
/// credential and clear it when the machine changes hands.</para>
/// </summary>
public sealed class WhatsAppBrowser : IAsyncDisposable
{
    const string DeploymentMessage =
        "Playwright runtime files could not be found. Deploy the whole build or publish folder, not just the executable.";
    const string BrowserInstallMessage =
        "Install the browser runtime by running `pwsh .\\playwright.ps1 install chromium` from the app folder.";

    /// <summary>
    /// Below roughly <see cref="MinUsableWidth"/> WhatsApp Web collapses to a single-pane layout in
    /// which the chat list and the open conversation do not coexist — the search box then simply is not
    /// on the page, and the run fails with a selector timeout that looks nothing like the real cause.
    ///
    /// <para>Set through Chrome's own <c>--window-size</c> rather than Playwright's viewport emulation,
    /// paired with <c>NoViewport</c> so the page uses the real window's inner size. A persistent profile
    /// restores the window geometry it was last closed at, which is how a run ends up in a narrow
    /// window nobody chose.</para>
    /// </summary>
    const int WindowWidth = 1440;
    const int WindowHeight = 960;

    /// <summary>The width below which the layout is not usable for automation.</summary>
    public const int MinUsableWidth = 1000;

    readonly AutomationJobBus _bus;
    readonly ILogger<WhatsAppBrowser> _logger;

    /// <summary>A Chrome user-data directory can be held by exactly one browser process; a second
    /// launch on the same directory fails or pops a "profile in use" dialog.</summary>
    readonly SemaphoreSlim _launchGate = new(1, 1);

    IPlaywright? _playwright;
    IBrowserContext? _context;

    public WhatsAppBrowser(AutomationJobBus bus, ILogger<WhatsAppBrowser> logger)
    {
        ArgumentNullException.ThrowIfNull(bus);

        _bus = bus;
        _logger = logger;

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA",
            "WhatsApp");
        Directory.CreateDirectory(directory);

        // Sibling of seller-groups.json, so one folder holds everything this module owns.
        ProfilePath = Path.Combine(directory, "profile");
    }

    public string ProfilePath { get; }

    /// <summary>A profile directory that exists and is not empty. Not proof of a live session — that
    /// is what <see cref="CheckSignedInAsync"/> is for.</summary>
    public bool HasProfile =>
        Directory.Exists(ProfilePath) && Directory.EnumerateFileSystemEntries(ProfilePath).Any();

    public bool IsBrowserReady => _context is not null;

    /// <summary>What the last probe found, so the panel can show a state without launching Chrome.</summary>
    public bool? LastKnownSignedIn { get; private set; }

    public DateTimeOffset? LastCheckedUtc { get; private set; }

    /// <summary>Opens the window on WhatsApp Web so the operator can scan the QR code. The profile
    /// persists the login by itself — there is deliberately no "save session" step to click.</summary>
    public async Task OpenLoginAsync()
    {
        var page = await EnsureAppPageAsync();
        await page.BringToFrontAsync();
    }

    /// <summary>Launches Chrome if needed and returns a page parked on WhatsApp Web.</summary>
    public async Task<IPage> EnsureAppPageAsync()
    {
        var context = await EnsureContextAsync();

        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

        if (!page.Url.StartsWith(WhatsAppSelectors.AppUrl, StringComparison.OrdinalIgnoreCase))
        {
            await page.GotoAsync(WhatsAppSelectors.AppUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        }

        // One place to set it, so no individual call has to carry a timeout.
        page.SetDefaultTimeout(10_000);
        return page;
    }

    /// <summary>
    /// Probes for the chat list, then for the QR screen. Returns false rather than throwing when
    /// neither appears — "we could not tell" and "signed out" are both reasons not to start a run.
    /// </summary>
    public async Task<bool> CheckSignedInAsync(int timeoutMs = 20_000)
    {
        var page = await EnsureAppPageAsync();

        var signedIn = await WhatsAppSelectors.AnyVisibleAsync(page, WhatsAppSelectors.SignedInMarkers, timeoutMs);
        if (!signedIn)
        {
            // Only asked when the first probe failed: the QR screen and the chat list never coexist,
            // and this distinguishes "signed out" from "still loading".
            var signedOut = await WhatsAppSelectors.AnyVisibleAsync(page, WhatsAppSelectors.SignedOutMarkers, 4_000);
            _bus.Log(signedOut
                ? "WhatsApp Web is showing the QR code — not signed in."
                : "WhatsApp Web showed neither the chat list nor the QR code. It may still be loading.");
        }

        LastKnownSignedIn = signedIn;
        LastCheckedUtc = DateTimeOffset.UtcNow;
        return signedIn;
    }

    public async Task<IBrowserContext> EnsureContextAsync()
    {
        if (_context is not null)
            return _context;

        await _launchGate.WaitAsync();
        try
        {
            if (_context is not null)
                return _context;

            EnsurePlaywrightRuntimeFilesPresent();
            _playwright ??= await CreatePlaywrightAsync();
            _context = await LaunchPersistentAsync(_playwright);

            // A window closed by hand must not leave a dead context behind that every later call
            // then fails against.
            _context.Close += (_, _) => _context = null;

            return _context;
        }
        finally
        {
            _launchGate.Release();
        }
    }

    async Task<IBrowserContext> LaunchPersistentAsync(IPlaywright playwright)
    {
        Directory.CreateDirectory(ProfilePath);

        // Headless is not an option: the QR has to be scannable, and more importantly a visible window
        // is the operator's only way to watch automation touching their own WhatsApp account.
        //
        // SlowMo stays 0. MiraklBrowser uses 300 because the Mirakl form re-renders as each field is
        // filled; that reasoning does not transfer, and SlowMo multiplied across PressSequentially's
        // per-character actions would make a 400-character message take minutes. The runner uses
        // PressSequentially's own Delay instead.
        //
        // No stealth flags. If WhatsApp objects to automated clients then this tool is on the wrong
        // side of that either way, and evasion turns a visible block into a silent shadow-limit — a far
        // worse failure mode for something that sends outward-facing messages.
        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = false,
            SlowMo = 0,
            // NoViewport hands the page the real window's inner size instead of emulating one, so the
            // --window-size below is what WhatsApp's responsive layout actually reacts to.
            ViewportSize = ViewportSize.NoViewport,
            Args = [$"--window-size={WindowWidth},{WindowHeight}", "--window-position=40,40"]
        };

        try
        {
            // Installed Google Chrome first, matching MiraklBrowser. The separate user-data directory
            // means this never touches the operator's own Chrome profile.
            return await playwright.Chromium.LaunchPersistentContextAsync(ProfilePath,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = options.Headless,
                    SlowMo = options.SlowMo,
                    ViewportSize = options.ViewportSize,
                    Args = options.Args,
                    Channel = "chrome"
                });
        }
        catch (PlaywrightException chromeException)
        {
            _bus.Log($"Chrome could not be launched, falling back to bundled Chromium. {chromeException.Message}");

            try
            {
                return await playwright.Chromium.LaunchPersistentContextAsync(ProfilePath, options);
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
    /// Signs out by deleting the profile. Chrome holds <c>SingletonLock</c>, <c>lockfile</c> and open
    /// handles under <c>Default/</c>, so a straight recursive delete on a live profile throws — and
    /// Windows releases the handles asynchronously after the child process exits, which is why the
    /// first attempt often loses a race the second wins.
    /// </summary>
    public async Task<string> ClearSessionAsync()
    {
        await CloseContextAsync();

        if (!Directory.Exists(ProfilePath))
        {
            LastKnownSignedIn = null;
            LastCheckedUtc = null;
            return "There was no saved WhatsApp profile to clear.";
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Directory.Delete(ProfilePath, recursive: true);
                LastKnownSignedIn = null;
                LastCheckedUtc = null;
                return "The WhatsApp profile was deleted. The next login starts from a QR code.";
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(500);
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                await Task.Delay(500);
            }
        }

        // Never leave a half-deleted profile behind: a partially wiped Chrome profile produces bizarre
        // failures a long way downstream. Move it aside whole and start clean instead.
        var stalePath = $"{ProfilePath}.stale-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        try
        {
            Directory.Move(ProfilePath, stalePath);
            Directory.CreateDirectory(ProfilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The WhatsApp profile could neither be deleted nor renamed.");
            throw new InvalidOperationException(
                "The WhatsApp profile is locked and could not be removed. Close any leftover Chrome window " +
                $"using it, then try again. Profile: {ProfilePath}", ex);
        }

        LastKnownSignedIn = null;
        LastCheckedUtc = null;
        return "The profile was in use, so it was moved aside to " + Path.GetFileName(stalePath) +
               " and a clean one created. You are signed out. Delete the old folder when convenient — " +
               "a leftover Chrome window from a crashed run may still be holding it.";
    }

    /// <summary>
    /// Publishing only the executable leaves Playwright without its driver, which otherwise fails deep
    /// inside the first call with an unhelpful message.
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

    static async Task<IPlaywright> CreatePlaywrightAsync()
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

    async Task CloseContextAsync()
    {
        if (_context is null)
            return;

        try { await _context.CloseAsync(); } catch { /* window already gone */ }
        _context = null;

        // Chrome exits a moment after the context closes; the delete that follows needs it gone.
        await Task.Delay(600);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseContextAsync();
        _playwright?.Dispose();
        _launchGate.Dispose();
    }
}
