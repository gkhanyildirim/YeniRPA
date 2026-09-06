using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using LiteDB;
using YeniRPA.Web.Models;
// LiteDB also declares a JsonSerializer type; this app's JSON is always System.Text.Json's.
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>The instance surface <see cref="CategoryRuleStore"/> exposes through DI. The workbook
/// reader (<see cref="CategoryRuleStore.ReadWorkbook"/>) and the matching helpers
/// (<see cref="CategoryRuleStore.FileCategory"/>, <see cref="CategoryRuleStore.Covers"/>) stay static
/// on the concrete class.</summary>
public interface ICategoryRuleStore
{
    /// <summary>Where the data now lives — the shared LiteDB file. Kept on the interface because
    /// <see cref="Status"/> surfaces it.</summary>
    string FilePath { get; }

    CategoryRuleFile Load();
    void Save(CategoryRuleFile file);
    CategoryRuleStatus Status();

    /// <summary>One-time import from <c>category-rules.json</c>, run by <see cref="JsonToLiteDbMigrator"/>
    /// at startup. A no-op once this store already holds a LiteDB document.</summary>
    void MigrateLegacyJson();
}

/// <summary>
/// Owns the marketplace's RuleSet workbook, reduced to the one thing this module can use — which
/// product types each category accepts — in the <c>categoryRules</c> collection of the shared LiteDB
/// database (<see cref="ILiteDbContext"/>).
///
/// <para>Modelled on <see cref="TitleRuleStore"/>, with one difference that matters. A rule set is
/// data that exists nowhere else, so losing it is unrecoverable; this data is <b>derived</b> from a
/// workbook the marketplace publishes and can always be rebuilt by uploading that workbook again.</para>
///
/// <para>The workbook is parsed <b>once, at upload</b>. Re-reading a 113 KB spreadsheet on every
/// preview would be work done over and over for an answer that only changes when the marketplace
/// publishes a new edition.</para>
/// </summary>
public sealed class CategoryRuleStore : ICategoryRuleStore
{
    const int CurrentVersion = 1;
    const int DocumentId = 1;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public sealed class Document
    {
        public int Id { get; set; }
        public CategoryRuleFile Data { get; set; } = null!;
    }

    readonly ILiteCollection<Document> _collection;

    public CategoryRuleStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        FilePath = context.DatabasePath;
        _collection = context.GetCollection<Document>("categoryRules");
        _collection.EnsureIndex(x => x.Id, unique: true);
    }

    public string FilePath { get; }

    public CategoryRuleFile Load() => _collection.FindById(DocumentId)?.Data ?? Empty();

    public void Save(CategoryRuleFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var stamped = file with
        {
            Version = CurrentVersion,
            UpdatedUtc = DateTime.UtcNow.ToString("u"),
        };

        _collection.Upsert(new Document { Id = DocumentId, Data = stamped });
    }

    /// <summary>The pre-LiteDB location, read once at startup and never again.</summary>
    internal static string LegacyJsonPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YeniRPA", "TitleCleaner", "category-rules.json");

    public void MigrateLegacyJson()
    {
        if (_collection.Count() > 0)
            return;

        var path = LegacyJsonPath();
        if (!File.Exists(path))
            return;

        var json = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            Save(Parse(json));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A legacy file that no longer parses is not a reason to fail startup — this data can
            // always be rebuilt by re-uploading the RuleSet workbook.
        }
    }

    public CategoryRuleStatus Status()
    {
        var file = Load();

        return new CategoryRuleStatus(
            file.SourceName,
            file.UpdatedUtc,
            file.RuleList.Count,
            file.RuleList.Select(r => r.CategoryTr).Distinct(StringComparer.Ordinal).Count(),
            FilePath);
    }

    internal static CategoryRuleFile Parse(string json) =>
        JsonSerializer.Deserialize<CategoryRuleFile>(json, JsonOptions)
        ?? throw new InvalidOperationException("The category rule file is empty.");

    internal static string Serialize(CategoryRuleFile file) =>
        JsonSerializer.Serialize(file, JsonOptions);

    static CategoryRuleFile Empty() => new(CurrentVersion, null, null, []);

    // ---------------------------------------------------------------------
    // Reading the RuleSet workbook
    // ---------------------------------------------------------------------

    /// <summary>The sheets carrying category rules. The published workbook splits them over two
    /// ("Link Rules" and "Link Rules (2)"), so the prefix is matched rather than the whole name.</summary>
    const string SheetPrefix = "Link Rules";

    const string ConditionsHeader = "Conditions";
    const string CategoryHeader = "Mirakl Category";

    /// <summary>The condition line this reads. The workbook's conditions also carry feature frames,
    /// brand lists and much else; none of it says anything about a product's type.</summary>
    const string TypeCondition = "Ürün Tipi";

    /// <summary>
    /// Reads a RuleSet workbook into the rules this module can act on.
    ///
    /// <para><b>The two category columns are read by position, not by name.</b> The sheet heads both
    /// of them "Mirakl Category" — the code on the left, the Turkish label on the right — so a
    /// header-name lookup finds one and silently loses the other.</para>
    /// </summary>
    public static List<CategoryTypeRule> ReadWorkbook(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var workbook = new XLWorkbook(stream);
        var rules = new List<CategoryTypeRule>();

        foreach (var sheet in workbook.Worksheets)
        {
            if (!sheet.Name.StartsWith(SheetPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var header = sheet.Row(1);
            var conditions = 0;
            var categories = new List<int>();

            foreach (var cell in header.CellsUsed())
            {
                var text = cell.GetString().Trim();

                if (string.Equals(text, ConditionsHeader, StringComparison.OrdinalIgnoreCase))
                    conditions = cell.Address.ColumnNumber;
                else if (string.Equals(text, CategoryHeader, StringComparison.OrdinalIgnoreCase))
                    categories.Add(cell.Address.ColumnNumber);
            }

            if (conditions == 0 || categories.Count == 0)
                continue;

            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

            for (var r = 2; r <= lastRow; r++)
            {
                var types = ReadTypes(sheet.Cell(r, conditions).GetString());
                if (types.Count == 0)
                    continue;

                var code = sheet.Cell(r, categories[0]).GetString().Trim();
                var label = categories.Count > 1 ? sheet.Cell(r, categories[1]).GetString().Trim() : "";

                if (code.Length == 0 && label.Length == 0)
                    continue;

                // "#N/A" is what the sheet leaves where a category has no Turkish label yet.
                if (label.Length == 0 || label.Equals("#N/A", StringComparison.OrdinalIgnoreCase))
                    label = code;

                rules.Add(new CategoryTypeRule(code, label, types));
            }
        }

        if (rules.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{fileName}' carries no '{TypeCondition}' rules. Expected a RuleSet workbook with a " +
                $"'{SheetPrefix}' sheet whose '{ConditionsHeader}' column holds lines like " +
                $"\"{TypeCondition} = Ankastre Ocak OR Gazlı Ocak\".");
        }

        return rules;
    }

    /// <summary>
    /// The product types out of one Conditions cell, or an empty list where it names none.
    ///
    /// <para>The cell is several conditions, one per line. Only the product-type line is read; the
    /// rest constrain a rule in ways that have nothing to do with what a title says.</para>
    /// </summary>
    static List<string> ReadTypes(string conditions)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(conditions))
            return found;

        foreach (var line in conditions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var at = line.IndexOf('=');
            if (at < 0)
                continue;

            if (!string.Equals(line[..at].Trim(), TypeCondition, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var type in line[(at + 1)..].Split(" OR ", StringSplitOptions.TrimEntries))
            {
                if (type.Length > 0 && !found.Contains(type, StringComparer.Ordinal))
                    found.Add(type);
            }
        }

        return found;
    }

    // ---------------------------------------------------------------------
    // Matching an uploaded file to a category
    // ---------------------------------------------------------------------

    /// <summary>Header names a product file writes its category under.</summary>
    static readonly string[] CategoryHeaders = ["Kategori", "CATEGORY", "Category"];

    /// <summary>
    /// The category an uploaded product file is for, or <c>null</c>.
    ///
    /// <para>Taken as the most common non-empty value rather than the first: the header rows and the
    /// odd blank cell are not what the file is about, and a single stray row should not decide it.</para>
    /// </summary>
    public static string? FileCategory(List<List<string>> table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (table.Count < 2)
            return null;

        var header = TabularFile.BuildHeaderIndex(table[0]);
        int? column = null;

        foreach (var name in CategoryHeaders)
        {
            if (header.TryGetValue(name, out var index))
            {
                column = index;
                break;
            }
        }

        if (column is null)
            return null;

        var best = table
            .Skip(1)
            .Select(row => TabularFile.GetCell(row, column).Trim())
            .Where(value => value.Length > 0)
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        return best?.Key;
    }

    /// <summary>
    /// Whether a RuleSet rule belongs to the category an uploaded file declares.
    ///
    /// <para>A product file writes its category as a path — "EV ALETLERİ/BÜYÜK EV ALETLERİ/OCAKLAR"
    /// — while the RuleSet names the leaf alone. Only the leaf is compared, and folded, so the
    /// Turkish dotted I does not decide whether two spellings of one category are the same.</para>
    /// </summary>
    public static bool Covers(CategoryTypeRule rule, string? fileCategory)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var leaf = Leaf(fileCategory);
        if (leaf.Length == 0)
            return false;

        return string.Equals(FoldedTitle.Fold(rule.CategoryTr), leaf, StringComparison.Ordinal)
            || string.Equals(FoldedTitle.Fold(rule.Category), leaf, StringComparison.Ordinal);
    }

    static string Leaf(string? path)
    {
        var value = (path ?? "").Trim();
        if (value.Length == 0)
            return "";

        var at = value.LastIndexOf('/');
        return FoldedTitle.Fold(at >= 0 ? value[(at + 1)..] : value);
    }
}
