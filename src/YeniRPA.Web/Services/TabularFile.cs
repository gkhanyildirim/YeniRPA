using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace YeniRPA.Web.Services;

/// <summary>
/// Reads an uploaded .xlsx or .csv into a plain row/column string table, first row = header.
///
/// These readers started life private inside <see cref="ReturnSlaReportBuilder"/> and moved here
/// unchanged when <see cref="ReturnListBuilder"/> needed the same four exports. The bodies are
/// deliberately identical to what the SLA report has always run on — the README's guarantee that
/// its numbers cannot drift depends on that.
/// </summary>
internal static class TabularFile
{
    /// <summary>
    /// The file name is load-bearing: the XLSX or the CSV path is picked purely from the extension.
    /// </summary>
    public static List<List<string>> Read(Stream stream, string fileName)
    {
        var isXlsx = fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".xls", StringComparison.OrdinalIgnoreCase);

        return isXlsx ? ReadXlsx(stream) : ReadCsv(stream);
    }

    /// <summary>Maps header text to column index, first occurrence wins, case-insensitive.</summary>
    public static Dictionary<string, int> BuildHeaderIndex(List<string> header)
    {
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].Trim();
            if (!string.IsNullOrEmpty(name) && !idx.ContainsKey(name))
                idx[name] = i;
        }
        return idx;
    }

    public static string GetCell(List<string> row, int? col)
        => col.HasValue && col.Value < row.Count ? row[col.Value] : "";

    public static string GetCell(List<string> row, int col)
        => col < row.Count ? row[col] : "";

    public static DateTime? ParseDate(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        string[] formats =
        [
            "MM/dd/yyyy hh:mm:ss tt", "MM/dd/yyyy h:mm:ss tt", "M/d/yyyy h:mm:ss tt",
            "dd.MM.yyyy HH:mm", "dd.MM.yyyy", "yyyy-MM-dd HH:mm:ss.fffffff", "yyyy-MM-dd HH:mm:ss",
        ];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            return parsed;

        if (DateTime.TryParse(text, new CultureInfo("tr-TR"), DateTimeStyles.None, out parsed))
            return parsed;

        return null;
    }

    /// <summary>
    /// Parses a date written by the return templates, which put the <b>day first</b>.
    ///
    /// <para><see cref="ParseDate"/> leads with <c>DateTime.TryParse(…, InvariantCulture)</c>, which is
    /// month-first and accepts '.' as a separator: it reads "12.08.2026" as 8 December and
    /// "07.08.2026" as 8 July. Every date whose day <em>and</em> month are 12 or under comes back
    /// transposed, which moves rows in and out of a date range and — in the SLA report — changes how
    /// many days a return has been open.</para>
    ///
    /// <para>Started life as <c>ReturnListBuilder.ParseTemplateDate</c>; moved here unchanged when the
    /// Return SLA report needed the same day-first reading of "Talep Tarihi".</para>
    /// </summary>
    public static DateTime? ParseDayFirstDate(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;

        string[] dayFirstFormats =
        [
            "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy",
            "d.M.yyyy HH:mm", "d.M.yyyy",
            "yyyy-MM-dd HH:mm:ss.fffffff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd",
        ];

        if (DateTime.TryParseExact(text, dayFirstFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        // An unexpected shape is better read leniently than dropped; only the ambiguous day/month
        // case above needed pinning down.
        return ParseDate(text);
    }

    /// <summary>What a tracking-code cell on a return template actually contains.</summary>
    public enum TrackingState { Missing, Malformed, Ok }

    /// <summary>
    /// Reads a return template's tracking-code cell.
    ///
    /// <para>The MP export writes the literal text <c>NULL</c> into the tracking column on three rows
    /// out of four (1991 of 2685 on the sample export). Testing "is it empty" would count those rows
    /// as shipped back to the seller, which is the difference between a return that is running late
    /// and one that was never sent. Every real code on both templates is digits only; anything else
    /// is surfaced for review rather than trusted.</para>
    ///
    /// <para>Started life as <c>ReturnListBuilder.ReadTracking</c>; moved here unchanged when the
    /// Return SLA report needed the same rule.</para>
    /// </summary>
    public static (TrackingState State, string Code) ReadTracking(string raw)
    {
        var code = raw.Trim();

        if (code.Length == 0 || code.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return (TrackingState.Missing, "");

        return code.All(char.IsAsciiDigit)
            ? (TrackingState.Ok, code)
            : (TrackingState.Malformed, code);
    }

    public static double ParseNumber(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
            return 0;

        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>
    /// The key both sides of an order-number match are reduced to: <c>01259_311911494-A</c> and a bare
    /// <c>311911494</c> both become <c>311911494</c>. The prefix is the marketplace and the suffix is
    /// the per-seller split, so neither belongs in the identity of the customer order.
    ///
    /// Started life private inside <see cref="ReturnListBuilder"/> and moved here unchanged when
    /// <see cref="TicketSellerBuilder"/> needed the same key.
    /// </summary>
    public static string OrderCore(string orderNumber)
    {
        var value = orderNumber.Trim();
        if (value.Length == 0)
            return "";

        var underscore = value.IndexOf('_');
        if (underscore >= 0)
        {
            value = value[(underscore + 1)..];
            var dash = value.IndexOf('-');
            if (dash >= 0)
                value = value[..dash];
        }

        return value.Trim();
    }

    /// <summary>The orders export writes seller ids as floats ("11842.0"); the templates use "11842".</summary>
    public static string NormalizeSellerId(string raw)
    {
        var value = raw.Trim();
        var dot = value.IndexOf('.');
        return dot >= 0 ? value[..dot] : value;
    }

    // ---------------------------------------------------------------------

    static List<List<string>> ReadXlsx(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

        var table = new List<List<string>>();
        for (var r = 1; r <= lastRow; r++)
        {
            var row = new List<string>(lastCol);
            for (var c = 1; c <= lastCol; c++)
                row.Add(sheet.Cell(r, c).GetString());
            table.Add(row);
        }
        return table;
    }

    static List<List<string>> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();

        var lines = SplitCsvLines(text);
        if (lines.Count == 0) return [];

        var headerLine = lines[0];
        var delimiter = headerLine.Count(c => c == ';') > headerLine.Count(c => c == ',') ? ';' : ',';

        var table = new List<List<string>>(lines.Count);
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            table.Add(ParseCsvLine(line, delimiter));
        }
        return table;
    }

    /// <summary>Splits raw CSV text into logical lines, respecting quoted fields that may contain newlines.</summary>
    static List<string> SplitCsvLines(string text)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                current.Append(ch);
            }
            else if ((ch == '\n') && !inQuotes)
            {
                lines.Add(current.ToString().TrimEnd('\r'));
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
            lines.Add(current.ToString().TrimEnd('\r'));

        return lines;
    }

    static List<string> ParseCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
            }
            else
            {
                // A quote only opens a quoted field at the *start* of one (RFC 4180). Anywhere else
                // it is a literal character — which is what an inch mark is: a screen size of 16"
                // used to switch quoting on mid-field and swallow the rest of the line into that
                // cell, silently emptying every column after it. Title Cleaner reads screen sizes
                // out of exactly that kind of column.
                if (ch == '"' && field.Length == 0)
                    inQuotes = true;
                else if (ch == delimiter)
                {
                    fields.Add(field.ToString());
                    field.Clear();
                }
                else
                    field.Append(ch);
            }
        }
        fields.Add(field.ToString());
        return fields;
    }
}
