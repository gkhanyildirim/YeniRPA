using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using YeniRPA.Web.Models;
using static YeniRPA.Web.Services.XlsxStyles;

namespace YeniRPA.Web.Services;

/// <summary>
/// Turns one dashboard section into a single-sheet workbook: title, the filter that was active when
/// it was exported, then the table itself. Shares <see cref="XlsxStyles"/> with the full report
/// workbook so a section download looks like it came from the same place.
/// </summary>
public static class TableWorkbookBuilder
{
    /// <summary>
    /// Upper bound on posted rows. The dashboard's own tables are capped well below this (the
    /// cancellation detail list, the largest, stops at 200) — the limit exists so a hand-crafted
    /// request cannot turn into an unbounded allocation.
    /// </summary>
    public const int MaxRows = 50_000;

    const int TitleRow = 1;
    const int SubtitleRow = 2;
    const int HeaderRow = 4;

    public static byte[] Build(TableExportRequest request)
    {
        var columns = request.Columns ?? [];
        var rows = request.Rows ?? [];

        if (columns.Count == 0)
            throw new InvalidOperationException("The export request carried no columns.");
        if (rows.Count > MaxRows)
            throw new InvalidOperationException($"Too many rows to export at once (limit {MaxRows:N0}).");

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(SheetName(request.SheetName ?? request.Title));
        ApplyBaseFont(sheet);
        sheet.ShowGridLines = false;

        WriteHeading(sheet, request, columns.Count);

        for (var c = 0; c < columns.Count; c++)
        {
            var cell = sheet.Cell(HeaderRow, c + 1);
            cell.SetValue(columns[c].Label ?? "");
            cell.Style.Alignment.WrapText = true;
            if (columns[c].Numeric)
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        StyleHeaderRow(sheet.Range(HeaderRow, 1, HeaderRow, columns.Count));

        for (var r = 0; r < rows.Count; r++)
        {
            var values = rows[r];
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = sheet.Cell(HeaderRow + 1 + r, c + 1);
                WriteValue(cell, c < values.Count ? values[c] : default);
                if (columns[c].Numeric)
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }
        }

        var lastRow = HeaderRow + rows.Count;
        if (rows.Count > 0)
        {
            ApplyThinBorders(sheet.Range(HeaderRow, 1, lastRow, columns.Count));
            ApplyZebra(sheet, HeaderRow + 1, lastRow, 1, columns.Count);
            ApplyNumberFormats(sheet, rows, columns.Count, lastRow);
        }

        // Only the table is measured — a merged title spanning every column would otherwise stretch
        // the first column to the width of the whole heading.
        sheet.Columns().AdjustToContents(HeaderRow, lastRow);
        foreach (var column in sheet.ColumnsUsed())
            column.Width = Math.Clamp(column.Width, 10, 46);

        sheet.SheetView.FreezeRows(HeaderRow);
        sheet.Cell(HeaderRow, 1).SetActive();

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    static void WriteHeading(IXLWorksheet sheet, TableExportRequest request, int columnCount)
    {
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Export" : request.Title.Trim();
        var titleCell = sheet.Cell(TitleRow, 1);
        titleCell.SetValue(title);
        titleCell.Style.Font.Bold = true;
        titleCell.Style.Font.FontSize = 14;
        titleCell.Style.Font.FontColor = NavyColor;

        var subtitle = (request.Subtitle ?? "").Trim();
        var subtitleCell = sheet.Cell(SubtitleRow, 1);
        subtitleCell.SetValue(subtitle);
        subtitleCell.Style.Font.Italic = true;
        subtitleCell.Style.Font.FontSize = 9;
        subtitleCell.Style.Font.FontColor = MutedColor;

        if (columnCount > 1)
        {
            sheet.Range(TitleRow, 1, TitleRow, columnCount).Merge();
            sheet.Range(SubtitleRow, 1, SubtitleRow, columnCount).Merge();
        }
    }

    /// <summary>
    /// Writes a posted value with its JSON type preserved. Text goes in through
    /// <see cref="IXLCell.SetValue{T}"/>, which stores it as a string — a cell whose text begins with
    /// "=" is never promoted to a formula.
    /// </summary>
    static void WriteValue(IXLCell cell, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out var number):
                cell.Value = number;
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                cell.SetValue(value.GetBoolean() ? "Yes" : "No");
                break;
            case JsonValueKind.String:
                cell.SetValue(value.GetString() ?? "");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                break;
            default:
                cell.SetValue(value.ToString());
                break;
        }
    }

    /// <summary>
    /// Number format is decided per column, not per cell: a column holding 1 and 1.5 would otherwise
    /// print as "1" above "1.50" and read as two different kinds of figure.
    /// </summary>
    static void ApplyNumberFormats(
        IXLWorksheet sheet, IReadOnlyList<IReadOnlyList<JsonElement>> rows, int columnCount, int lastRow)
    {
        for (var c = 0; c < columnCount; c++)
        {
            var sawNumber = false;
            var sawFraction = false;

            foreach (var row in rows)
            {
                if (c >= row.Count || row[c].ValueKind != JsonValueKind.Number) continue;
                if (!row[c].TryGetDouble(out var number)) continue;

                sawNumber = true;
                if (Math.Abs(number % 1) > 1e-9) sawFraction = true;
            }

            if (sawNumber)
            {
                sheet.Range(HeaderRow + 1, c + 1, lastRow, c + 1)
                    .Style.NumberFormat.Format = sawFraction ? "#,##0.00" : "#,##0";
            }
        }
    }

    /// <summary>Excel rejects sheet names over 31 characters or containing <c>[]:*?/\</c>.</summary>
    static string SheetName(string? proposed)
    {
        var cleaned = new string((proposed ?? "").Where(ch => !char.IsControl(ch) && "[]:*?/\\".IndexOf(ch) < 0).ToArray())
            .Trim()
            .Trim('\'');

        if (cleaned.Length == 0) return "Export";
        return cleaned.Length <= 31 ? cleaned : cleaned[..31].TrimEnd();
    }

    /// <summary>
    /// Keeps the download name to plain ASCII. Section names are English, so nothing is lost, and the
    /// browser gets a Content-Disposition it cannot misread.
    /// </summary>
    public static string FileName(string? proposed)
    {
        var text = (proposed ?? "").Trim();
        if (text.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            text = text[..^5];

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is >= ' ' and <= '~' && "\"\\/:*?<>|".IndexOf(ch) < 0)
                builder.Append(ch);
            else if (!char.IsControl(ch))
                builder.Append('-');
        }

        var name = builder.ToString().Trim(' ', '.', '-');
        if (name.Length == 0) name = "Export";
        if (name.Length > 90) name = name[..90].TrimEnd();

        return name + ".xlsx";
    }
}
