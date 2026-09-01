using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>
/// Owns <c>%LOCALAPPDATA%\YeniRPA\TitleCleaner\reference-lists.json</c> — the value catalogues a rule
/// may consult for spellings longer than its cells carry.
///
/// <para>Modelled on <see cref="CategoryRuleStore"/> rather than <see cref="TitleRuleStore"/>, and the
/// distinction is the same one: a rule set is what the category team decided and exists nowhere else,
/// while these lists are <b>derived</b> from a workbook that can always be uploaded again. They get
/// the atomic write and the backup generation anyway — they cost nothing — but an unreadable file here
/// is a reason to re-upload, not a disaster.</para>
///
/// <para>Kept out of <c>title-rules.json</c> deliberately. A processor catalogue is five thousand
/// lines; the rule file is meant to stay readable, and hand-editable in a pinch.</para>
/// </summary>
public sealed class TitleReferenceStore
{
    const int CurrentVersion = 1;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    readonly object _sync = new();

    public TitleReferenceStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA",
            "TitleCleaner");
        Directory.CreateDirectory(directory);

        FilePath = Path.Combine(directory, "reference-lists.json");
        BackupPath = FilePath + ".bak";
    }

    public string FilePath { get; }
    public string BackupPath { get; }

    public TitleReferenceFile Load()
    {
        lock (_sync)
        {
            if (!File.Exists(FilePath))
                return Empty();

            string json;
            try
            {
                json = File.ReadAllText(FilePath, Encoding.UTF8);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"The reference lists could not be read from {FilePath}: {ex.Message}", ex);
            }

            return string.IsNullOrWhiteSpace(json) ? Empty() : Parse(json);
        }
    }

    public void Save(TitleReferenceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        lock (_sync)
        {
            var stamped = file with
            {
                Version = CurrentVersion,
                UpdatedUtc = DateTime.UtcNow.ToString("u"),
            };

            var json = JsonSerializer.Serialize(stamped, JsonOptions);
            var temporary = FilePath + ".tmp";

            File.WriteAllText(temporary, json, Encoding.UTF8);

            if (File.Exists(FilePath))
                File.Replace(temporary, FilePath, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(temporary, FilePath);
        }
    }

    /// <summary>Adds or replaces one list, keyed on its folded name so "İşlemciler" and "işlemciler"
    /// are one list rather than two that shadow each other.</summary>
    public TitleReferenceFile Put(TitleReferenceList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (string.IsNullOrWhiteSpace(list.Name))
            throw new InvalidOperationException("A reference list needs a name.");

        lock (_sync)
        {
            var key = FoldedTitle.Fold(list.Name);

            var kept = Load().ListList
                .Where(l => !string.Equals(FoldedTitle.Fold(l.Name), key, StringComparison.Ordinal))
                .ToList();

            kept.Add(list);

            var file = new TitleReferenceFile(CurrentVersion, null, kept);
            Save(file);
            return file;
        }
    }

    public TitleReferenceFile Remove(string name)
    {
        lock (_sync)
        {
            var key = FoldedTitle.Fold(name ?? "");

            var kept = Load().ListList
                .Where(l => !string.Equals(FoldedTitle.Fold(l.Name), key, StringComparison.Ordinal))
                .ToList();

            var file = new TitleReferenceFile(CurrentVersion, null, kept);
            Save(file);
            return file;
        }
    }

    /// <summary>What the editor shows: one line per list, without the thousands of values.</summary>
    public IReadOnlyList<TitleReferenceStatus> Status() =>
        Load().ListList
            .Select(l => new TitleReferenceStatus(l.Name, l.SourceName, l.ValueList.Count))
            .ToList();

    internal static TitleReferenceFile Parse(string json) =>
        JsonSerializer.Deserialize<TitleReferenceFile>(json, JsonOptions)
        ?? throw new InvalidOperationException("The reference list file is empty.");

    internal static string Serialize(TitleReferenceFile file) =>
        JsonSerializer.Serialize(file, JsonOptions);

    static TitleReferenceFile Empty() => new(CurrentVersion, null, []);

    // ---------------------------------------------------------------------
    // Reading a catalogue workbook
    // ---------------------------------------------------------------------

    /// <summary>How many values one list may carry. Well past any real catalogue — the published
    /// Intel/AMD list is about five thousand — and there to stop a wrong column choice loading a
    /// column of product descriptions as a value list.</summary>
    public const int MaxValues = 100_000;

    /// <summary>
    /// Reads one column out of every sheet of a workbook into a list of values.
    ///
    /// <para><b>Every sheet, not the first.</b> The catalogue this was built for splits Intel and AMD
    /// over two sheets under one header, behind a third sheet of provenance notes — reading sheet one
    /// finds the notes and none of the processors. A sheet without the named column is skipped rather
    /// than refused, which is what makes that layout work without the operator having to know it.</para>
    ///
    /// <para>Values are trimmed, blanks dropped, and duplicates removed on the folded form so a
    /// catalogue that lists one model twice does not search for it twice.</para>
    /// </summary>
    public static IReadOnlyList<string> ReadWorkbook(Stream stream, string columnHeader)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (string.IsNullOrWhiteSpace(columnHeader))
            throw new InvalidOperationException("Say which column holds the values.");

        var wanted = columnHeader.Trim();
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sheetsWithColumn = 0;

        using var workbook = new XLWorkbook(stream);

        foreach (var sheet in workbook.Worksheets)
        {
            var used = sheet.RangeUsed();
            if (used is null)
                continue;

            var first = used.FirstRow();
            var column = first.Cells()
                .FirstOrDefault(c => string.Equals(
                    c.GetString().Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                ?.Address.ColumnNumber;

            if (column is null)
                continue;

            sheetsWithColumn++;

            foreach (var row in used.Rows().Skip(1))
            {
                var text = row.Worksheet.Cell(row.RowNumber(), column.Value).GetString().Trim();

                if (text.Length == 0 || !seen.Add(FoldedTitle.Fold(text)))
                    continue;

                values.Add(text);

                if (values.Count > MaxValues)
                {
                    throw new InvalidOperationException(
                        $"The column '{wanted}' holds more than {MaxValues:N0} values. " +
                        "That is not a value catalogue — check the column name.");
                }
            }
        }

        if (sheetsWithColumn == 0)
        {
            var names = string.Join(", ", workbook.Worksheets.Select(s => $"'{s.Name}'"));
            throw new InvalidOperationException(
                $"No sheet in this workbook has a column headed '{wanted}'. Sheets: {names}.");
        }

        if (values.Count == 0)
            throw new InvalidOperationException($"The column '{wanted}' is empty on every sheet.");

        return values;
    }
}
