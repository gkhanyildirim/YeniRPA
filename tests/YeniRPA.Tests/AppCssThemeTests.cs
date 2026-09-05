using System.Text.RegularExpressions;
using Xunit;

namespace YeniRPA.Tests;

/// <summary>
/// Guards the one thing about app.css that cannot be caught by looking at the page.
///
/// <para>The dark palette has to be declared twice — once under <c>prefers-color-scheme</c> for people
/// who never touch the toggle, once under <c>[data-theme="dark"]</c> so the toggle wins in both
/// directions. CSS gives no way to write that body once, so the two are copy-paste. When they drift,
/// nothing errors: the console simply looks different depending on whether the viewer's dark mode came
/// from the OS or from the toggle, and only one of the two gets tested by whoever made the change.</para>
///
/// <para>The other test here holds the line that lets Chart.js work at all: <c>RPA.palette()</c> reads
/// these tokens with <c>getComputedStyle</c> and <c>RPA.alpha()</c> takes them apart with
/// <c>parseInt(hex, 16)</c>, so a colour written as <c>oklch()</c> or <c>color-mix()</c> reaches the
/// charts as an unparsed string and paints nothing.</para>
/// </summary>
public class AppCssThemeTests
{
    static string Css() => File.ReadAllText(CssPath());

    static string CssPath()
    {
        // The test binary sits under tests/YeniRPA.Tests/bin/...; walk up to the repo and across.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "YeniRPA.Web", "wwwroot", "css", "app.css");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("app.css was not found from " + AppContext.BaseDirectory);
    }

    /// <summary>Every <c>--token: value;</c> in a block, whitespace-normalised so indentation cannot fail it.</summary>
    static Dictionary<string, string> Tokens(string block)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(block, @"(--[a-z0-9-]+)\s*:\s*([^;]+);", RegexOptions.IgnoreCase))
            tokens[m.Groups[1].Value] = Regex.Replace(m.Groups[2].Value.Trim(), @"\s+", " ");
        return tokens;
    }

    /// <summary>The body of the brace-delimited block that starts at <paramref name="header"/>.</summary>
    static string Block(string css, string header)
    {
        var start = css.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"app.css no longer contains the block '{header}'.");

        var open = css.IndexOf('{', start);
        Assert.True(open >= 0, $"'{header}' is not followed by a block.");

        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0)
                return css[(open + 1)..i];
        }

        throw new InvalidOperationException($"'{header}' is not closed.");
    }

    [Fact]
    public void TheTwoDarkPaletteBlocksDeclareExactlyTheSameTokens()
    {
        var css = Css();
        var media = Tokens(Block(css, ":root:not([data-theme=\"light\"])"));
        var toggle = Tokens(Block(css, ":root[data-theme=\"dark\"]"));

        Assert.NotEmpty(media);

        var onlyInMedia = media.Keys.Except(toggle.Keys).OrderBy(k => k).ToList();
        var onlyInToggle = toggle.Keys.Except(media.Keys).OrderBy(k => k).ToList();

        Assert.True(onlyInMedia.Count == 0,
            "Declared only under prefers-color-scheme, so the theme toggle would not get it: " +
            string.Join(", ", onlyInMedia));
        Assert.True(onlyInToggle.Count == 0,
            "Declared only under [data-theme=\"dark\"], so OS dark mode would not get it: " +
            string.Join(", ", onlyInToggle));

        var different = media.Keys
            .Where(k => media[k] != toggle[k])
            .Select(k => $"{k}: '{media[k]}' vs '{toggle[k]}'")
            .OrderBy(x => x)
            .ToList();

        Assert.True(different.Count == 0,
            "The two dark blocks disagree, so the console changes depending on how dark mode was " +
            "switched on: " + string.Join(" · ", different));
    }

    /// <summary>
    /// The colours <c>RPA.palette()</c> hands to Chart.js. They may only ever be plain hex —
    /// <c>RPA.alpha()</c> parses them by hand and a functional colour would arrive unparsed.
    /// </summary>
    [Fact]
    public void EveryChartTokenIsPlainHex()
    {
        var css = Css();
        var chartTokens = new[]
        {
            "--series-1", "--series-2", "--series-3", "--series-4",
            "--series-5", "--series-6", "--series-7", "--series-8",
            "--mark-critical", "--mark-serious", "--mark-warning", "--mark-good",
            "--accent", "--red", "--green", "--amber",
            "--ink", "--ink-2", "--ink-3", "--line", "--surface", "--surface-2", "--surface-3",
        };

        var offenders = new List<string>();
        foreach (Match m in Regex.Matches(css, @"(--[a-z0-9-]+)\s*:\s*([^;]+);", RegexOptions.IgnoreCase))
        {
            var name = m.Groups[1].Value;
            if (!chartTokens.Contains(name)) continue;

            var value = m.Groups[2].Value.Trim();
            if (!Regex.IsMatch(value, @"^#[0-9a-f]{3}([0-9a-f]{3})?$", RegexOptions.IgnoreCase))
                offenders.Add($"{name}: {value}");
        }

        Assert.True(offenders.Count == 0,
            "RPA.alpha() parses these with parseInt(hex, 16), so a functional colour reaches the " +
            "charts unparsed: " + string.Join(" · ", offenders));
    }

    /// <summary>
    /// The categorical palette is validated as an ordered set — the slot order is the colour-blindness
    /// safety mechanism, not decoration. Changing a hue here without re-running the validator is the
    /// mistake this catches.
    /// </summary>
    [Theory]
    [InlineData("--series-1", "#2A78D6", "#3987E5")]
    [InlineData("--series-2", "#EB6834", "#D95926")]
    [InlineData("--series-3", "#1BAF7A", "#199E70")]
    [InlineData("--series-4", "#EDA100", "#C98500")]
    [InlineData("--series-5", "#E87BA4", "#D55181")]
    [InlineData("--series-6", "#008300", "#008300")]
    [InlineData("--series-7", "#4A3AA7", "#9085E9")]
    [InlineData("--series-8", "#E34948", "#E66767")]
    public void TheValidatedSeriesPaletteIsUnchanged(string token, string light, string dark)
    {
        var css = Css();
        var values = Regex.Matches(css, Regex.Escape(token) + @"\s*:\s*([^;]+);")
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();

        Assert.Contains(light, values);
        Assert.Contains(dark, values);
    }
}
