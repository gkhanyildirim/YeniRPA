using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace YeniRPA.Web.Services;

/// <summary>
/// Reads a large .xlsx one row at a time instead of loading it whole.
///
/// <para><b>Why this exists beside <see cref="TabularFile"/>.</b> Every other upload in this app is an
/// export of a few thousand rows, and reading it into a <c>List&lt;List&lt;string&gt;&gt;</c> costs
/// nothing worth measuring. The Mirakl offer export is not that file: it runs to ~203 000 rows across
/// 26 columns, and <see cref="TabularFile"/>'s ClosedXML path materialises the entire worksheet DOM —
/// 5.3 million cell objects — before the first row is looked at. That is gigabytes of working set for
/// a file this module reads exactly four columns out of.</para>
///
/// <para>So the rows are streamed: shared strings are read once into a flat array, the sheet is walked
/// with <see cref="OpenXmlReader"/>, and each row is yielded and then dropped. Memory stays flat in the
/// size of one row, and the caller can group as it goes.</para>
///
/// <para>The header is yielded as the first element, so the shape is identical to
/// <see cref="TabularFile.Read(Stream, string)"/> and the same
/// <see cref="TabularFile.BuildHeaderIndex"/> / <see cref="TabularFile.GetCell(List{string}, int?)"/>
/// helpers read it. CSV is handed straight to <see cref="TabularFile"/>: a CSV of this size is not a
/// shape anyone uploads, and one streaming reader is enough to maintain.</para>
/// </summary>
internal static class OfferExportReader
{
    /// <summary>
    /// The rows of the upload, header first. Lazily evaluated for .xlsx — enumerate it once, inside the
    /// <c>using</c> that owns <paramref name="stream"/>.
    /// </summary>
    public static IEnumerable<List<string>> Read(Stream stream, string fileName, string? sheetName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var isXlsx = fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".xls", StringComparison.OrdinalIgnoreCase);

        return isXlsx ? ReadXlsx(stream, sheetName) : TabularFile.Read(stream, fileName);
    }

    static IEnumerable<List<string>> ReadXlsx(Stream stream, string? sheetName)
    {
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);

        var workbookPart = document.WorkbookPart
            ?? throw new InvalidOperationException("The uploaded file is not a readable Excel workbook.");

        var sharedStrings = ReadSharedStrings(workbookPart);
        var worksheetPart = FindSheet(workbookPart, sheetName);

        using var reader = OpenXmlReader.Create(worksheetPart);

        while (reader.Read())
        {
            if (!reader.IsStartElement || reader.ElementType != typeof(Row))
                continue;

            // The whole row is loaded, then dropped before the next one — that is the bound on memory
            // here. Loading cell by cell would allocate the same objects one level down and make the
            // sparse-column handling below harder to follow for nothing.
            if (reader.LoadCurrentElement() is not Row row)
                continue;

            yield return ReadRow(row, sharedStrings);
        }
    }

    /// <summary>
    /// One row as a dense list of strings, indexed by column position.
    ///
    /// <para>Cells the writer omitted are the reason this cannot simply append: Excel writes no
    /// <c>&lt;c&gt;</c> element at all for an empty cell, so row 2 of the real export jumps from
    /// <c>L2</c> to <c>N2</c>. Appending would shift every column after the gap by one and quietly read
    /// the EAN column as the lead time. Each value is placed at the index its own cell reference names.</para>
    /// </summary>
    static List<string> ReadRow(Row row, string[] sharedStrings)
    {
        var values = new List<string>();

        foreach (var cell in row.Elements<Cell>())
        {
            var index = ColumnIndex(cell.CellReference?.Value);
            if (index < 0)
                index = values.Count;

            while (values.Count <= index)
                values.Add("");

            values[index] = CellText(cell, sharedStrings);
        }

        return values;
    }

    static string CellText(Cell cell, string[] sharedStrings)
    {
        if (cell.DataType is not null && cell.DataType.Value == CellValues.SharedString)
        {
            if (!int.TryParse(cell.CellValue?.InnerText, out var index) ||
                index < 0 || index >= sharedStrings.Length)
            {
                return "";
            }
            return sharedStrings[index];
        }

        if (cell.DataType is not null && cell.DataType.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? "";

        // Numbers, booleans and dates all arrive as their raw stored text. Nothing this module reads is
        // a date, and a lead time or a seller id is the same string either way — no number formatting is
        // applied on purpose, because that is where a seller id would pick up a thousands separator.
        return cell.CellValue?.InnerText ?? "";
    }

    /// <summary>
    /// The zero-based column a cell reference names: <c>A1</c> → 0, <c>Z9</c> → 25, <c>AA1</c> → 26.
    /// Returns -1 for a reference with no letters, which is not something Excel writes.
    /// </summary>
    internal static int ColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference))
            return -1;

        var index = 0;
        var letters = 0;

        foreach (var ch in cellReference)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                index = (index * 26) + (ch - 'A' + 1);
                letters++;
            }
            else if (ch is >= 'a' and <= 'z')
            {
                index = (index * 26) + (ch - 'a' + 1);
                letters++;
            }
            else
            {
                break;
            }
        }

        return letters == 0 ? -1 : index - 1;
    }

    /// <summary>
    /// The shared string table as a flat array, read once with the same streaming reader.
    ///
    /// <para>This is the one part that is held whole, and it has to be: cell values index into it in
    /// worksheet order, so it cannot be streamed alongside the rows. On the real export it is ~380 000
    /// distinct strings — tens of megabytes, against the gigabytes the worksheet DOM would have cost.</para>
    /// </summary>
    static string[] ReadSharedStrings(WorkbookPart workbookPart)
    {
        var part = workbookPart.SharedStringTablePart;
        if (part is null)
            return [];

        var strings = new List<string>();

        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (!reader.IsStartElement || reader.ElementType != typeof(SharedStringItem))
                continue;

            // InnerText rather than Text.Text: a string Excel stored as several formatted runs has no
            // single <t> child, and reading only the first would truncate the seller name.
            strings.Add(reader.LoadCurrentElement()?.InnerText ?? "");
        }

        return [.. strings];
    }

    /// <summary>
    /// The sheet to read: the named one when a name is given, the first otherwise. A name that matches
    /// nothing throws and lists what the workbook actually holds — the same contract, and the same
    /// reasoning, as <c>TabularFile.FindSheet</c>.
    /// </summary>
    static WorksheetPart FindSheet(WorkbookPart workbookPart, string? sheetName)
    {
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        if (sheets.Count == 0)
            throw new InvalidOperationException("The uploaded workbook holds no sheets.");

        var wanted = (sheetName ?? "").Trim();

        var sheet = wanted.Length == 0
            ? sheets[0]
            // Trimmed on both sides: a sheet tab renamed by hand often carries a trailing space, and it
            // is invisible in Excel.
            : sheets.FirstOrDefault(s =>
                  string.Equals((s.Name?.Value ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException(
                  $"The uploaded workbook has no sheet named '{wanted}'. It holds: " +
                  string.Join(", ", sheets.Select(s => $"'{s.Name?.Value}'")) + ".");

        var id = sheet.Id?.Value
            ?? throw new InvalidOperationException($"Sheet '{sheet.Name?.Value}' cannot be opened.");

        return (WorksheetPart)workbookPart.GetPartById(id);
    }
}
