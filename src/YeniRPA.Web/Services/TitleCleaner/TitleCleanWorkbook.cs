using ClosedXML.Excel;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>
/// The run's output: the uploaded file with its titles cleaned and its attribute cells corrected,
/// and nothing else.
///
/// <para><b>One sheet, and the uploaded file's own column layout.</b> The cleaned title goes into
/// the title column and each corrected value back into the column it came from, so the result can
/// go straight to the marketplace. Writing the clean title anywhere else would make this a report
/// rather than a file — the marketplace reads the title column, so an untouched one there uploads
/// every title unchanged while quietly correcting the attributes around it.</para>
///
/// <para><b>What this deliberately does not carry.</b> It used to append an original-title column,
/// a row status, an error list and a verdict column per rule, and to add an <em>Orijinal</em> and a
/// <em>Kural Seti</em> sheet. All of it is gone: the category team uploads this file to the
/// marketplace, and every extra column and sheet was something they had to strip first. The same
/// information is on screen after a run — the review table, the per-column table, and their own
/// export buttons.</para>
///
/// <para><b>The consequence, stated plainly:</b> a cleaned title cannot be reconstructed from the
/// result, and this file no longer carries a copy of what went in. The uploaded file is the only
/// way back, so it has to be kept. That trade was made deliberately, not overlooked.</para>
///
/// <para>Deliberately <b>not</b> built through <see cref="TableWorkbookBuilder"/>: that writes a
/// styled report with title and filter rows above the header, and this sheet has to be re-readable
/// — by the marketplace, and by this app when the operator re-uploads the output to check that a
/// second pass changes nothing.</para>
/// </summary>
public static class TitleCleanWorkbook
{
    /// <summary>Upper bound on rows written. Well above any real category export; the limit exists so
    /// a malformed file cannot turn into an unbounded allocation.</summary>
    public const int MaxRows = 200_000;

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
