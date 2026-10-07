using Microsoft.Playwright;
using YeniRPA.Web.Services.GmvNotification;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Read-only diagnostic for the planned GMV notification module. It answers two questions before any
/// feature is built on top of them: which request on the Mirakl dashboard carries the GMV figure, and
/// how long a saved login survives when it is kept busy with light requests.
///
/// <para>It never clicks, submits or changes anything on the marketplace. Cookies and response bodies
/// are not logged — only times, status codes and request paths.</para>
/// </summary>
public sealed class MiraklSessionProbe : IAsyncDisposable
{
    public const string DashboardUrl = "https://mediamarktsaturn.mirakl.net/marketplace-dashboard";
    const int MaxRecentLines = 200;

    readonly MiraklBrowser _browser;
    readonly string _logPath;
    readonly SemaphoreSlim _logGate = new(1, 1);
    readonly object _sync = new();
    readonly List<string> _recent = [];

    CancellationTokenSource? _cts;
    Task? _loop;
    string? _gmvUrl;
    string[] _privateUrls = [];

    public MiraklSessionProbe(MiraklBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);
        _browser = browser;

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YeniRPA", "Mirakl");
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, "probe.log");
    }

    public bool IsRunning
    {
        get { lock (_sync) return _loop is { IsCompleted: false }; }
    }

    /// <summary>Path of the discovered GMV request (query string left out), or null before discovery.</summary>
    public string? GmvEndpoint
    {
        get { lock (_sync) return _gmvUrl is null ? null : new Uri(_gmvUrl).AbsolutePath; }
    }

    public string LogPath => _logPath;

    public IReadOnlyList<string> RecentLines()
    {
        lock (_sync) return [.. _recent];
    }

    /// <summary>
    /// Opens the dashboard once with the saved login and notes which JSON responses mention GMV.
    /// Opens a visible Chrome window for a few seconds, like every other module here.
    /// </summary>
    public async Task DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!_browser.HasSavedSession)
            throw new InvalidOperationException("There is no saved Mirakl session. Sign in and save the session first.");

        var browser = await _browser.EnsureBrowserAsync();
        await using var context = await _browser.CreateAuthContextAsync(browser);
        var page = await context.NewPageAsync();

        var pending = new List<Task>();
        var candidates = new List<string>();

        page.Response += (_, response) =>
        {
            lock (pending) pending.Add(InspectAsync(response, candidates));
        };

        await Log("discover: opening dashboard");
        await page.GotoAsync(TodayDashboardUrl(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await page.WaitForTimeoutAsync(6_000);

        Task[] inspections;
        lock (pending) inspections = [.. pending];
        await Task.WhenAll(inspections);

        var finalPath = new Uri(page.Url).AbsolutePath;
        await Log($"discover: page ended on {new Uri(page.Url).Host}{finalPath}");

        if (!page.Url.Contains("marketplace-dashboard", StringComparison.OrdinalIgnoreCase))
        {
            await Log("discover: the saved session did not reach the dashboard (login redirect). Sign in again and save the session.");
            return;
        }

        string[] found;
        lock (candidates) found = [.. candidates];

        if (found.Length == 0)
        {
            await Log("discover: no JSON response mentioning GMV was seen.");
            return;
        }

        foreach (var url in found)
            await Log($"discover: GMV found in {new Uri(url).AbsolutePath}");

        // A translations file mentions "GMV" as a label and is served without a login, so it would make
        // an expired session look valid. Only data endpoints behind the login ("/private/") are probed.
        var chosen = found.FirstOrDefault(u =>
            new Uri(u).AbsolutePath.Contains("/private/", StringComparison.OrdinalIgnoreCase)
            && !u.Contains("translation", StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            await Log("discover: only public responses mention GMV; nothing to probe.");
            return;
        }

        lock (_sync)
        {
            _gmvUrl = chosen;
            _privateUrls = [.. found.Where(u => new Uri(u).AbsolutePath.Contains("/private/", StringComparison.OrdinalIgnoreCase))];
        }
        await Log($"discover: will probe {new Uri(chosen).AbsolutePath}");
    }

    /// <summary>
    /// Reads each discovered private endpoint once and lists every JSON field whose name mentions GMV,
    /// with its path and value, so the notification reader can be pointed at the right field.
    /// </summary>
    public async Task<IReadOnlyList<string>> SampleAsync()
    {
        string[] urls;
        lock (_sync) urls = [.. _privateUrls];

        if (urls.Length == 0)
            throw new InvalidOperationException("Run discovery first.");

        await using var context = await _browser.CreateAuthApiContextAsync();
        var lines = new List<string>();

        foreach (var discovered in urls)
        {
            // The operator's own setting: all orders, taxes and shipping included.
            var url = Configure(discovered, "true", "true", "ALL");
            var path = new Uri(url).AbsolutePath;
            lines.Add($"{path} query: {Uri.UnescapeDataString(new Uri(url).Query)}");
            var response = await context.GetAsync(url, new APIRequestContextOptions { Timeout = 30_000 });
            if (!response.Ok)
            {
                lines.Add($"{path}: status {response.Status}");
                continue;
            }

            using var document = System.Text.Json.JsonDocument.Parse(await response.BodyAsync());
            var before = lines.Count;
            Walk(document.RootElement, "$", path, lines, 0);
            if (lines.Count == before)
                lines.Add($"{path}: no field named like GMV");
        }

        return lines;
    }

    /// <summary>
    /// Calls the sales endpoint with each combination of the dashboard's GMV switches and lists the
    /// GMV each returns, so the combination that matches the number on screen can be picked.
    /// </summary>
    public async Task<IReadOnlyList<string>> VariantsAsync()
    {
        string? sales;
        lock (_sync) sales = _privateUrls.FirstOrDefault(u => new Uri(u).AbsolutePath.EndsWith("/private/sales", StringComparison.OrdinalIgnoreCase));

        if (sales is null)
            throw new InvalidOperationException("Run discovery first.");

        await using var context = await _browser.CreateAuthApiContextAsync();
        var lines = new List<string>();

        foreach (var taxes in new[] { "true", "false" })
        foreach (var shipping in new[] { "true", "false" })
        foreach (var shipped in new[] { "YES", "NO", "ALL" })
        {
            var label = $"taxes={taxes} shipping={shipping} shipped={shipped}";

            var response = await context.GetAsync(Configure(sales, taxes, shipping, shipped), new APIRequestContextOptions { Timeout = 30_000 });
            if (!response.Ok)
            {
                lines.Add($"{label}: status {response.Status}");
                continue;
            }

            using var document = System.Text.Json.JsonDocument.Parse(await response.BodyAsync());
            lines.Add(document.RootElement.TryGetProperty("gmv", out var gmv)
                ? $"{label}: gmv = {gmv}"
                : $"{label}: no gmv field");
        }

        return lines;
    }

    static void Walk(System.Text.Json.JsonElement element, string at, string endpoint, List<string> lines, int depth)
    {
        if (depth > 8 || lines.Count > 60)
            return;

        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var child = $"{at}.{property.Name}";
                    if (property.Name.Contains("gmv", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind is not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array))
                    {
                        lines.Add($"{endpoint} {child} = {property.Value}");
                    }
                    Walk(property.Value, child, endpoint, lines, depth + 1);
                }
                break;
            case System.Text.Json.JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray().Take(3))
                    Walk(item, $"{at}[{index++}]", endpoint, lines, depth + 1);
                break;
        }
    }

    /// <summary>
    /// The dashboard with its "Today" date filter, the way the operator's own browser URL carries it:
    /// local midnight to the next local midnight, in epoch milliseconds.
    /// </summary>
    static string TodayDashboardUrl()
    {
        var (from, to) = PlatformToday();
        var filter = $"{{\"startDate\":{from.ToUnixTimeMilliseconds()},\"endDate\":{to.ToUnixTimeMilliseconds()}," +
                     "\"presetId\":\"roma-range-calendar-filter-today\",\"presetLabel\":\"Today\"}";
        return $"{DashboardUrl}?dateFilter={Uri.EscapeDataString(filter)}";
    }

    static (DateTimeOffset From, DateTimeOffset To) PlatformToday() => GmvDashboard.PlatformToday();

    /// <summary>
    /// The sales endpoint's URL for today and the given GMV switches, rebuilt from the discovered one so
    /// the dates follow the clock and the switches do not depend on what the saved browser state holds.
    /// </summary>
    static string Configure(string url, string taxes, string shipping, string shipped)
    {
        var uri = new Uri(url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var (from, to) = PlatformToday();
        query["startDate"] = from.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
        query["endDate"] = to.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
        query["includeTaxes"] = taxes;
        query["includeShippingCharges"] = shipping;
        query["showShipped"] = shipped;
        query["showDebited"] = "ALL";
        return new UriBuilder(uri) { Query = query.ToString() }.Uri.ToString();
    }

    async Task InspectAsync(IResponse response, List<string> candidates)
    {
        try
        {
            var type = response.Request.ResourceType;
            if (type is not ("xhr" or "fetch"))
                return;

            var headers = response.Headers;
            if (!headers.TryGetValue("content-type", out var contentType)
                || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                return;

            var body = await response.TextAsync();
            if (body.Contains("gmv", StringComparison.OrdinalIgnoreCase))
            {
                lock (candidates) candidates.Add(response.Url);
            }
        }
        catch (PlaywrightException)
        {
            // A response that was aborted or has no body is not a candidate.
        }
    }

    /// <summary>
    /// Requests the GMV endpoint (or the dashboard page when none was found) at a fixed interval from
    /// one long-lived HTTP context, and records when the login stops working.
    /// </summary>
    public void StartKeepAlive(TimeSpan interval)
    {
        lock (_sync)
        {
            if (_loop is { IsCompleted: false })
                throw new InvalidOperationException("The session probe is already running.");

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(interval, _cts.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_sync)
        {
            _cts?.Cancel();
            loop = _loop;
        }

        if (loop is not null)
        {
            try { await loop; } catch (OperationCanceledException) { /* stopped on request */ }
        }
    }

    async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        try
        {
            // One context for the whole run: it keeps whatever cookies the server refreshes, which is
            // exactly what a keep-alive depends on. A new context per tick would replay the saved ones.
            await using var context = await _browser.CreateAuthApiContextAsync();
            await Log($"probe: started, every {interval.TotalMinutes:0.#} min");

            using var timer = new PeriodicTimer(interval);
            do
            {
                if (!await ProbeOnceAsync(context, started))
                    return;
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // Stopped on request.
        }
        catch (Exception ex)
        {
            await Log($"probe: stopped by error: {ex.Message}");
        }
        finally
        {
            await Log("probe: stopped");
        }
    }

    /// <summary>True while the session still looks valid.</summary>
    async Task<bool> ProbeOnceAsync(IAPIRequestContext context, DateTimeOffset started)
    {
        string? gmvUrl;
        lock (_sync) gmvUrl = _gmvUrl;

        var url = gmvUrl ?? DashboardUrl;
        var minutes = (DateTimeOffset.Now - started).TotalMinutes;

        var response = await context.GetAsync(url, new APIRequestContextOptions { Timeout = 30_000 });
        var contentType = response.Headers.TryGetValue("content-type", out var value) ? value : "";

        var valid = gmvUrl is null
            ? response.Ok && response.Url.Contains("marketplace-dashboard", StringComparison.OrdinalIgnoreCase)
            : response.Ok && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

        var path = new Uri(response.Url).AbsolutePath;
        await Log($"probe: +{minutes:0} min, status {response.Status}, {(valid ? "valid" : "EXPIRED")}, {path}");

        if (!valid)
            await Log($"probe: the session stopped working after about {minutes:0} minutes.");

        return valid;
    }

    async Task Log(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";

        lock (_sync)
        {
            _recent.Add(line);
            if (_recent.Count > MaxRecentLines)
                _recent.RemoveAt(0);
        }

        await _logGate.WaitAsync();
        try { await File.AppendAllTextAsync(_logPath, line + Environment.NewLine); }
        finally { _logGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { /* shutting down */ }
        _logGate.Dispose();
    }
}
