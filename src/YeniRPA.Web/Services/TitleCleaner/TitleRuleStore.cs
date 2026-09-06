using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using LiteDB;
using YeniRPA.Web.Models;
// LiteDB also declares a JsonSerializer type; this app's JSON is always System.Text.Json's.
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>The instance surface <see cref="TitleRuleStore"/> exposes through DI. Every static member
/// (<see cref="TitleRuleStore.Parse"/>, <see cref="TitleRuleStore.ToForm(TitleRuleSet)"/>, the Excel
/// round trip, the cell encoding) stays on the concrete class — none of it depends on where the file
/// lives, and the wire/editor shapes are not something a swap of storage engine should touch.</summary>
public interface ITitleRuleStore
{
    /// <summary>Where the data now lives — the shared LiteDB file. Kept on the interface because the
    /// rule editor shows it.</summary>
    string FilePath { get; }

    TitleRuleFile Load();
    void Save(TitleRuleFile file);
    TitleRuleSet? Find(string? name);

    /// <summary>One-time import from <c>title-rules.json</c>, run by <see cref="JsonToLiteDbMigrator"/>
    /// at startup. A no-op once this store already holds a LiteDB document.</summary>
    void MigrateLegacyJson();
}

/// <summary>
/// Owns every category's naming standard, in the <c>titleRules</c> collection of the shared LiteDB
/// database (<see cref="ILiteDbContext"/>).
///
/// <para>Modelled on <see cref="SellerGroupStore"/> and for the same reason: like the seller/group
/// mapping, this is data that exists nowhere else. It is not derived from an export and it cannot be
/// rebuilt by re-running anything — it is what the category team decided a laptop title should look
/// like. It used to keep its own atomic-write-plus-backup JSON file for that reason; LiteDB's
/// write-ahead log now gives the same crash-safety without this class hand-rolling it.</para>
///
/// <para>Not encrypted, also like <see cref="SellerGroupStore"/>: these are column names and unit
/// spellings, not credentials.</para>
/// </summary>
public sealed class TitleRuleStore : ITitleRuleStore
{
    const int CurrentVersion = 1;
    const int DocumentId = 1;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // The file is meant to be readable, and hand-editable in a pinch: "Measure" says what it is
        // where a bare 1 does not.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>One document holding every rule set — the aggregate <c>Load</c>/<c>Save</c> always
    /// dealt in. Attribute order inside a set is load-bearing (see the class doc on
    /// <see cref="TitleAttributeRule"/> ordering), which is also why this stays one document rather
    /// than one row per set: nothing here is ever read one set at a time.</summary>
    public sealed class Document
    {
        public int Id { get; set; }
        public TitleRuleFile Data { get; set; } = null!;
    }

    readonly ILiteCollection<Document> _collection;

    public TitleRuleStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        FilePath = context.DatabasePath;
        _collection = context.GetCollection<Document>("titleRules");
        _collection.EnsureIndex(x => x.Id, unique: true);
    }

    public string FilePath { get; }

    public TitleRuleFile Load() => _collection.FindById(DocumentId)?.Data ?? Empty();

    /// <summary>
    /// The deserialisation half of <see cref="Load"/>, split out so the defaults can be tested
    /// without a file. What a rule omits matters: a <c>remove</c> that quietly read back as
    /// <c>false</c> would leave every title in the catalogue untouched while reporting success.
    /// </summary>
    internal static TitleRuleFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<TitleRuleFile>(json, JsonOptions);
        return file is null ? Empty() : file with { Sets = file.Sets ?? [] };
    }

    internal static string Serialize(TitleRuleFile file) => JsonSerializer.Serialize(file, JsonOptions);

    /// <summary>
    /// Reads one rule set posted as JSON in the editor's flattened shape — what the browser sends
    /// alongside an upload, before the set has been saved.
    /// </summary>
    public static TitleRuleSet ParseRuleSetForm(string json)
    {
        TitleRuleSetForm? form;
        try
        {
            form = JsonSerializer.Deserialize<TitleRuleSetForm>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The rule set could not be read: {ex.Message}", ex);
        }

        if (form is null)
            throw new InvalidOperationException("The rule set could not be read: it was empty.");

        return FromForm(form);
    }

    // ---------------------------------------------------------------------
    // Editor shape
    // ---------------------------------------------------------------------

    public static TitleRuleSetForm ToForm(TitleRuleSet set) => new(
        set.Name,
        set.TitleColumn,
        set.AttributeList.Select(rule => new TitleAttributeForm(
            rule.Column,
            rule.Kind.ToString(),
            rule.Remove,
            rule.Correct,
            rule.FillFromTitle,
            rule.AllowSuffix,
            rule.AllowPartial,
            // One entry per line: the editor's box is where forty alias groups have to be read, and
            // on one line they cannot be.
            EncodeUnitLines(rule.UnitList),
            EncodeAliasLines(rule.AliasGroups),
            rule.ReferenceList ?? "")).ToList(),
        set.DecimalSeparator == "," ? "," : ".",
        set.CollapseRepeats);

    public static TitleRuleSet FromForm(TitleRuleSetForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new TitleRuleSet(
            (form.Name ?? "").Trim(),
            (form.TitleColumn ?? "").Trim(),
            form.AttributeList
                .Where(a => !string.IsNullOrWhiteSpace(a.Column))
                .Select(a => new TitleAttributeRule(
                    a.Column.Trim(),
                    ParseKind(a.Kind),
                    a.Remove,
                    a.Correct,
                    a.FillFromTitle,
                    a.AllowSuffix,
                    a.AllowPartial,
                    ParseUnits(a.Units),
                    ParseAliases(a.Aliases),
                    string.IsNullOrWhiteSpace(a.ReferenceList) ? null : a.ReferenceList.Trim()))
                .ToList(),
            form.DecimalSeparator == "," ? "," : ".",
            form.CollapseRepeats);
    }

    public static TitleRuleFileForm ToForm(TitleRuleFile file) =>
        new(file.Version, file.UpdatedUtc, file.Sets.Select(ToForm).ToList());

    public static TitleRuleFile FromForm(TitleRuleFileForm form) =>
        new(CurrentVersion, form.UpdatedUtc, (form.Sets ?? []).Select(FromForm).ToList());

    internal static TitleRuleFileForm ParseFileForm(string json)
    {
        var form = JsonSerializer.Deserialize<TitleRuleFileForm>(json, JsonOptions);
        return form is null
            ? new TitleRuleFileForm(CurrentVersion, null, [])
            : form with { Sets = form.Sets ?? [] };
    }

    public void Save(TitleRuleFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var stamped = file with
        {
            Version = CurrentVersion,
            UpdatedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Sets = file.Sets ?? [],
        };

        _collection.Upsert(new Document { Id = DocumentId, Data = stamped });
    }

    /// <summary>The pre-LiteDB location, read once at startup and never again.</summary>
    internal static string LegacyJsonPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YeniRPA", "TitleCleaner", "title-rules.json");

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
        catch (JsonException)
        {
            // A legacy file that no longer parses is not a reason to fail startup: Load() would have
            // refused it under the old code too, and there is nothing here worth carrying over.
        }
    }

    /// <summary>The named rule set, or <c>null</c>. Names are compared folded, so the same set typed
    /// with a Turkish dotted I still resolves.</summary>
    public TitleRuleSet? Find(string? name)
    {
        var wanted = FoldedTitle.Fold(name);
        if (wanted.Length == 0)
            return null;

        return Load().Sets.FirstOrDefault(
            set => string.Equals(FoldedTitle.Fold(set.Name), wanted, StringComparison.Ordinal));
    }

    static TitleRuleFile Empty() => new(CurrentVersion, null, []);

    // ---------------------------------------------------------------------
    // Excel round trip
    // ---------------------------------------------------------------------

    const int SetColumn = 1;
    const int TitleColumn = 2;
    const int DecimalColumn = 3;
    const int ColumnColumn = 4;
    const int KindColumn = 5;
    const int RemoveColumn = 6;
    const int CorrectColumn = 7;
    const int FillColumn = 8;
    const int UnitsColumn = 9;
    const int AliasColumn = 10;

    /// <summary>Last on purpose. Putting a new column where it reads best would shift every constant
    /// above, and those decide which cell each value is written into.</summary>
    const int SuffixColumn = 11;
    const int PartialColumn = 12;
    const int ReferenceColumn = 13;

    /// <summary>A rule-set setting rather than a per-rule one, written on every row of that set — the
    /// same shape as "Ondalık Ayracı", which the sheet has always repeated the same way.</summary>
    const int RepeatColumn = 14;

    static readonly string[] Headers =
    [
        "Kural Seti", "Başlık Kolonu", "Ondalık Ayracı", "Kolon", "Tip",
        "Çıkar", "Düzelt", "Başlıktan Doldur", "Birimler", "Değerler", "Ek", "Kısmi",
        "Referans Listesi", "Tekrarı Sil",
    ];

    /// <summary>What the alias column used to be called. Workbooks exported before the rename carry
    /// it, and the column is optional — without this they would import silently, keeping every rule
    /// but losing its whole catalogue.</summary>
    const string LegacyAliasHeader = "Eşanlamlılar";

    /// <summary>
    /// A plain sheet, one row per attribute, deliberately <b>not</b> built through
    /// <see cref="TableWorkbookBuilder"/> — that writes a styled report with title rows above the
    /// data, which <see cref="ReadWorkbook"/> could not read back. Same call and same reason as
    /// <see cref="SellerGroupStore.BuildWorkbook"/>.
    /// </summary>
    public static byte[] BuildWorkbook(IReadOnlyList<TitleRuleSet> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Kural Setleri");

        for (var c = 0; c < Headers.Length; c++)
            sheet.Cell(1, c + 1).Value = Headers[c];
        sheet.Row(1).Style.Font.Bold = true;

        // Text throughout: a unit spelling of "11" or a set named "2024" must not come back as a
        // number, and the encoded unit/alias cells must survive verbatim.
        sheet.Columns(SetColumn, RepeatColumn).Style.NumberFormat.Format = "@";

        var row = 2;
        foreach (var set in sets)
        {
            foreach (var rule in set.AttributeList)
            {
                sheet.Cell(row, SetColumn).SetValue(set.Name);
                sheet.Cell(row, TitleColumn).SetValue(set.TitleColumn);
                sheet.Cell(row, DecimalColumn).SetValue(set.DecimalSeparator);
                sheet.Cell(row, ColumnColumn).SetValue(rule.Column);
                sheet.Cell(row, KindColumn).SetValue(rule.Kind.ToString());
                sheet.Cell(row, RemoveColumn).SetValue(Yes(rule.Remove));
                sheet.Cell(row, CorrectColumn).SetValue(Yes(rule.Correct));
                sheet.Cell(row, FillColumn).SetValue(Yes(rule.FillFromTitle));
                sheet.Cell(row, UnitsColumn).SetValue(EncodeUnits(rule.UnitList));
                sheet.Cell(row, AliasColumn).SetValue(EncodeAliases(rule.AliasGroups));
                sheet.Cell(row, SuffixColumn).SetValue(Yes(rule.AllowSuffix));
                sheet.Cell(row, PartialColumn).SetValue(Yes(rule.AllowPartial));
                sheet.Cell(row, ReferenceColumn).SetValue(rule.ReferenceList ?? "");
                sheet.Cell(row, RepeatColumn).SetValue(Yes(set.CollapseRepeats));
                row++;
            }
        }

        sheet.Columns(SetColumn, RepeatColumn).AdjustToContents();
        foreach (var column in sheet.ColumnsUsed())
            column.Width = Math.Clamp(column.Width, 10, 52);

        sheet.SheetView.FreezeRows(1);

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
    }

    /// <summary>Reads a rule-set workbook or CSV back. The file name is load-bearing — see
    /// <see cref="TabularFile.Read"/>.</summary>
    public static List<TitleRuleSet> ReadWorkbook(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("The rule set file is empty.");

        var header = TabularFile.BuildHeaderIndex(table[0]);

        var cSet = Require(header, Headers[SetColumn - 1]);
        var cTitle = Require(header, Headers[TitleColumn - 1]);
        var cColumn = Require(header, Headers[ColumnColumn - 1]);

        var cDecimal = Optional(header, Headers[DecimalColumn - 1]);
        var cKind = Optional(header, Headers[KindColumn - 1]);
        var cRemove = Optional(header, Headers[RemoveColumn - 1]);
        var cCorrect = Optional(header, Headers[CorrectColumn - 1]);
        var cFill = Optional(header, Headers[FillColumn - 1]);
        var cUnits = Optional(header, Headers[UnitsColumn - 1]);
        var cAlias = OptionalAny(header, Headers[AliasColumn - 1], LegacyAliasHeader);
        var cSuffix = Optional(header, Headers[SuffixColumn - 1]);
        var cPartial = Optional(header, Headers[PartialColumn - 1]);

        // Optional like the rest: a workbook exported before reference lists existed carries no such
        // column, and it has to keep importing rather than being refused for a column it predates.
        var cReference = Optional(header, Headers[ReferenceColumn - 1]);
        var cRepeat = Optional(header, Headers[RepeatColumn - 1]);

        // Insertion-ordered, because attribute order inside a set decides which of two attributes
        // claims a stretch of title that both could match.
        var sets = new List<(string Name, string Title, string Separator, bool Repeats,
            List<TitleAttributeRule> Rules)>();

        foreach (var row in table.Skip(1))
        {
            var setName = TabularFile.GetCell(row, cSet).Trim();
            var column = TabularFile.GetCell(row, cColumn).Trim();

            if (setName.Length == 0 || column.Length == 0)
                continue;

            var titleColumn = TabularFile.GetCell(row, cTitle).Trim();
            var separator = TabularFile.GetCell(row, cDecimal).Trim() == "," ? "," : ".";
            var repeats = ParseBool(TabularFile.GetCell(row, cRepeat), fallback: false);

            var existing = sets.FirstOrDefault(s =>
                string.Equals(FoldedTitle.Fold(s.Name), FoldedTitle.Fold(setName), StringComparison.Ordinal));

            if (existing.Rules is null)
            {
                existing = (setName, titleColumn, separator, repeats, []);
                sets.Add(existing);
            }

            existing.Rules.Add(new TitleAttributeRule(
                column,
                ParseKind(TabularFile.GetCell(row, cKind)),
                ParseBool(TabularFile.GetCell(row, cRemove), fallback: true),
                ParseBool(TabularFile.GetCell(row, cCorrect), fallback: true),
                ParseBool(TabularFile.GetCell(row, cFill), fallback: false),
                ParseBool(TabularFile.GetCell(row, cSuffix), fallback: false),
                ParseBool(TabularFile.GetCell(row, cPartial), fallback: false),
                ParseUnits(TabularFile.GetCell(row, cUnits)),
                ParseAliases(TabularFile.GetCell(row, cAlias)),
                Blank(TabularFile.GetCell(row, cReference))));
        }

        if (sets.Count == 0)
            throw new InvalidOperationException("The rule set file carries no rows.");

        return sets
            .Select(s => new TitleRuleSet(s.Name, s.Title, s.Rules, s.Separator, s.Repeats))
            .ToList();
    }

    /// <summary>An empty cell as <c>null</c> — "no reference list" and "a list named nothing" are the
    /// same thing, and the rule carries the first of them.</summary>
    static string? Blank(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    static int Require(Dictionary<string, int> header, string name) =>
        header.TryGetValue(name, out var index)
            ? index
            : throw new InvalidOperationException($"Required column '{name}' was not found in the rule set file.");

    static int? Optional(Dictionary<string, int> header, string name) =>
        header.TryGetValue(name, out var index) ? index : null;

    /// <summary>The first of these headers the file carries, so a column that has been renamed can
    /// still be read out of a workbook exported under its old name.</summary>
    static int? OptionalAny(Dictionary<string, int> header, params string[] names)
    {
        foreach (var name in names)
        {
            if (header.TryGetValue(name, out var index))
                return index;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Cell encoding
    // ---------------------------------------------------------------------
    //
    // Units and alias groups are lists of lists, and a spreadsheet cell holds one string. The
    // encoding is deliberately plain enough to edit by hand:
    //
    //   Birimler   GB=gb|gbyte|gigabayt@1 ; TB=tb|terabayt@1024
    //   Değerler   W11P|Windows 11 Pro|Win 11 Pro ; W11H|Windows 11 Home
    //
    // In both, ";" separates entries and "|" separates spellings. For a unit, what precedes "="
    // is the canonical spelling and what follows "@" is its size in the base unit.
    //
    // A newline separates entries too, and the editor uses that form: one value per line is
    // readable where forty of them on one line are not. The workbook keeps ";" because a cell is
    // one line, and the *parser* accepts both — so there is still exactly one implementation of
    // this format, and a cell somebody typed by hand reads back either way.

    internal static string Yes(bool value) => value ? "Evet" : "Hayır";

    static bool ParseBool(string raw, bool fallback)
    {
        var text = FoldedTitle.Fold(raw);
        if (text.Length == 0)
            return fallback;

        return text switch
        {
            "evet" or "yes" or "true" or "1" or "x" or "var" => true,
            "hayir" or "no" or "false" or "0" or "yok" => false,
            _ => fallback,
        };
    }

    /// <summary>The older spellings are kept alongside the current label so a workbook exported
    /// before the type was renamed still reads back as the same kind — an unrecognised word falls
    /// through to <see cref="TitleAttributeKind.Text"/> without complaining, which would quietly
    /// disable the rule's conflict detection.</summary>
    static TitleAttributeKind ParseKind(string raw) => FoldedTitle.Fold(raw) switch
    {
        "measure" or "olcu" or "olculu" or "birim" => TitleAttributeKind.Measure,
        "alias" or "deger listesi" or "degerler" or "esanlamli" or "katalog"
            => TitleAttributeKind.Alias,
        _ => TitleAttributeKind.Text,
    };

    /// <summary>The separators an entry list may be written with. ";" is what a spreadsheet cell
    /// holds; a newline is what the editor shows.</summary>
    static readonly char[] EntrySeparators = [';', '\n', '\r'];

    internal static string EncodeUnits(IReadOnlyList<MeasureUnit> units) =>
        string.Join(" ; ", UnitEntries(units));

    /// <summary>The same entries, one per line — the form the rule editor's box holds.</summary>
    internal static string EncodeUnitLines(IReadOnlyList<MeasureUnit> units) =>
        string.Join("\n", UnitEntries(units));

    static IEnumerable<string> UnitEntries(IReadOnlyList<MeasureUnit> units)
    {
        foreach (var unit in units ?? [])
        {
            var spellings = (unit.Spellings ?? []).Where(s => !string.IsNullOrWhiteSpace(s));
            var text = unit.Canonical + "=" + string.Join("|", spellings);

            yield return unit.Factor > 0
                ? text + "@" + unit.Factor.ToString("0.##########", CultureInfo.InvariantCulture)
                : text;
        }
    }

    static IReadOnlyList<MeasureUnit>? ParseUnits(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return null;

        var units = new List<MeasureUnit>();

        foreach (var entry in text.Split(
            EntrySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var body = entry;
            double factor = 0;

            var at = body.LastIndexOf('@');
            if (at >= 0)
            {
                if (Measures.TryParseQuantity(body[(at + 1)..], out var parsed))
                    factor = parsed;
                body = body[..at];
            }

            var equals = body.IndexOf('=');
            var canonical = (equals >= 0 ? body[..equals] : body).Trim();
            if (canonical.Length == 0)
                continue;

            var spellings = equals >= 0
                ? body[(equals + 1)..]
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList()
                : [];

            units.Add(new MeasureUnit(canonical, spellings, factor));
        }

        return units.Count > 0 ? units : null;
    }

    internal static string EncodeAliases(IReadOnlyList<IReadOnlyList<string>> groups) =>
        string.Join(" ; ", AliasEntries(groups));

    /// <summary>The same groups, one per line — the form the rule editor's box holds.</summary>
    internal static string EncodeAliasLines(IReadOnlyList<IReadOnlyList<string>> groups) =>
        string.Join("\n", AliasEntries(groups));

    static IEnumerable<string> AliasEntries(IReadOnlyList<IReadOnlyList<string>> groups) =>
        (groups ?? [])
            .Where(group => group is { Count: > 0 })
            .Select(group => string.Join("|", group.Where(s => !string.IsNullOrWhiteSpace(s))));

    static IReadOnlyList<IReadOnlyList<string>>? ParseAliases(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return null;

        var groups = new List<IReadOnlyList<string>>();

        foreach (var entry in text.Split(
            EntrySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var spellings = entry
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (spellings.Count > 0)
                groups.Add(spellings);
        }

        return groups.Count > 0 ? groups : null;
    }
}
