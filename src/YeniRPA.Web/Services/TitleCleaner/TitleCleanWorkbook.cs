using System.Globalization;
using ClosedXML.Excel;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>
/// The run's output: the cleaned catalogue, an untouched copy of what went in, and the rule set that
/// produced the difference.
///
/// <para><b>Sheet 1 keeps the uploaded file's own column layout</b>, with the title column holding
/// the cleaned title and the attribute cells their corrected values, so it can go straight back to
/// the marketplace. Writing the clean title anywhere else would make this a report rather than a
/// file — the marketplace reads the title column, so an untouched one there uploads every title
/// unchanged while quietly correcting the attributes around it. On a 298-column export the operator
/// would not even see the difference: the appended column sits past everything they ever scroll to.
/// The added columns go on the end for the same reason the order report's <c>Carrier (Normalized)</c>
/// does — inserting them next to the columns they describe would shift every original column.</para>
///
/// <para><b>Sheet 2 is why this is safe to run.</b> A cleaner rewrites data in place and the original
/// title is not recoverable from the result. A wrong rule set noticed a week later is a difference
/// between two sheets in one file rather than a restore request.</para>
///
/// <para>Deliberately <b>not</b> built through <see cref="TableWorkbookBuilder"/>: that writes a
/// styled report with title and filter rows above the header, and sheet 1 has to be re-readable —
/// by the marketplace, and by this app when the operator re-uploads the output to check that a
/// second pass changes nothing.</para>
/// </summary>
public static class TitleCleanWorkbook
{
    /// <summary>The title column holds the cleaned title, so what is appended is the old one.</summary>
    public const string OriginalTitleHeader = "Orijinal Başlık";

    public const string StatusHeader = "Durum";
    public const string ErrorHeader = "Hatalar";

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
        WriteOriginal(workbook.AddWorksheet("Orijinal"), table);
        WriteRuleSet(workbook.AddWorksheet("Kural Seti"), rules, rows);

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

        var extra = columnCount + 1;
        sheet.Cell(1, extra).SetValue(OriginalTitleHeader);
        sheet.Cell(1, extra + 1).SetValue(StatusHeader);
        sheet.Cell(1, extra + 2).SetValue(ErrorHeader);

        for (var a = 0; a < rules.Attributes.Count; a++)
            sheet.Cell(1, extra + 3 + a).SetValue(rules.Attributes[a].Rule.Column + " Durumu");

        var lastColumn = extra + 2 + rules.Attributes.Count;
        sheet.Row(1).Style.Font.Bold = true;

        // Text throughout. A model code of "007" read back as a number becomes "7", and the whole
        // point of this sheet is that it can be re-uploaded unchanged.
        sheet.Columns(1, lastColumn).Style.NumberFormat.Format = "@";

        for (var r = 1; r < table.Count; r++)
        {
            var source = table[r];
            var target = r + 1;
            byRowNumber.TryGetValue(r + 1, out var cleaned);

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

            // The cleaned title goes into the title column itself, the same way a corrected value
            // goes back into its own column. Anything else makes this sheet a report rather than a
            // file: the marketplace reads the title column, so leaving the old title there would
            // upload every title unchanged while quietly fixing the attributes around it.
            //
            // The original is kept twice over — appended here, and untouched on the Orijinal sheet.
            if (titleIndex >= 0)
                sheet.Cell(target, titleIndex + 1).SetValue(cleaned.CleanTitle);

            sheet.Cell(target, extra).SetValue(cleaned.OriginalTitle);
            sheet.Cell(target, extra + 1).SetValue(RowStatus(cleaned));
            sheet.Cell(target, extra + 2).SetValue(string.Join(" | ", cleaned.Errors));

            for (var a = 0; a < rules.Attributes.Count; a++)
            {
                var column = rules.Attributes[a].Rule.Column;
                var attribute = cleaned.Attributes.FirstOrDefault(x =>
                    string.Equals(x.Column, column, StringComparison.Ordinal));

                if (attribute is not null)
                    sheet.Cell(target, extra + 3 + a).SetValue(Label(attribute.Status));
            }

            if (cleaned.HasConflict)
            {
                sheet.Range(target, extra + 1, target, extra + 2).Style.Font.FontColor = XLColor.DarkRed;
                sheet.Cell(target, extra + 1).Style.Font.Bold = true;
            }
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns(1, lastColumn).AdjustToContents();

        foreach (var column in sheet.ColumnsUsed())
            column.Width = Math.Clamp(column.Width, 10, 60);
    }

    /// <summary>A verbatim copy. This is the only record of what the titles used to say.</summary>
    static void WriteOriginal(IXLWorksheet sheet, List<List<string>> table)
    {
        var columnCount = table.Max(row => row.Count);

        for (var r = 0; r < table.Count; r++)
        {
            for (var c = 0; c < columnCount; c++)
                sheet.Cell(r + 1, c + 1).SetValue(TabularFile.GetCell(table[r], c));
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.Columns(1, columnCount).Style.NumberFormat.Format = "@";
        sheet.SheetView.FreezeRows(1);
        sheet.Columns(1, columnCount).AdjustToContents();

        foreach (var column in sheet.ColumnsUsed())
            column.Width = Math.Clamp(column.Width, 10, 60);
    }

    /// <summary>
    /// What ran, so the difference between the two sheets can be explained months later without
    /// anyone having to remember which rule set was selected.
    /// </summary>
    static void WriteRuleSet(IXLWorksheet sheet, CompiledRuleSet rules, IReadOnlyList<TitleCleanRow> rows)
    {
        var source = rules.Source;

        sheet.Cell(1, 1).SetValue("Kural Seti");
        sheet.Cell(1, 2).SetValue(source.Name);
        sheet.Cell(2, 1).SetValue("Başlık Kolonu");
        sheet.Cell(2, 2).SetValue(source.TitleColumn);
        sheet.Cell(3, 1).SetValue("Ondalık Ayracı");
        sheet.Cell(3, 2).SetValue(rules.DecimalSeparator);
        sheet.Cell(4, 1).SetValue("Çalıştırma");
        sheet.Cell(4, 2).SetValue(DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));

        sheet.Cell(5, 1).SetValue("Satır");
        sheet.Cell(5, 2).SetValue(rows.Count.ToString(CultureInfo.InvariantCulture));
        sheet.Cell(6, 1).SetValue("Başlığı Değişen");
        sheet.Cell(6, 2).SetValue(rows.Count(r => r.Changed).ToString(CultureInfo.InvariantCulture));
        sheet.Cell(7, 1).SetValue("İncelenecek");
        sheet.Cell(7, 2).SetValue(rows.Count(r => r.HasConflict).ToString(CultureInfo.InvariantCulture));

        sheet.Range(1, 1, 7, 1).Style.Font.Bold = true;

        string[] headers =
            ["Kolon", "Tip", "Çıkar", "Düzelt", "Başlıktan Doldur", "Birimler", "Eşanlamlılar"];

        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(9, c + 1).SetValue(headers[c]);
        sheet.Row(9).Style.Font.Bold = true;

        var row = 10;
        foreach (var attribute in rules.Attributes)
        {
            var rule = attribute.Rule;
            sheet.Cell(row, 1).SetValue(rule.Column);
            sheet.Cell(row, 2).SetValue(rule.Kind.ToString());
            sheet.Cell(row, 3).SetValue(TitleRuleStore.Yes(rule.Remove));
            sheet.Cell(row, 4).SetValue(TitleRuleStore.Yes(rule.Correct));
            sheet.Cell(row, 5).SetValue(TitleRuleStore.Yes(rule.FillFromTitle));
            sheet.Cell(row, 6).SetValue(TitleRuleStore.EncodeUnits(rule.UnitList));
            sheet.Cell(row, 7).SetValue(TitleRuleStore.EncodeAliases(rule.AliasGroups));
            row++;
        }

        sheet.Columns(1, headers.Length).AdjustToContents();
        foreach (var column in sheet.ColumnsUsed())
            column.Width = Math.Clamp(column.Width, 12, 60);
    }

    // ---------------------------------------------------------------------

    public static string RowStatus(TitleCleanRow row) =>
        row.HasConflict ? "İncelenecek"
        : row.Changed ? "Temizlendi"
        : "Dokunulmadı";

    public static string Label(TitleAttributeStatus status) => status switch
    {
        TitleAttributeStatus.Ok => "OK",
        TitleAttributeStatus.Corrected => "DÜZELTİLDİ",
        TitleAttributeStatus.Conflict => "ÇAKIŞMA",
        TitleAttributeStatus.Ambiguous => "BELİRSİZ",
        TitleAttributeStatus.NotInTitle => "BAŞLIKTA YOK",
        TitleAttributeStatus.Filled => "DOLDURULDU",
        _ => "ÖZELLİK BOŞ",
    };

    /// <summary>Keeps the download name to plain ASCII, the same call and reason as
    /// <see cref="TableWorkbookBuilder.FileName"/>.</summary>
    public static string FileName(string? ruleSetName) =>
        TableWorkbookBuilder.FileName("Temizlenmis Basliklar" +
            (string.IsNullOrWhiteSpace(ruleSetName) ? "" : " - " + ruleSetName.Trim()));
}
