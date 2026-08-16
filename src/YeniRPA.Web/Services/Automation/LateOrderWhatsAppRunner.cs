using System.Text;
using Microsoft.Playwright;

namespace YeniRPA.Web.Services.Automation;

/// <summary>One message, addressed to one WhatsApp group. The body is final — it is the exact text the
/// operator read in the preview.</summary>
public sealed record WhatsAppMessage(string GroupName, string SellerId, string SellerName, string Body);

/// <summary>
/// Posts one warning message per seller group by driving WhatsApp Web, streaming progress through
/// <see cref="AutomationJobBus"/> like every other automation module.
///
/// <para>Structurally a sibling of <see cref="CreateReturnRunner"/>, but the blast radius is different:
/// a Mirakl return can be undone by hand, a WhatsApp message cannot be recalled and lands in front of
/// an external party. Three guards exist only because of that, and none of them may be relaxed for
/// convenience:</para>
/// <list type="number">
///   <item><description>A chat is opened only on an <b>exact, case-sensitive</b> title match, and only
///   when exactly one chat carries that title. Never <c>.First</c>.</description></item>
///   <item><description>After the click, the conversation header is read back and compared again —
///   an independent second check, because the result list re-sorts while it loads and the click can
///   land one row over.</description></item>
///   <item><description>The composed text is read back out of the box and compared to the intended
///   body before Enter is pressed. This catches a dropped keystroke, an emoji auto-conversion and a
///   focus steal, all while everything is still reversible.</description></item>
/// </list>
/// </summary>
public sealed class LateOrderWhatsAppRunner
{
    public const string ModuleName = "late-orders";

    /// <summary>
    /// Randomised pause between groups. A fixed interval is the single most machine-legible signal a
    /// client can emit; randomising across a 2x range removes it for free. Six seconds is roughly the
    /// floor for something a human could plausibly be doing — find the group, paste, send.
    /// </summary>
    const int MinDelayBetweenGroupsMs = 6_000;
    const int MaxDelayBetweenGroupsMs = 12_000;

    /// <summary>
    /// A dry run pauses barely at all. The rate limit exists so WhatsApp does not see a machine
    /// blasting an account — a dry run sends nothing, so there is nothing to pace. Keeping the live
    /// delay here would make testing twenty groups a four-minute wait for no protection at all.
    /// </summary>
    const int DryRunDelayBetweenGroupsMs = 700;

    /// <summary>
    /// A refusal, not a truncation: over this the request is rejected so the operator narrows the run
    /// rather than being left believing all sixty messages went out.
    /// </summary>
    public const int MaxGroupsPerRun = 40;

    /// <summary>
    /// Per character in the <em>search box</em> only. The search is debounced and reacts to each
    /// keystroke, so a small gap keeps it reliable; the group name is short enough that this costs
    /// nothing. The message body is typed by a different route entirely — see <c>ComposeAsync</c>.
    /// </summary>
    const int SearchTypingDelayMs = 8;

    /// <summary>Fallback typing speed for the body, used only when the fast path fails verification.</summary>
    const int BodyTypingDelayMs = 4;

    /// <summary>WhatsApp's chat search is debounced and the list re-sorts as results stream in.</summary>
    const int SearchSettleMs = 700;

    public const int MaxMessageChars = 4_000;

    /// <summary>Raised when the WhatsApp session is gone. Aborts the whole run rather than the row.</summary>
    sealed class SessionLostException(string message) : Exception(message);

    readonly AutomationJobBus _bus;
    readonly WhatsAppBrowser _browser;
    readonly ILogger<LateOrderWhatsAppRunner> _logger;

    public LateOrderWhatsAppRunner(AutomationJobBus bus, WhatsAppBrowser browser, ILogger<LateOrderWhatsAppRunner> logger)
    {
        _bus = bus;
        _browser = browser;
        _logger = logger;
    }

    /// <summary>Claims the app-wide run slot and starts the batch in the background. False when another
    /// automation run already holds it.</summary>
    public bool TryStart(IReadOnlyList<WhatsAppMessage> messages, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (!_bus.TryBeginRun(ModuleName))
            return false;

        // Deliberately not awaited: the POST returns as soon as the batch is accepted, and progress
        // reaches the browser over the event stream instead of over this request.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(messages, dryRun);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The WhatsApp run failed before it could send anything.");
                _bus.Log($"Fatal error: {ex.Message}");
                _bus.Done(0, [.. messages.Select(m => m.GroupName)]);
            }
            finally
            {
                _bus.EndRun();
            }
        });

        return true;
    }

    async Task RunAsync(IReadOnlyList<WhatsAppMessage> messages, bool dryRun)
    {
        _bus.Started(ModuleName, messages.Count);
        _bus.Log(dryRun
            ? $"DRY RUN — composing {messages.Count} message(s). Nothing will be sent."
            : $"LIVE — sending {messages.Count} message(s).");

        var page = await _browser.EnsureAppPageAsync();

        if (!await _browser.CheckSignedInAsync())
        {
            _bus.Log("Not signed in to WhatsApp Web. Open the login window and scan the QR code first.");
            _bus.Done(0, [.. messages.Select(m => m.GroupName)]);
            return;
        }

        try
        {
            await PrepareWindowAsync(page);
        }
        catch (Exception ex)
        {
            // Nothing can be searched for without the chat list, so this is a run-level failure rather
            // than a per-group one.
            _bus.Log($"The chat list could not be reached: {ex.Message}");
            _bus.Done(0, [.. messages.Select(m => m.GroupName)]);
            return;
        }

        var processed = 0;
        var failed = new List<string>();

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];

            try
            {
                await SendToGroupAsync(page, message, dryRun);
                processed++;
                _bus.Log(dryRun ? $"Verified: {message.GroupName}" : $"Sent: {message.GroupName}");
            }
            catch (SessionLostException ex)
            {
                // A conscious divergence from CreateReturnRunner's never-abort rule: every remaining
                // group would fail identically, and forty screenshots of the same QR code help nobody
                // while WhatsApp watches a client hammer a signed-out session.
                _bus.Log($"Aborting the run: {ex.Message}");
                failed.AddRange(messages.Skip(i).Select(m => m.GroupName));
                break;
            }
            catch (Exception ex)
            {
                failed.Add(message.GroupName);
                _logger.LogWarning(ex, "WhatsApp send failed for group {GroupName}.", message.GroupName);

                var screenshotPath = await TryCaptureFailureScreenshotAsync(page, message.GroupName);
                var suffix = screenshotPath is null ? string.Empty : $" | screenshot: {screenshotPath}";
                _bus.Log($"Failed: {message.GroupName} - {ex.Message}{suffix}");
            }

            _bus.Progress(processed + failed.Count, messages.Count);

            if (i < messages.Count - 1 && failed.Count + processed < messages.Count)
            {
                var pause = dryRun
                    ? DryRunDelayBetweenGroupsMs
                    : Random.Shared.Next(MinDelayBetweenGroupsMs, MaxDelayBetweenGroupsMs);

                _bus.Log($"  waiting {pause / 1000.0:0.0}s before the next group");
                await Task.Delay(pause);
            }
        }

        _bus.Done(processed, failed);
    }

    /// <summary>
    /// Gets the page into the one state every group step assumes: wide enough to show both panes, with
    /// the chat list visible and no conversation open.
    ///
    /// <para>Both halves are here because of a real failure. A persistent Chrome profile restores the
    /// window size it was last closed at, and below roughly 1000px WhatsApp drops the chat list
    /// entirely — the search box is then simply absent, and the run dies on a selector timeout that
    /// reads like a broken selector rather than a narrow window. A chat left open from a previous run
    /// hides the list the same way. Saying the width out loud turns both into one obvious log line.</para>
    /// </summary>
    async Task PrepareWindowAsync(IPage page)
    {
        var width = 0;
        try { width = await page.EvaluateAsync<int>("() => window.innerWidth"); } catch { /* not fatal */ }

        if (width > 0)
        {
            _bus.Log($"WhatsApp Web window is {width}px wide.");

            if (width < WhatsAppBrowser.MinUsableWidth)
            {
                _bus.Log(
                    $"WARNING: below ~{WhatsAppBrowser.MinUsableWidth}px WhatsApp Web hides the chat list and the " +
                    "search box is not on the page at all. Maximise the WhatsApp window before running.");
            }
        }

        // Closes whatever conversation was left open, so the left column is on screen and the keyboard
        // shortcuts below act on the chat list rather than on a message composer.
        await page.Keyboard.PressAsync("Escape");

        if (await WhatsAppSelectors.AnyVisibleAsync(page, WhatsAppSelectors.ChatListPane, 3_000))
            return;

        _bus.Log("The chat list is not visible — reloading WhatsApp Web to get back to a known state.");
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WhatsAppSelectors.FirstAvailableAsync(page, WhatsAppSelectors.ChatListPane, 30_000, "the chat list");
    }

    async Task SendToGroupAsync(IPage page, WhatsAppMessage message, bool dryRun)
    {
        var target = Normalize(message.GroupName);
        _bus.Log($"  [{message.GroupName}] Search");

        await ClearSearchAsync(page);

        var search = await WhatsAppSelectors.FirstAvailableAsync(
            page, WhatsAppSelectors.SearchBox, 10_000, "the chat search box");
        await search.PressSequentiallyAsync(message.GroupName,
            new LocatorPressSequentiallyOptions { Delay = SearchTypingDelayMs });
        await page.WaitForTimeoutAsync(SearchSettleMs);

        var matches = await FindExactMatchesAsync(page, target);

        if (matches.Count == 0)
        {
            await ThrowIfSignedOutAsync(page);
            throw new InvalidOperationException(
                $"No chat is named exactly '{message.GroupName}'. Check the group name in the mapping table — " +
                "it is compared character for character and nothing is matched approximately.");
        }

        if (matches.Count > 1)
        {
            // Two groups with the same name is a real situation (an old and a new group for one seller),
            // and picking either is a coin flip on where a competitor-visible message lands.
            throw new InvalidOperationException(
                $"{matches.Count} chats are named '{message.GroupName}'. Rename one in WhatsApp so the right " +
                "one can be identified.");
        }

        _bus.Log($"  [{message.GroupName}] Open chat");
        await matches[0].ScrollIntoViewIfNeededAsync();
        await matches[0].ClickAsync();

        await VerifyHeaderAsync(page, message.GroupName, target);

        var box = await ComposeAsync(page, message);

        if (dryRun)
        {
            await ClearBoxAsync(page, box);
            _bus.Log($"  [{message.GroupName}] DRY RUN — composed and verified, not sent");
            return;
        }

        _bus.Log($"  [{message.GroupName}] Send");
        await page.Keyboard.PressAsync("Enter");

        // The box emptying is WhatsApp acknowledging the message, and is the only confirmation
        // available without reading the conversation back.
        try
        {
            await page.WaitForFunctionAsync(
                "el => el.innerText.trim().length === 0",
                await box.ElementHandleAsync(),
                new PageWaitForFunctionOptions { Timeout = 8_000 });
        }
        catch (PlaywrightException)
        {
            throw new InvalidOperationException(
                "Enter was pressed but the message box did not clear, so the message may not have been sent. " +
                "Check the group before re-running this seller.");
        }
    }

    // ---------------------------------------------------------------------------

    /// <summary>
    /// Leftover text from the previous group silently narrows the next search to zero results, and the
    /// failure then reads as "group not found" for a group that exists.
    /// </summary>
    async Task ClearSearchAsync(IPage page)
    {
        await page.Keyboard.PressAsync("Escape");

        var search = await WhatsAppSelectors.FirstAvailableAsync(
            page, WhatsAppSelectors.SearchBox, 10_000, "the chat search box");

        await search.ClickAsync();
        await page.Keyboard.PressAsync("Control+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.WaitForTimeoutAsync(150);
    }

    /// <summary>
    /// Every search result whose title matches exactly. The title comes from the <c>title</c> attribute
    /// rather than the text: WhatsApp truncates long names with a CSS ellipsis and renders emoji as
    /// images, so InnerText both cuts names short and drops characters.
    ///
    /// <para>Comparison is <see cref="StringComparison.Ordinal"/> — exact and case-sensitive. The
    /// Turkish name folding used for seller matching does not belong here: the mapping table is where
    /// the operator pastes a name copied from WhatsApp, and case-insensitivity would let two chats
    /// differing only in case both match.</para>
    /// </summary>
    static async Task<List<ILocator>> FindExactMatchesAsync(IPage page, string target)
    {
        string selector;
        try
        {
            selector = await WhatsAppSelectors.FirstAvailableSelectorAsync(
                page, WhatsAppSelectors.SearchResultRow, 8_000, "the search results");
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        var rows = page.Locator(selector);
        var count = await rows.CountAsync();
        var matches = new List<ILocator>();

        for (var i = 0; i < count; i++)
        {
            var row = rows.Nth(i);
            var titleElement = row.Locator(WhatsAppSelectors.ResultTitleInRow).First;

            if (await titleElement.CountAsync() == 0)
                continue;

            var title = Normalize(await titleElement.GetAttributeAsync("title") ?? "");
            if (string.Equals(title, target, StringComparison.Ordinal))
                matches.Add(row);
        }

        return matches;
    }

    /// <summary>
    /// The second, independent check that guard 2 exists for. The result list re-sorts while results
    /// stream in, so the row under the cursor between the match and the click is not guaranteed to be
    /// the row that matched — the click can land one row over. Nothing is typed until the header agrees.
    /// </summary>
    async Task VerifyHeaderAsync(IPage page, string groupName, string target)
    {
        await WhatsAppSelectors.FirstAvailableAsync(
            page, WhatsAppSelectors.ConversationPanel, 15_000, "the conversation panel");

        // Every titled element in the header, not just the first: the header carries the chat name and
        // a subtitle ("grup bilgisi için buraya tıklayın"), and taking .First matched the subtitle and
        // failed the guard on the correct chat. Requiring the exact name to appear among them is what
        // makes this a guard; which element carries it is not interesting.
        List<string> titles = [];
        for (var attempt = 0; attempt < 6; attempt++)
        {
            titles = await CollectHeaderTitlesAsync(page);
            if (titles.Any(title => string.Equals(title, target, StringComparison.Ordinal)))
            {
                _bus.Log($"  [{groupName}] Confirmed the open chat is '{groupName}'");
                return;
            }

            // The header renders a moment after #main on a slow chat, so a miss is retried briefly
            // before it is believed.
            await page.WaitForTimeoutAsync(400);
        }

        throw new InvalidOperationException(
            $"The open chat's header does not carry '{groupName}'. Found: " +
            (titles.Count == 0 ? "(nothing)" : string.Join(" | ", titles.Distinct())) +
            ". Nothing was typed.");
    }

    static async Task<List<string>> CollectHeaderTitlesAsync(IPage page)
    {
        foreach (var selector in WhatsAppSelectors.HeaderTitle)
        {
            var all = page.Locator(selector);
            int count;
            try { count = await all.CountAsync(); } catch { continue; }

            var titles = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var title = await all.Nth(i).GetAttributeAsync("title");
                if (!string.IsNullOrWhiteSpace(title))
                    titles.Add(Normalize(title));
            }

            if (titles.Count > 0)
                return titles;
        }

        return [];
    }

    /// <summary>
    /// Types the body and verifies it landed. Two traps live in this method.
    ///
    /// <para><b>Enter sends.</b> A <c>\n</c> inside the typed string posts a half-finished message to a
    /// real group, so the body is split on newlines first and the breaks are Shift+Enter.</para>
    ///
    /// <para><b>The box is a contenteditable div, not an input.</b> <c>FillAsync</c> or a DOM value
    /// assignment can look like it worked — text visible in the box — while WhatsApp's editor model
    /// never updated, producing an empty or unsendable message. Real key events are the only reliable
    /// route.</para>
    /// </summary>
    async Task<ILocator> ComposeAsync(IPage page, WhatsAppMessage message)
    {
        var box = await WhatsAppSelectors.FirstAvailableAsync(
            page, WhatsAppSelectors.MessageBox, 10_000, "the message box");

        var lines = message.Body.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var expected = NormalizeBody(message.Body);

        // Two routes, fast first. InsertText hands the whole line to the editor in one step instead of
        // synthesising a key event per character, which is the difference between a message appearing
        // instantly and several seconds of watching it type itself. It is only safe to attempt because
        // the read-back below is mandatory either way: if a rich-text editor ignores inserted text, the
        // verification catches it and the key-by-key route runs instead. The optimisation cannot
        // produce a wrong message, only a retry.
        foreach (var useFastPath in new[] { true, false })
        {
            await box.ClickAsync();
            await ClearBoxAsync(page, box);

            _bus.Log(useFastPath
                ? $"  [{message.GroupName}] Type {lines.Length} line(s)"
                : $"  [{message.GroupName}] Retyping key by key");

            for (var i = 0; i < lines.Length; i++)
            {
                // Enter sends. A \n inside the typed text would post a half-finished message to a real
                // group, so the body is split first and the breaks are Shift+Enter.
                if (i > 0)
                    await page.Keyboard.PressAsync("Shift+Enter");

                if (lines[i].Length == 0)
                    continue;

                if (useFastPath)
                    await page.Keyboard.InsertTextAsync(lines[i]);
                else
                    await box.PressSequentiallyAsync(lines[i], new LocatorPressSequentiallyOptions { Delay = BodyTypingDelayMs });
            }

            await page.WaitForTimeoutAsync(250);

            var typed = NormalizeBody(await box.InnerTextAsync());
            if (string.Equals(typed, expected, StringComparison.Ordinal))
            {
                _bus.Log($"  [{message.GroupName}] Read back and verified ({expected.Length} chars)");
                return box;
            }

            if (useFastPath)
            {
                _bus.Log($"  [{message.GroupName}] Fast insert did not land cleanly — falling back");
                continue;
            }

            await ClearBoxAsync(page, box);
            throw new InvalidOperationException(
                "What was typed does not match the message that was approved, so nothing was sent. " +
                FirstDifference(expected, typed));
        }

        throw new InvalidOperationException("The message could not be composed.");
    }

    static async Task ClearBoxAsync(IPage page, ILocator box)
    {
        await box.ClickAsync();
        await page.Keyboard.PressAsync("Control+A");
        await page.Keyboard.PressAsync("Backspace");
    }

    /// <summary>Names the first line that differs, which is almost always enough to see what happened —
    /// an emoji auto-conversion, a dropped character, a stray break.</summary>
    static string FirstDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');

        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "(no line)";
            var a = i < actualLines.Length ? actualLines[i] : "(no line)";

            if (!string.Equals(e, a, StringComparison.Ordinal))
                return $"First difference on line {i + 1}: expected \"{e}\", got \"{a}\".";
        }

        return "The texts differ in length but not in any line.";
    }

    /// <summary>Turns a signed-out page into an abort rather than a per-group failure.</summary>
    static async Task ThrowIfSignedOutAsync(IPage page)
    {
        if (await WhatsAppSelectors.AnyVisibleAsync(page, WhatsAppSelectors.SignedOutMarkers, 2_000))
            throw new SessionLostException("WhatsApp Web is showing the QR code — the session has ended.");
    }

    /// <summary>Trim plus NFC. A group named from an iOS client can carry a decomposed "ü" (u + U+0308)
    /// while the operator typed the precomposed form; they render identically and compare unequal.</summary>
    static string Normalize(string value) =>
        (value ?? "").Trim().Normalize(NormalizationForm.FormC);

    /// <summary>The same, plus the zero-width spaces WhatsApp inserts and per-line trailing whitespace,
    /// neither of which is a real difference between what was approved and what was typed.</summary>
    static string NormalizeBody(string value)
    {
        var text = (value ?? "")
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("​", "")
            .Replace("‎", "")
            .Replace(" ", " ");

        var lines = text.Split('\n').Select(line => line.TrimEnd());
        return string.Join("\n", lines).Trim().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// A screenshot of the page as it was left is usually the only way to tell a renamed group from an
    /// expired session, so failing to take one must not itself fail the row.
    /// </summary>
    async Task<string?> TryCaptureFailureScreenshotAsync(IPage page, string groupName)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "artifacts", ModuleName);
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"{SanitizeFileName(groupName)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            return path;
        }
        catch (Exception ex)
        {
            _bus.Log($"Screenshot failed: {groupName} - {ex.Message}");
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
