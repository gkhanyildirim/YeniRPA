using ClosedXML.Excel;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>
/// The run's output: the uploaded file with its titles cleaned and its attribute cells corrected,
/// and nothing else touched.
///
/// <para><b>The uploaded workbook itself, edited in place — not a new file.</b> This used to build a
/// brand new single-sheet workbook from scratch, which is what a marketplace's own product-feed
/// export cannot survive: that file carries the dropdown lists the category team fills the sheet
/// with (a <c>dataValidation</c> entry per attribute column, backed by a <c>ReferenceData</c> sheet
/// of allowed values) plus an <c>Error Details</c> and a <c>Columns</c> sheet the marketplace's own
/// importer expects back. A freshly built sheet has none of that — every dropdown, every other
/// sheet, gone — which is exactly what the category team asked to stop losing. So
/// <see cref="BuildFromOriginal"/> opens the file the operator uploaded and writes only into the
/// cells that actually changed: the cleaned title, and each attribute cell <c>Düzelt</c> rewrote.
/// Everything else — every other sheet, every dropdown, every cell nobody touched — is exactly the
/// bytes that came in.</para>
///
/// <para><b>Only the title and attribute columns of the row's own sheet are ever written.</b> No row
/// status, no error list, no verdict column, no extra sheet is added — the same reasoning as before,
/// just now applied to an edit rather than to what a fresh sheet would have carried: the category
/// team uploads this straight to the marketplace, and anything this file doesn't need to hold is
/// something they would have had to strip back out. The review table, the per-column table and their
/// own export buttons are where that information already lives, on screen, after a run.</para>
///
/// <para><see cref="Build"/> — the old, from-scratch path — is kept for the one case that has no
/// original workbook to edit: a <c>.csv</c> upload. There a dropdown was never possible in the first
/// place, so nothing is lost by writing a fresh sheet.</para>
/// </summary>
public static class TitleCleanWorkbook
{
    /// <summary>Upper bound on rows written. Well above any real category export; the limit exists so
    /// a malformed file cannot turn into an unbounded allocation.</summary>
    public const int MaxRows = 200_000;

    /// <summary>
    /// Edits the uploaded <c>.xlsx</c>/<c>.xls</c> workbook in place: only the title cell and the
    /// attribute cells a rule actually corrected are rewritten. Every other cell, every other sheet,
    /// every data validation, defined name and conditional format the file carried in is untouched —
    /// this reopens the operator's own bytes rather than building anything new.
    /// </summary>
    /// <param name="originalXlsxStream">The uploaded file's own bytes — the same stream
    /// <see cref="TabularFile.Read"/> read <paramref name="table"/> out of, reopened. Must be
    /// seekable from the start.</param>
    public static byte[] BuildFromOriginal(
        Stream originalXlsxStream,
        List<List<string>> table,
        CompiledRuleSet rules,
        IReadOnlyList<TitleCleanRow> rows)
    {
        ArgumentNullException.ThrowIfNull(originalXlsxStream);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rows);

        if (table.Count == 0)
            throw new InvalidOperationException("There is nothing to write: the uploaded file was empty.");

        if (table.Count - 1 > MaxRows)
            throw new InvalidOperationException($"Too many rows to write at once (limit {MaxRows:N0}).");

        using var workbook = new XLWorkbook(originalXlsxStream);

        // The same sheet TabularFile.ReadXlsx read table out of — first worksheet, no name given —
        // so every row and column index below lines up with table and rows without translation.
        WriteChangedCells(workbook.Worksheets.First(), table, rules, rows);

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
    }

    /// <summary>Only the cells a rule actually changed — the title, where the row's clean title
    /// differs from its original, and each attribute cell whose corrected value differs from what
    /// the cell held. Nothing else on the sheet is written, so nothing else can be disturbed.</summary>
    static void WriteChangedCells(
        IXLWorksheet sheet, List<List<string>> table, CompiledRuleSet rules, IReadOnlyList<TitleCleanRow> rows)
    {
        var header = table[0];
        var attributeIndex = TabularFile.BuildHeaderIndex(header);

        var titleIndex = attributeIndex.TryGetValue(rules.Source.TitleColumn.Trim(), out var found)
            ? found
            : -1;

        foreach (var cleaned in rows)
        {
            // rows carries TitleCleanRow.RowNumber as the 1-based Excel row it came from — the same
            // number table's own index maps to (TitleCleanBuilder.Clean sets it to r + 1) — so no
            // separate row lookup is needed.
            var target = cleaned.RowNumber;

            if (titleIndex >= 0 && cleaned.Changed)
                SetText(sheet.Cell(target, titleIndex + 1), cleaned.CleanTitle);

            foreach (var attribute in cleaned.Attributes)
            {
                if (string.Equals(attribute.Value, attribute.OriginalValue, StringComparison.Ordinal))
                    continue;

                if (attributeIndex.TryGetValue(attribute.Column, out var index))
                    SetText(sheet.Cell(target, index + 1), attribute.Value);
            }
        }
    }

    /// <summary>Writes a value as text on just this one cell. A model code of "007" read back as a
    /// number becomes "7", and the whole point of this file is that it can be re-uploaded
    /// unchanged — so the format is forced on the cell being written, not on the column, which would
    /// touch cells nobody asked to change.</summary>
    static void SetText(IXLCell cell, string value)
    {
        cell.Style.NumberFormat.Format = "@";
        cell.SetValue(value);
    }

    /// <summary>The old, from-scratch output: a single new sheet, for a <c>.csv</c> upload, which has
    /// no original workbook — dropdowns and other sheets were never on the table for it, so nothing
    /// this file exists to preserve is lost by building fresh.</summary>
    public static byte[] Build(
        List<List<string>> table, CompiledRuleSet rules, IReadOnlyList<TitleCleanRow> rows)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rows);

        if (table.Count == 0)
            throw new InvalidOperationException("There is nothing to write: the uploaded file was empty.");

        if (table.Count - 1 > MaxRows)
            throw new InvalidOperationException($"Too many rows to write at once (limit {MaxRows:N0}).");

        using var workbook = new XLWorkbook();

        WriteCleaned(workbook.AddWorksheet("Temizlenmiş"), table, rules, rows);

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
    }

    // ---------------------------------------------------------------------

    static void WriteCleaned(
        IXLWorksheet sheet, List<List<string>> table, CompiledRuleSet rules, IReadOnlyList<TitleCleanRow> rows)
    {
        var header = table[0];
        var columnCount = header.Count;
        var byRowNumber = rows.ToDictionary(r => r.RowNumber);
        var attributeIndex = TabularFile.BuildHeaderIndex(header);

        for (var c = 0; c < columnCount; c++)
            sheet.Cell(1, c + 1).SetValue(header[c]);

        var titleIndex = attributeIndex.TryGetValue(rules.Source.TitleColumn.Trim(), out var found)
            ? found
            : -1;

        sheet.Row(1).Style.Font.Bold = true;

        // Text throughout. A model code of "007" read back as a number becomes "7", and the whole
        // point of this sheet is that it can be re-uploaded unchanged.
        sheet.Columns(1, columnCount).Style.NumberFormat.Format = "@";

        for (var r = 1; r < table.Count; r++)
        {
            var source = table[r];
            var target = r + 1;
            byRowNumber.TryGetValue(r + 1, out var cleaned);

            // Every row is copied across as it stands, including the technical code row a marketplace
            // template carries under its header — the builder skips it, and it has to survive.
            for (var c = 0; c < columnCount; c++)
                sheet.Cell(target, c + 1).SetValue(TabularFile.GetCell(source, c));

            if (cleaned is null)
                continue;

            // The corrected values go back into the columns they came from.
            foreach (var attribute in cleaned.Attributes)
            {
                if (attributeIndex.TryGetValue(attribute.Column, out var index))
                    sheet.Cell(target, index + 1).SetValue(attribute.Value);
            }

            if (titleIndex >= 0)
                sheet.Cell(target, titleIndex + 1).SetValue(cleaned.CleanTitle);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns(1, columnCount).AdjustToContents();

        foreach (var column in sheet.ColumnsUsed())
            column.Width = Math.Clamp(column.Width, 10, 60);
    }

    /// <summary>Keeps the download name to plain ASCII, the same call and reason as
    /// <see cref="TableWorkbookBuilder.FileName"/>.</summary>
    public static string FileName(string? ruleSetName) =>
        TableWorkbookBuilder.FileName("Temizlenmis Basliklar" +
            (string.IsNullOrWhiteSpace(ruleSetName) ? "" : " - " + ruleSetName.Trim()));
}
