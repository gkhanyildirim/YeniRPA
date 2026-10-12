using System.Text;

namespace YeniRPA.Web.Services;

/// <summary>
/// Row-at-a-time reader for the two SKU report exports. The offers export is a ~50 MB CSV with a
/// couple of hundred thousand rows; <see cref="TabularFile.Read(Stream, string)"/> would materialise
/// the whole text and every row before the first one is looked at, so CSV is streamed here instead.
/// A workbook is delegated to <see cref="OfferExportReader"/>, which already streams with
/// <c>OpenXmlReader</c>.
///
/// <para>The first element is the header, as with <see cref="OfferExportReader.Read"/>. The result is
/// lazy: enumerate it once, inside the <c>using</c> that owns the stream.</para>
/// </summary>
internal static class SkuOfferReader
{
    public static IEnumerable<List<string>> Read(Stream stream, string fileName) =>
        TabularFile.IsXlsx(fileName) ? OfferExportReader.Read(stream, fileName) : ReadCsv(stream);

    static IEnumerable<List<string>> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1 << 16, leaveOpen: true);

        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
            yield break;

        // Same rule as TabularFile.ReadCsv: whichever of ';' and ',' the header uses more.
        var delimiter = headerLine.Count(c => c == ';') > headerLine.Count(c => c == ',') ? ';' : ',';
        yield return ParseLine(headerLine, delimiter);

        var buffer = new char[1 << 16];
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var quotePending = false; // a '"' seen inside quotes: closing quote, or the first half of ""

        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];

                if (quotePending)
                {
                    quotePending = false;
                    if (ch == '"')
                    {
                        field.Append('"');
                        continue;
                    }
                    inQuotes = false;
                }

                if (inQuotes)
                {
                    if (ch == '"') quotePending = true;
                    else field.Append(ch);
                    continue;
                }

                // A quote only opens a quoted field at the start of one; elsewhere it is a literal
                // (an inch mark in a product name) — the rule TabularFile.ParseCsvLine pins down.
                if (ch == '"' && field.Length == 0)
                    inQuotes = true;
                else if (ch == delimiter)
                {
                    fields.Add(field.ToString());
                    field.Clear();
                }
                else if (ch == '\n')
                {
                    fields.Add(field.ToString());
                    field.Clear();
                    var width = fields.Count;
                    if (!IsBlank(fields))
                        yield return fields;
                    fields = new List<string>(width);
                }
                else if (ch != '\r')
                    field.Append(ch);
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            if (!IsBlank(fields))
                yield return fields;
        }
    }

    static bool IsBlank(List<string> fields) => fields.Count == 1 && fields[0].Length == 0;

    static List<string> ParseLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch != '"') field.Append(ch);
                else if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else inQuotes = false;
            }
            else if (ch == '"' && field.Length == 0) inQuotes = true;
            else if (ch == delimiter) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(ch);
        }

        fields.Add(field.ToString());
        return fields;
    }
}
