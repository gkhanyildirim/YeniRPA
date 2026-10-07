using System.Globalization;
using System.Text.RegularExpressions;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Reads the monthly seller target workbook ("Joint Business Plan"): a title block, then a header row
/// (Seller Name, Seller ID, target, current revenue, …) and one row per seller.
///
/// <para>The header is not on row 1 — the workbook opens with a title block — so it is found by
/// scanning for the Seller Name / Seller ID cells. Only the target and current-revenue columns are
/// read; the ratio and forecast columns are formulas whose cached values would go stale, so the
/// percentage is always computed from the two plain numbers.</para>
/// </summary>
internal static partial class SellerTargetReader
{
    static readonly string[] TurkishMonths =
        ["Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran", "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık"];

    static readonly string[] EnglishMonths =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    [GeneratedRegex(@"\b([A-Za-z]{3})[A-Za-z]*\.?\s*'?(\d{2,4})\b")]
    private static partial Regex MonthPattern();

    public static SellerTargetSheet Read(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName);

        var headerRow = table.FindIndex(row => row.Any(IsSellerHeader));
        if (headerRow < 0)
            throw new InvalidOperationException("No header row with 'Seller Name' or 'Seller ID' was found in this file.");

        var header = table[headerRow];
        var nameCol = FindColumn(header, "seller name");
        var idCol = FindColumn(header, "seller id");
        var targetCol = FindColumnContaining(header, "target");
        var currentCol = FindColumnContaining(header, "güncel");
        var periodCol = FindColumn(header, "period");

        if (nameCol is null)
            throw new InvalidOperationException("The file has no 'Seller Name' column.");

        if (targetCol is null)
            throw new InvalidOperationException("The file has no target column (a header containing 'Target').");

        if (currentCol is null)
            throw new InvalidOperationException("The file has no current revenue column (a header containing 'Güncel').");

        var (label, monthName, daysInMonth) = DetectMonth(header[targetCol.Value]);
        var rows = new List<SellerTargetRow>();
        for (var r = headerRow + 1; r < table.Count; r++)
        {
            var row = table[r];
            var name = TabularFile.GetCell(row, nameCol.Value).Trim();
            if (name.Length == 0)
                continue;

            var id = idCol is null ? "" : TabularFile.NormalizeSellerId(TabularFile.GetCell(row, idCol.Value));
            // An unresolved lookup in the source workbook comes through as "#N/A": that is "no id", not an id.
            if (id.StartsWith('#'))
                id = "";

            var target = ParseAmount(TabularFile.GetCell(row, targetCol.Value));
            var currentText = TabularFile.GetCell(row, currentCol.Value).Trim();
            double? current = currentText.Length == 0 || currentText.StartsWith('#')
                ? null
                : ParseAmount(currentText);

            // "1-5 Ekim" -> 5 days elapsed. Without a Period column the month is taken as running up to
            // today, which is the only other reading of "current revenue" the file allows.
            var days = periodCol is null ? null : ParseDaysElapsed(TabularFile.GetCell(row, periodCol.Value));
            rows.Add(new SellerTargetRow(id, name, target, current, days ?? (periodCol is null ? DateTime.Now.Day : null), daysInMonth));
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("The file has no seller rows under its header.");

        return new SellerTargetSheet(label, monthName, rows);
    }

    /// <summary>
    /// An amount cell as the workbook reader hands it over: General-format text, so it never carries
    /// thousands separators and a lone "," or "." is the decimal mark ("1674707,21" on a Turkish
    /// machine, "1674707.21" on an English one). <see cref="TabularFile.ParseNumber"/> reads text
    /// invariantly and would take that comma for a thousands separator — 1.67 million turns into
    /// 167 trillion — so the mark is normalised first. With both marks present, the last one is the decimal.
    /// </summary>
    static double ParseAmount(string text)
    {
        var value = text.Trim().Replace(" ", "");
        if (value.Length == 0)
            return 0;

        var comma = value.LastIndexOf(',');
        var dot = value.LastIndexOf('.');
        if (comma >= 0 || dot >= 0)
        {
            var decimalAt = Math.Max(comma, dot);
            value = value[..decimalAt].Replace(",", "").Replace(".", "") + "." + value[(decimalAt + 1)..];
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) ? amount : 0;
    }

    static bool IsSellerHeader(string cell)
    {
        var text = cell.Trim();
        return text.Equals("Seller Name", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Seller ID", StringComparison.OrdinalIgnoreCase);
    }

    static int? FindColumn(List<string> header, string name)
    {
        var index = header.FindIndex(h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index;
    }

    static int? FindColumnContaining(List<string> header, string part)
    {
        var index = header.FindIndex(h => h.Contains(part, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index;
    }

    /// <summary>The last day of a period such as "1-5 Ekim" — the number of days the current revenue covers.</summary>
    static int? ParseDaysElapsed(string period)
    {
        var match = PeriodPattern().Match(period);
        return match.Success && int.TryParse(match.Groups[1].Value, out var days) && days is > 0 and <= 31 ? days : null;
    }

    [GeneratedRegex(@"(\d{1,2})\s*(?:[A-Za-zÇĞİÖŞÜçğıöşü]+)?\s*$")]
    private static partial Regex PeriodPattern();

    /// <summary>"Sum of Oct '26 GMV Target 3" → ("Ekim 2026", "Ekim", 31). Falls back to the current
    /// month when the header names none, so the template always has something to print.</summary>
    static (string Label, string Name, int DaysInMonth) DetectMonth(string targetHeader)
    {
        var year = DateTime.Now.Year;
        var month = DateTime.Now.Month - 1;

        var match = MonthPattern().Match(targetHeader);
        if (match.Success)
        {
            var found = Array.IndexOf(EnglishMonths, match.Groups[1].Value.ToLowerInvariant());
            if (found >= 0)
            {
                month = found;
                var y = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                year = y < 100 ? 2000 + y : y;
            }
        }

        return ($"{TurkishMonths[month]} {year}", TurkishMonths[month], DateTime.DaysInMonth(year, month + 1));
    }
}