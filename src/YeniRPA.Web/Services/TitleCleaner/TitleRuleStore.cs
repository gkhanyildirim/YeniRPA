using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>
/// Owns <c>%LOCALAPPDATA%\YeniRPA\TitleCleaner\title-rules.json</c> — every category's naming
/// standard.
///
/// <para>Modelled on <see cref="SellerGroupStore"/> and for the same reason: like the seller/group
/// mapping, this is data that exists nowhere else. It is not derived from an export and it cannot be
/// rebuilt by re-running anything — it is what the category team decided a laptop title should look
/// like. So the write is atomic, one backup generation is kept, and a file that will not parse is an
/// error rather than a silent fresh start.</para>
///
/// <para>Not encrypted, also like <see cref="SellerGroupStore"/>: these are column names and unit
/// spellings, not credentials.</para>
/// </summary>
public sealed class TitleRuleStore
{
    const int CurrentVersion = 1;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // The file is meant to be readable, and hand-editable in a pinch: "Measure" says what it is
        // where a bare 1 does not.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Load and save are one operation each; a singleton reachable from several controller
    /// actions needs them not to interleave.</summary>
    readonly object _sync = new();

    public TitleRuleStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA",
            "TitleCleaner");
        Directory.CreateDirectory(directory);

        FilePath = Path.Combine(directory, "title-rules.json");
        BackupPath = FilePath + ".bak";
    }

    public string FilePath { get; }
    public string BackupPath { get; }

    /// <summary>Reads the file every call rather than caching it — it is a few KB, and a cache would
    /// go stale the moment someone edited the JSON by hand.</summary>
    public TitleRuleFile Load()
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
                    $"The title rule sets could not be read from {FilePath}: {ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(json))
                return Empty();

            try
            {
                return Parse(json);
            }
            catch (JsonException ex)
            {
                // Never silently start over: an empty list looks exactly like "the rule sets vanished",
                // and these are hand-built.
                throw new InvalidOperationException(
                    $"The title rule sets at {FilePath} are not valid JSON ({ex.Message}). " +
                    $"The previous version is kept at {BackupPath}.", ex);
            }
        }
    }

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
            EncodeUnits(rule.UnitList),
            EncodeAliases(rule.AliasGroups))).ToList(),
        set.DecimalSeparator == "," ? "," : ".");

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
                    ParseUnits(a.Units),
                    ParseAliases(a.Aliases)))
                .ToList(),
            form.DecimalSeparator == "," ? "," : ".");
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

    /// <summary>Writes to a temp file and moves it over the original, keeping one backup generation.</summary>
    public void Save(TitleRuleFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var stamped = file with
        {
            Version = CurrentVersion,
            UpdatedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Sets = file.Sets ?? [],
        };

        lock (_sync)
        {
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(stamped, JsonOptions), Encoding.UTF8);

            if (File.Exists(FilePath))
                File.Copy(FilePath, BackupPath, overwrite: true);

            File.Move(tempPath, FilePath, overwrite: true);
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

    static readonly string[] Headers =
    [
        "Kural Seti", "Başlık Kolonu", "Ondalık Ayracı", "Kolon", "Tip",
        "Çıkar", "Düzelt", "Başlıktan Doldur", "Birimler", "Eşanlamlılar",
    ];

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
        sheet.Columns(SetColumn, AliasColumn).Style.NumberFormat.Format = "@";

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
                row++;
            }
        }

        sheet.Columns(SetColumn, AliasColumn).AdjustToContents();
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
        var cAlias = Optional(header, Headers[AliasColumn - 1]);

        // Insertion-ordered, because attribute order inside a set decides which of two attributes
        // claims a stretch of title that both could match.
        var sets = new List<(string Name, string Title, string Separator, List<TitleAttributeRule> Rules)>();

        foreach (var row in table.Skip(1))
        {
            var setName = TabularFile.GetCell(row, cSet).Trim();
            var column = TabularFile.GetCell(row, cColumn).Trim();

            if (setName.Length == 0 || column.Length == 0)
                continue;

            var titleColumn = TabularFile.GetCell(row, cTitle).Trim();
            var separator = TabularFile.GetCell(row, cDecimal).Trim() == "," ? "," : ".";

            var existing = sets.FirstOrDefault(s =>
                string.Equals(FoldedTitle.Fold(s.Name), FoldedTitle.Fold(setName), StringComparison.Ordinal));

            if (existing.Rules is null)
            {
                existing = (setName, titleColumn, separator, []);
                sets.Add(existing);
            }

            existing.Rules.Add(new TitleAttributeRule(
                column,
                ParseKind(TabularFile.GetCell(row, cKind)),
                ParseBool(TabularFile.GetCell(row, cRemove), fallback: true),
                ParseBool(TabularFile.GetCell(row, cCorrect), fallback: true),
                ParseBool(TabularFile.GetCell(row, cFill), fallback: false),
                ParseUnits(TabularFile.GetCell(row, cUnits)),
                ParseAliases(TabularFile.GetCell(row, cAlias))));
        }

        if (sets.Count == 0)
            throw new InvalidOperationException("The rule set file carries no rows.");

        return sets
            .Select(s => new TitleRuleSet(s.Name, s.Title, s.Rules, s.Separator))
            .ToList();
    }

    static int Require(Dictionary<string, int> header, string name) =>
        header.TryGetValue(name, out var index)
            ? index
            : throw new InvalidOperationException($"Required column '{name}' was not found in the rule set file.");

    static int? Optional(Dictionary<string, int> header, string name) =>
        header.TryGetValue(name, out var index) ? index : null;

    // ---------------------------------------------------------------------
    // Cell encoding
    // ---------------------------------------------------------------------
    //
    // Units and alias groups are lists of lists, and a spreadsheet cell holds one string. The
    // encoding is deliberately plain enough to edit by hand:
    //
    //   Birimler      GB=gb|gbyte|gigabayt@1 ; TB=tb|terabayt@1024
    //   Eşanlamlılar  W11P|Windows 11 Pro|Win 11 Pro ; W11H|Windows 11 Home
    //
    // In both, ";" separates entries and "|" separates spellings. For a unit, what precedes "="
    // is the canonical spelling and what follows "@" is its size in the base unit.

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

    static TitleAttributeKind ParseKind(string raw) => FoldedTitle.Fold(raw) switch
    {
        "measure" or "olcu" or "olculu" or "birim" => TitleAttributeKind.Measure,
        "alias" or "esanlamli" or "katalog" => TitleAttributeKind.Alias,
        _ => TitleAttributeKind.Text,
    };

    internal static string EncodeUnits(IReadOnlyList<MeasureUnit> units)
    {
        if (units.Count == 0)
            return "";

        return string.Join(" ; ", units.Select(unit =>
        {
            var spellings = (unit.Spellings ?? []).Where(s => !string.IsNullOrWhiteSpace(s));
            var text = unit.Canonical + "=" + string.Join("|", spellings);

            return unit.Factor > 0
                ? text + "@" + unit.Factor.ToString("0.##########", CultureInfo.InvariantCulture)
                : text;
        }));
    }

    static IReadOnlyList<MeasureUnit>? ParseUnits(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return null;

        var units = new List<MeasureUnit>();

        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

    internal static string EncodeAliases(IReadOnlyList<IReadOnlyList<string>> groups)
    {
        if (groups.Count == 0)
            return "";

        return string.Join(" ; ", groups
            .Where(group => group is { Count: > 0 })
            .Select(group => string.Join("|", group.Where(s => !string.IsNullOrWhiteSpace(s)))));
    }

    static IReadOnlyList<IReadOnlyList<string>>? ParseAliases(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return null;

        var groups = new List<IReadOnlyList<string>>();

        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
