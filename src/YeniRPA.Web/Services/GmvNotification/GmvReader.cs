using System.Text.Json;
using Microsoft.Playwright;
using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Web.Services.GmvNotification;

public interface IGmvReader
{
    /// <summary>Reads today's GMV.</summary>
    /// <exception cref="GmvSessionExpiredException">There is no saved login, or it no longer works.</exception>
    /// <exception cref="GmvUnavailableException">The dashboard answered but no usable figure came out.</exception>
    Task<GmvReading> ReadAsync(CancellationToken cancellationToken);

    GmvSessionStatus Status { get; }
}

/// <summary>
/// Reads the dashboard's GMV over plain HTTP with the saved Mirakl login — no Chrome window.
///
/// <para><b>One long-lived request context, on purpose.</b> The Marketplace ends a login after 30
/// minutes without activity. Requests made more often than that keep it alive, but only if they
/// share one cookie jar: a context rebuilt from the saved file for every call would replay the cookies
/// as they were when "Save session" was clicked and never see the ones the server has renewed since.
/// The context is rebuilt only when the saved session changes (a new "Save session"), disappears, or
/// is found expired.</para>
///
/// <para>Nothing is written to disk and no cookie leaves this class.</para>
/// </summary>
public sealed class GmvReader(MiraklBrowser browser) : IGmvReader, IAsyncDisposable
{
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _statusSync = new();

    IAPIRequestContext? _context;
    DateTime _contextSessionStamp;
    string _state = "unknown";
    DateTime? _checkedUtc;

    public GmvSessionStatus Status
    {
        get
        {
            lock (_statusSync)
                return new GmvSessionStatus(browser.HasSavedSession ? _state : "none", _checkedUtc);
        }
    }

    public async Task<GmvReading> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!browser.HasSavedSession)
            {
                await DisposeContextAsync();
                SetState("none");
                throw new GmvSessionExpiredException("There is no saved Marketplace session. Sign in and save the session.");
            }

            var context = await EnsureContextAsync();

            IAPIResponse response;
            try
            {
                response = await context.GetAsync(GmvDashboard.SalesUrl(), new APIRequestContextOptions { Timeout = 30_000 });
            }
            catch (PlaywrightException ex)
            {
                throw new GmvUnavailableException($"The dashboard could not be reached: {ex.Message}", ex);
            }

            // An expired login answers 401/403, or redirects to the sign-in page, which arrives as HTML.
            var contentType = response.Headers.TryGetValue("content-type", out var value) ? value : "";
            if (response.Status is 401 or 403
                || (response.Ok && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)))
            {
                await DisposeContextAsync();
                SetState("expired");
                throw new GmvSessionExpiredException("The Marketplace session has expired. Sign in again.");
            }

            if (!response.Ok)
                throw new GmvUnavailableException($"The dashboard answered with status {response.Status}.");

            SetState("valid");
            return new GmvReading(ParseGmv(await response.BodyAsync()), DateTimeOffset.Now);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The <c>gmv</c> number of the sales response. Anything else is "unavailable", never zero.</summary>
    internal static double ParseGmv(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("gmv", out var gmv)
                && gmv.ValueKind == JsonValueKind.Number
                && gmv.TryGetDouble(out var number)
                && double.IsFinite(number) && number >= 0)
            {
                return number;
            }
        }
        catch (JsonException)
        {
            // Falls through to the same "unavailable" as a missing field.
        }

        throw new GmvUnavailableException("The dashboard response did not contain a GMV figure.");
    }

    async Task<IAPIRequestContext> EnsureContextAsync()
    {
        var stamp = browser.SessionSavedUtc;
        if (_context is not null && stamp == _contextSessionStamp)
            return _context;

        await DisposeContextAsync();
        _context = await browser.CreateAuthApiContextAsync();
        _contextSessionStamp = stamp;
        return _context;
    }

    async Task DisposeContextAsync()
    {
        if (_context is null)
            return;

        try { await _context.DisposeAsync(); } catch (PlaywrightException) { /* already gone */ }
        _context = null;
    }

    void SetState(string state)
    {
        lock (_statusSync)
        {
            _state = state;
            _checkedUtc = DateTime.UtcNow;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeContextAsync();
        _gate.Dispose();
    }
}
