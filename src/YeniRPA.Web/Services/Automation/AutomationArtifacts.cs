using Microsoft.Playwright;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Where an automation run leaves evidence of what went wrong.
///
/// <para>Every runner needs the same thing when a row fails: a picture of the page as the failure left
/// it, because that is usually the only way to tell a missing button or a renamed group from an expired
/// session. The four runners each carried a byte-identical private copy of this; it lives here instead
/// so that "failures are screenshotted to <c>artifacts/&lt;module&gt;</c>" is one behaviour rather than
/// four that happen to agree.</para>
/// </summary>
internal static class AutomationArtifacts
{
    /// <summary>
    /// Screenshots <paramref name="page"/> into <c>artifacts/&lt;module&gt;</c> beside the executable and
    /// returns the path, or <c>null</c> when the screenshot itself failed — taking one must never turn a
    /// failed row into a failed run, so nothing here throws.
    /// </summary>
    /// <param name="key">What the row was about (an order id, a group name, a seller). Names the file.</param>
    public static async Task<string?> TryCaptureFailureScreenshotAsync(
        AutomationJobBus bus,
        IPage page,
        string module,
        string key)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "artifacts", module);
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"{SanitizeFileName(key)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            return path;
        }
        catch (Exception ex)
        {
            bus.Log($"Screenshot failed: {key} - {ex.Message}");
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
