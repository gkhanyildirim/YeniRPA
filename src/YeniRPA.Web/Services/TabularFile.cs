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

    public static double ParseNumber(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
            return 0;

        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
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
                if (ch == '"')
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
