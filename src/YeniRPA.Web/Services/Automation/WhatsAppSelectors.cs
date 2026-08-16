using Microsoft.Playwright;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Every CSS selector the WhatsApp Web automation depends on, in one place — this is the file to edit
/// when WhatsApp changes its markup, and nothing else should need touching.
///
/// <para>Each entry is a <b>list of candidates</b> rather than a single selector.
/// <see cref="FirstAvailableAsync"/> tries them in order and takes the first that resolves, so one
/// candidate rotting does not break a run. When they all fail, the exception names what was being
/// looked for and lists every selector tried — the failure message is the repair instruction.</para>
///
/// <para><b>Structure over labels.</b> <c>#main</c>, <c>footer</c> and <c>[contenteditable]</c> are DOM
/// shape. <c>aria-label='Send'</c> and <c>title='Search or start new chat'</c> are <em>localized
/// strings</em> — on a Turkish WhatsApp they read <c>Gönder</c> and <c>Ara veya yeni sohbet başlat</c>.
/// This deployment is Turkish, so label-based selectors are the trap most likely to bite here. They
/// appear only as last-resort fallbacks, spelled both ways.</para>
///
/// <para><b>Never depend on the Send button.</b> Press Enter instead: the send button is the most
/// aria-label-dependent element on the page and it is entirely avoidable.</para>
/// </summary>
public static class WhatsAppSelectors
{
    public const string AppUrl = "https://web.whatsapp.com/";

    /// <summary>The chat list pane only exists once the session is live.</summary>
    public static readonly string[] SignedInMarkers =
    [
        "#pane-side",
        "div[aria-label='Chat list']",
        "div[aria-label='Sohbet listesi']",
    ];

    /// <summary>
    /// The chat list has to be on screen before a search can happen. Checked separately from
    /// <see cref="SignedInMarkers"/> because "signed in" and "the left column is currently visible" are
    /// different claims — a chat opened in a narrow window satisfies the first and not the second.
    /// </summary>
    public static readonly string[] ChatListPane = ["#pane-side", "#side"];

    /// <summary>The QR screen. <c>div[data-ref]</c> is the QR container and has outlived several
    /// rewrites of the markup around it.</summary>
    public static readonly string[] SignedOutMarkers =
    [
        "canvas[aria-label*='Scan']",
        "canvas[aria-label*='Tara']",
        "[data-testid='qrcode']",
        "div[data-ref]",
    ];

    /// <summary>
    /// The chat search box.
    ///
    /// <para><c>data-tab='3'</c> is the search box and <c>data-tab='10'</c> is the message composer, so
    /// the number is what separates the two <c>contenteditable</c> divs on the page. It leads here
    /// <b>without</b> an <c>#side</c> ancestor: that id has been the left column for years but is not
    /// guaranteed to survive a layout rewrite, and requiring it turned a findable box into a timeout.
    /// The <c>#side</c>-scoped form stays as a fallback for the reverse case.</para>
    ///
    /// <para>Nothing here may match inside <c>#main</c>. A generic <c>div[contenteditable='true']</c>
    /// would happily resolve to the message composer of whatever chat is open, and typing a group name
    /// into it is the beginning of a very bad afternoon.</para>
    /// </summary>
    public static readonly string[] SearchBox =
    [
        "div[contenteditable='true'][data-tab='3']",
        "#side div[contenteditable='true'][data-tab='3']",
        "[role='textbox'][aria-label='Search input textbox']",
        "[role='textbox'][aria-label*='Ara']",
        "#side div[contenteditable='true']",
    ];

    public static readonly string[] SearchResultRow =
    [
        "#pane-side [role='listitem']",
        "#pane-side [role='row']",
        "div[aria-label*='earch'] [role='listitem']",
    ];

    /// <summary>
    /// Read the chat name from this element's <c>title</c> attribute, never its text. WhatsApp
    /// truncates long names with a CSS ellipsis (so InnerText returns "MMS x KAFKASDA Ge…") and renders
    /// emoji as <c>&lt;img alt="🚚"&gt;</c> (which InnerText drops entirely). The attribute carries the
    /// full literal name.
    /// </summary>
    public const string ResultTitleInRow = "span[title]";

    public static readonly string[] ConversationPanel = ["#main"];

    /// <summary>
    /// Elements in the open conversation's header that carry a <c>title</c>.
    ///
    /// <para>Read <b>all</b> of them and look for an exact match, rather than taking the first. The
    /// header holds the chat name <em>and</em> a subtitle ("grup bilgisi için buraya tıklayın" /
    /// "click here for group info"), both with a <c>title</c>, and <c>.First</c> picked the subtitle —
    /// which failed the guard on a chat that was in fact the right one. The guard's strength is the
    /// exact string comparison, not which element supplied the string.</para>
    /// </summary>
    public static readonly string[] HeaderTitle =
    [
        "#main header [title]",
        "#main header span[title]",
    ];

    /// <summary>
    /// The compose box. A <c>contenteditable</c> div, not an input — see
    /// <see cref="LateOrderWhatsAppRunner"/> for why that matters.
    /// </summary>
    public static readonly string[] MessageBox =
    [
        "#main footer div[contenteditable='true'][data-tab='10']",
        "#main footer div[contenteditable='true']",
        "#main div[contenteditable='true'][role='textbox']",
    ];

    /// <summary>
    /// Tries each candidate in turn and returns the first that becomes visible. On total failure the
    /// message names <paramref name="what"/> and lists everything tried, so the run log points straight
    /// at the array in this file that needs editing.
    /// </summary>
    public static async Task<ILocator> FirstAvailableAsync(IPage page, string[] candidates, int timeoutMs, string what)
    {
        // Fast pass: an element that is already on the page costs nothing to find, so the common case
        // does not pay the per-candidate wait budget just because the winning selector is third in the
        // list. Only when nothing is present yet does the waiting loop below run.
        foreach (var selector in candidates)
        {
            var ready = page.Locator(selector).First;
            try
            {
                if (await ready.IsVisibleAsync())
                    return ready;
            }
            catch (Exception ex) when (IsCandidateMiss(ex))
            {
            }
        }

        var perCandidate = Math.Max(700, timeoutMs / Math.Max(1, candidates.Length));

        foreach (var selector in candidates)
        {
            var locator = page.Locator(selector).First;
            try
            {
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = perCandidate
                });
                return locator;
            }
            catch (Exception ex) when (IsCandidateMiss(ex))
            {
                // Not this one — try the next.
            }
        }

        throw new InvalidOperationException(
            $"Could not find {what}. Tried: {string.Join(" | ", candidates)}");
    }

    /// <summary>
    /// Whether an exception means "this selector did not match" rather than "the browser is gone".
    ///
    /// <para><b>This version of Microsoft.Playwright has no <c>TimeoutException</c> of its own</b> —
    /// the assembly declares only <c>PlaywrightException</c> and <c>TargetClosedException</c>, and a
    /// locator timeout surfaces as <see cref="System.TimeoutException"/>. Catching
    /// <c>PlaywrightException</c> alone therefore let the very first timeout escape, so the fallback
    /// candidates were never tried and the operator saw a raw Playwright message naming one selector
    /// instead of the list this class exists to produce.</para>
    ///
    /// <para>A closed target is deliberately excluded: a closed browser will not be fixed by the next
    /// selector, and quietly working through the list would turn "the window was closed" into "the
    /// message box could not be found". <c>TargetClosedException</c> is <c>internal</c> in this version
    /// of the package, so it is identified by type name rather than by a <c>catch</c> clause.</para>
    /// </summary>
    static bool IsCandidateMiss(Exception ex)
    {
        if (ex is TimeoutException)
            return true;

        return ex is PlaywrightException && ex.GetType().Name != "TargetClosedException";
    }

    /// <summary>
    /// The same probe, but returning the selector that worked rather than its first match — needed
    /// where <em>every</em> match matters, such as counting how many chats carry the searched name.
    /// </summary>
    public static async Task<string> FirstAvailableSelectorAsync(IPage page, string[] candidates, int timeoutMs, string what)
    {
        var perCandidate = Math.Max(700, timeoutMs / Math.Max(1, candidates.Length));

        foreach (var selector in candidates)
        {
            try
            {
                await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = perCandidate
                });
                return selector;
            }
            catch (Exception ex) when (IsCandidateMiss(ex))
            {
            }
        }

        throw new InvalidOperationException(
            $"Could not find {what}. Tried: {string.Join(" | ", candidates)}");
    }

    /// <summary>True when any candidate is visible within the budget. Used for the signed-in/out probe,
    /// where absence is an answer rather than a failure.</summary>
    public static async Task<bool> AnyVisibleAsync(IPage page, string[] candidates, int timeoutMs)
    {
        try
        {
            await FirstAvailableAsync(page, candidates, timeoutMs, "marker");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
