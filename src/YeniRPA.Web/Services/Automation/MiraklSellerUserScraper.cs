using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Services.Automation;

/// <summary>One row of a seller's Users tab in the Mirakl operator back office.</summary>
public sealed record MiraklSellerUser(string Name, string Email, bool Enabled);

/// <summary>A row the fetch could not fill, kept so the operator can finish it by hand.</summary>
public sealed record FetchProblem(string SellerId, string SellerName, string Reason);

/// <summary>
/// The finished table plus what happened to it. Held by the scraper until the next run, because the
/// progress bus carries log lines and this is data.
/// </summary>
public sealed record SellerEmailFetchResult(
    IReadOnlyList<SellerMailEntry> Entries,
    int Filled,
    int Unchanged,
    int NoSellerId,
    int SkippedDisabled,
    IReadOnlyList<FetchProblem> Problems,
    string? Error,
    string CompletedUtc);

/// <summary>
/// Fills the mapping table's address column from each seller's Users tab in the Mirakl operator back
/// office, so ~190 addresses do not have to be copied across by hand.
///
/// <para><b>Why it drives a page instead of calling an API.</b> The Users tab is a React
/// micro-frontend: the server-rendered HTML is an empty shell, and the list arrives from an internal
/// endpoint under <c>/private/organizations/{org}/users</c> that is reached by resolving the shop to
/// an organisation first. That chain is undocumented, unversioned, and free to change on any Mirakl
/// release — and when it changes it does not fail loudly, it returns nothing, which written back into
/// the table would erase every address in it. Reading the page the operator would have read costs a
/// few seconds per seller and is true by construction.</para>
///
/// <para><b>The parser deliberately does not know Mirakl's markup.</b> It takes table rows carrying
/// both an address and a status word. That survives a CSS refactor, and it keeps the operator's own
/// address — present in the page chrome on every page — out of the results.</para>
/// </summary>
public sealed partial class MiraklSellerUserScraper
{
    public const string ModuleName = "offer-emails";

    /// <summary>
    /// <c>limit</c> is the Users tab's page size. High enough that no real seller needs a second
    /// page; <see cref="TruncationLimit"/> is what notices if one ever does.
    /// </summary>
    const string ShopUsersUrl = "https://mediamarktsaturn.mirakl.net/mmp/operator/shop/{0}/user?limit=100";

    const int TruncationLimit = 100;

    const int NavigationTimeoutMs = 60_000;

    /// <summary>How long to wait for the React app to put a row on the page. Generous, because the
    /// alternative to waiting is recording "no users" for a seller who has them.</summary>
    const int TableTimeoutMs = 30_000;

    /// <summary>A refusal, not a truncation — the same reasoning as every other run limit here.</summary>
    public const int MaxSellersPerRun = 400;

    readonly MiraklBrowser _browser;
    readonly AutomationJobBus _bus;
    readonly ILogger<MiraklSellerUserScraper> _logger;

    public MiraklSellerUserScraper(MiraklBrowser browser, AutomationJobBus bus, ILogger<MiraklSellerUserScraper> logger)
    {
        _browser = browser;
        _bus = bus;
        _logger = logger;
    }

    /// <summary>
    /// What the last run produced. The panel collects it when the run reports done — the bus streams
    /// progress, and a table is not progress.
    /// </summary>
    public SellerEmailFetchResult? LastResult { get; private set; }

    /// <summary>Raised when the back office answers with its sign-in page. Aborts the whole run.</summary>
    sealed class SessionLostException(string message) : Exception(message);

    /// <summary>
    /// Claims the app-wide run slot and starts the fetch in the background. False when another
    /// automation run already holds it — this one drives the same browser.
    /// </summary>
    public bool TryStart(IReadOnlyList<SellerMailEntry> entries, bool onlyMissing)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (!_bus.TryBeginRun(ModuleName))
            return false;

        LastResult = null;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(entries, onlyMissing);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The seller e-mail fetch failed.");
                _bus.Log($"Fatal error: {ex.Message}");

                // The table is handed back untouched. A half-applied fetch the operator could still
                // press Save on is worse than none at all.
                LastResult = new SellerEmailFetchResult(entries, 0, 0, 0, 0, [], ex.Message, Stamp());
                _bus.Done(0, ["fetch aborted"]);
            }
            finally
            {
                _bus.EndRun();
            }
        });

        return true;
    }

    async Task RunAsync(IReadOnlyList<SellerMailEntry> entries, bool onlyMissing)
    {
        // Which rows to go and look up. A row with no seller id cannot be looked up at all — the back
        // office page is addressed by id — and a filled row is left alone unless a full refresh was
        // asked for.
        var targets = new List<int>();
        var noSellerId = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            if (SellerGroupMap.NormalizeSellerId(entries[i].SellerId).Length == 0)
            {
                noSellerId++;
                continue;
            }

            if (onlyMissing && SellerMailStore.SplitAddresses(entries[i].Email).Count > 0)
                continue;

            targets.Add(i);
        }

        var unchanged = entries.Count - targets.Count - noSellerId;

        _bus.Started(ModuleName, targets.Count);
        _bus.Log($"Reading {targets.Count} seller page(s) in the Mirakl back office.");
        _bus.Log($"{unchanged} row(s) already have an address · {noSellerId} row(s) have no seller ID.");
        _bus.Log("");

        if (targets.Count == 0)
        {
            LastResult = new SellerEmailFetchResult(entries, 0, unchanged, noSellerId, 0, [], null, Stamp());
            _bus.Done(0, []);
            return;
        }

        var merged = new List<SellerMailEntry>(entries);
        var problems = new List<FetchProblem>();
        var filled = 0;
        var skippedDisabled = 0;

        var browser = await _browser.EnsureBrowserAsync();
        await using var context = await _browser.CreateAuthContextAsync(browser);
        var page = await context.NewPageAsync();

        try
        {
            for (var t = 0; t < targets.Count; t++)
            {
                var index = targets[t];
                var entry = merged[index];
                var label = entry.SellerName.Length > 0 ? entry.SellerName : entry.SellerId;

                try
                {
                    var users = await ReadSellerUsersAsync(page, entry.SellerId);
                    var enabled = users.Where(u => u.Enabled).ToList();
                    skippedDisabled += users.Count - enabled.Count;

                    if (enabled.Count == 0)
                    {
                        var reason = users.Count == 0
                            ? "No users are listed for this seller."
                            : $"All {users.Count} user(s) for this seller are disabled.";

                        problems.Add(new FetchProblem(entry.SellerId, entry.SellerName, reason));
                        _bus.Log($"  {label}: {reason}");
                    }
                    else
                    {
                        // Every enabled user goes on one mail, in the back office's own order — the
                        // only ordering both sides can agree on.
                        merged[index] = entry with
                        {
                            Email = SellerMailStore.JoinAddresses(enabled.Select(u => u.Email))
                        };
                        filled++;

                        _bus.Log($"  {label}: {enabled.Count} address(es) — {SellerMailStore.JoinAddresses(enabled.Select(u => u.Email))}");

                        if (users.Count >= TruncationLimit)
                        {
                            var reason = $"The page returned {users.Count} users, which is its page size — there may be more that were not read.";
                            problems.Add(new FetchProblem(entry.SellerId, entry.SellerName, reason));
                            _bus.Log($"  {label}: {reason}");
                        }
                    }
                }
                catch (SessionLostException)
                {
                    // Every remaining seller would fail identically, and each would look like "no
                    // users" — which, saved, would clear the whole column.
                    throw;
                }
                catch (Exception ex)
                {
                    problems.Add(new FetchProblem(entry.SellerId, entry.SellerName, ex.Message));
                    _logger.LogWarning(ex, "The users page for seller {SellerId} could not be read.", entry.SellerId);
                    _bus.Log($"  {label}: FAILED — {ex.Message}");
                }

                _bus.Progress(t + 1, targets.Count);
            }
        }
        finally
        {
            try { await page.CloseAsync(); } catch { /* window already gone */ }
        }

        LastResult = new SellerEmailFetchResult(
            merged, filled, unchanged, noSellerId, skippedDisabled, problems, null, Stamp());

        _bus.Log("");
        _bus.Log($"Filled {filled} row(s). Nothing is saved — review the table and press Save mapping.");
        _bus.Done(filled, [.. problems.Select(p => p.SellerName.Length > 0 ? p.SellerName : p.SellerId)]);
    }

    /// <summary>Navigates to one seller's Users tab and reads what the page renders.</summary>
    async Task<IReadOnlyList<MiraklSellerUser>> ReadSellerUsersAsync(IPage page, string sellerId)
    {
        await page.GotoAsync(
            string.Format(ShopUsersUrl, Uri.EscapeDataString(sellerId)),
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = NavigationTimeoutMs });

        if (LooksLikeSignInPage(page.Url, null))
        {
            throw new SessionLostException(
                "The saved Mirakl session has expired — the back office redirected to its sign-in page. " +
                "Open the Create Return panel, sign in again and save the session, then run this again. " +
                "Nothing was changed.");
        }

        // The list arrives after the document does, so waiting for the document is not enough.
        try
        {
            await page.WaitForFunctionAsync(
                "() => /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}/.test(document.body.innerText)",
                null,
                new PageWaitForFunctionOptions { Timeout = TableTimeoutMs });
        }
        catch (TimeoutException)
        {
            // No address appeared. That is either a seller with no users or a page that never
            // rendered, and the two must not be conflated — one is a fact, the other is a failure.
            var rendered = await page.EvaluateAsync<bool>(
                "() => /Results per page|Username/i.test(document.body.innerText)");

            if (!rendered)
            {
                throw new InvalidOperationException(
                    "The users list did not render within the time allowed, so this seller was not read.");
            }

            return [];
        }

        return ParseUsers(await page.ContentAsync());
    }

    static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'");

    // ---------------------------------------------------------------------
    // Parsing — pure, and where the tests live
    // ---------------------------------------------------------------------

    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();

    /// <summary>Deliberately loose. The strict rule is <see cref="SellerMailStore.LooksLikeEmail"/>,
    /// applied later; this one only has to find the token in a line of table text.</summary>
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    /// <summary>The avatar bubble Mirakl renders before the name, which flattens to text as "MY".</summary>
    [GeneratedRegex(@"^\p{Lu}{1,3}\s+")]
    private static partial Regex AvatarInitialsPattern();

    /// <summary>
    /// The users out of a rendered Users tab.
    ///
    /// <para>A row counts as a user row when it holds both an address and a status word. Requiring
    /// both is what keeps the operator's own address out of the results — it appears in the page
    /// chrome, never in a table row beside an Enabled/Disabled badge.</para>
    /// </summary>
    public static IReadOnlyList<MiraklSellerUser> ParseUsers(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var users = new List<MiraklSellerUser>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match row in RowPattern().Matches(html))
        {
            var text = FlattenToText(row.Groups[1].Value);

            var email = EmailPattern().Match(text);
            if (!email.Success)
                continue;

            bool enabled;
            if (ContainsWord(text, "Disabled"))
                enabled = false;
            else if (ContainsWord(text, "Enabled"))
                enabled = true;
            else
                continue;

            // The same address twice on one page is one user rendered in two places, not two users.
            if (!seen.Add(email.Value))
                continue;

            users.Add(new MiraklSellerUser(
                Name: AvatarInitialsPattern().Replace(text[..email.Index].Trim(), ""),
                Email: email.Value,
                Enabled: enabled));
        }

        return users;
    }

    /// <summary>
    /// True when this response is the sign-in page rather than the page that was asked for.
    ///
    /// <para>The URL is the reliable half: an expired session answers <b>HTTP 200</b> after following
    /// a redirect to <c>/login</c>, so a status check alone sees nothing wrong. The body check is the
    /// backstop for a sign-in page served in place without a redirect.</para>
    /// </summary>
    public static bool LooksLikeSignInPage(string? finalUrl, string? html)
    {
        if (finalUrl is not null &&
            (finalUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
             finalUrl.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
             finalUrl.Contains("/auth/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return html is not null && html.Contains("Marketplace - Sign in", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Markup to the text a human would read: tags dropped, entities decoded, whitespace
    /// runs collapsed. A cell holding a name above an address flattens to "Name address".</summary>
    static string FlattenToText(string markup)
    {
        var withoutTags = TagPattern().Replace(markup, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return string.Join(' ', decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Whole-word match. <c>Contains("Enabled")</c> is true of "Disabled", which would read every
    /// disabled user as enabled and mail a price list to someone who has left the company.
    /// </summary>
    static bool ContainsWord(string text, string word)
    {
        var index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var after = index + word.Length;
            var afterOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);

            if (beforeOk && afterOk)
                return true;

            index = after;
        }

        return false;
    }
}
