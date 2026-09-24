using System.Text.Json;
using ClosedXML.Excel;
using LiteDB;
using YeniRPA.Web.Models;
// LiteDB also declares a JsonSerializer type; this app's JSON is always System.Text.Json's.
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace YeniRPA.Web.Services;

/// <summary>The instance surface <see cref="SellerGroupStore"/> exposes through DI. The Excel round
/// trip (<see cref="SellerGroupStore.ReadWorkbook"/>, <see cref="SellerGroupStore.BuildWorkbook"/>)
/// stays static on the concrete class — nothing about it depends on where the mapping is stored.</summary>
public interface ISellerGroupStore
{
    /// <summary>Where the data now lives — the shared LiteDB file, not this store's own JSON file
    /// any more. Kept on the interface because the panel shows it.</summary>
    string FilePath { get; }

    SellerGroupFile Load();

    /// <summary>
    /// Replaces the whole document. Only for a restore, which legitimately owns every field —
    /// a module saving its own settings must use one of the two focused methods below, or it wipes
    /// the other module's half of the record.
    /// </summary>
    void Save(SellerGroupFile file);

    /// <summary>Saves the shared mapping table and Late Order Warnings' two templates, leaving the
    /// Incident Warnings fields exactly as they were.</summary>
    void SaveMapping(IReadOnlyList<SellerGroupEntry> entries, string? messageTemplate, string? orderLineTemplate);

    /// <summary>Saves Incident Warnings' templates and chase threshold, leaving the mapping table and
    /// Late Order Warnings' templates exactly as they were.</summary>
    void SaveIncidentSettings(string? messageTemplate, string? lineTemplate, int thresholdDays);

    /// <summary>Saves Stockout Warnings' templates and GMV threshold, leaving the mapping table and
    /// the other two modules' fields exactly as they were.</summary>
    void SaveStockoutSettings(string? messageTemplate, string? productLineTemplate, double gmvThreshold);

    /// <summary>Saves only the mapping table, leaving every module's message templates exactly as
    /// they were. Used by a module's own mapping editor (Stockout Warnings has one; Incident
    /// Warnings does not and edits the table from the Late Order Warnings tab instead) so that
    /// editing sellers from there can never touch another module's wording.</summary>
    void SaveEntries(IReadOnlyList<SellerGroupEntry> entries);

    SellerGroupMap BuildMap();

    /// <summary>One-time import from <c>seller-groups.json</c>, run by <see cref="JsonToLiteDbMigrator"/>
    /// at startup. A no-op once this store already holds a LiteDB document.</summary>
    void MigrateLegacyJson();
}

/// <summary>
/// Owns the seller → WhatsApp group mapping and the operator's edited message templates, in the
/// <c>sellerGroups</c> collection of the shared LiteDB database (<see cref="ILiteDbContext"/>).
///
/// <para>Deliberately <b>not</b> encrypted, unlike <c>MiraklBrowser</c>'s <c>auth.dat</c>. Those are
/// session cookies granting full operator access to the marketplace; these are group names and Turkish
/// message copy. The inconsistency between the two is the point, not an oversight.</para>
///
/// <para>This is the only data in the whole app that cannot be regenerated from an export. It used to
/// live in its own hand-rolled atomic-write-plus-backup JSON file for exactly that reason; LiteDB's
/// own write-ahead log gives the same crash-safety guarantee without this class having to implement
/// it, so <see cref="Save"/> is now a single <c>Upsert</c>.</para>
/// </summary>
public sealed class SellerGroupStore : ISellerGroupStore
{
    const int CurrentVersion = 1;
    const int DocumentId = 1;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Header names accepted by the Excel import, first is what the export writes.</summary>
    static readonly string[] SellerIdHeaders = ["Seller ID", "SellerId", "Seller Id", "Satıcı Id"];
    static readonly string[] SellerNameHeaders = ["Seller", "Seller name", "Satıcı", "Satıcı Adı"];
    static readonly string[] GroupHeaders = ["WhatsApp group", "WhatsApp Group", "Group", "Grup"];

    const int SellerIdColumn = 1;
    const int SellerNameColumn = 2;
    const int GroupColumn = 3;

    /// <summary>One document holding the whole mapping table, the same aggregate <c>Load</c>/<c>Save</c>
    /// always dealt in — the collection exists so this fits through <see cref="ILiteDbContext"/>, not
    /// because the mapping is queried row by row anywhere in the app.</summary>
    public sealed class Document
    {
        public int Id { get; set; }
        public SellerGroupFile Data { get; set; } = null!;
    }

    readonly ILiteCollection<Document> _collection;

    /// <summary>
    /// Serialises the load-modify-save sequences below. LiteDB makes each individual call atomic, not
    /// a read and a write across it — and here the two halves of one document are written by two
    /// different panels, so an interleave would drop whichever module read first.
    /// </summary>
    readonly object _sync = new();

    public SellerGroupStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        FilePath = context.DatabasePath;
        _collection = context.GetCollection<Document>("sellerGroups");
        _collection.EnsureIndex(x => x.Id, unique: true);
    }

    public string FilePath { get; }

    public SellerGroupFile Load() =>
        _collection.FindById(DocumentId)?.Data ?? new SellerGroupFile(CurrentVersion, null, null, null, []);

    public void Save(SellerGroupFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        lock (_sync) SaveCore(file);
    }

    /// <summary>
    /// Late Order Warnings' save. Reads the current document first so the Incident Warnings fields
    /// survive: the mapping panel does not post them, and building a fresh <c>SellerGroupFile</c> from
    /// its request — which is what this used to do — silently deleted the other module's templates.
    /// </summary>
    public void SaveMapping(IReadOnlyList<SellerGroupEntry> entries, string? messageTemplate, string? orderLineTemplate)
    {
        lock (_sync)
        {
            SaveCore(Load() with
            {
                Entries = entries ?? [],
                MessageTemplate = messageTemplate,
                OrderLineTemplate = orderLineTemplate,
            });
        }
    }

    /// <summary>The mirror of <see cref="SaveMapping"/>: writes only the Incident Warnings fields and
    /// leaves the mapping table and the late-order templates untouched.</summary>
    public void SaveIncidentSettings(string? messageTemplate, string? lineTemplate, int thresholdDays)
    {
        lock (_sync)
        {
            SaveCore(Load() with
            {
                IncidentMessageTemplate = messageTemplate,
                IncidentLineTemplate = lineTemplate,
                IncidentThresholdDays = thresholdDays,
            });
        }
    }

    /// <summary>The mirror of <see cref="SaveIncidentSettings"/>: writes only the Stockout Warnings
    /// fields and leaves the mapping table and the other two modules' fields untouched.</summary>
    public void SaveStockoutSettings(string? messageTemplate, string? productLineTemplate, double gmvThreshold)
    {
        lock (_sync)
        {
            SaveCore(Load() with
            {
                StockoutMessageTemplate = messageTemplate,
                StockoutProductLineTemplate = productLineTemplate,
                StockoutGmvThreshold = gmvThreshold,
            });
        }
    }

    /// <summary>Writes only <see cref="SellerGroupFile.Entries"/>. A module whose own panel edits
    /// the mapping table (rather than pointing the operator at Late Order Warnings' editor) saves
    /// through this, not <see cref="SaveMapping"/> — that one also carries Late Order Warnings'
    /// templates, and a caller with nothing to say about those would otherwise blank them.</summary>
    public void SaveEntries(IReadOnlyList<SellerGroupEntry> entries)
    {
        lock (_sync)
        {
            SaveCore(Load() with { Entries = entries ?? [] });
        }
    }

    /// <summary>Stamps and upserts. Callers hold <see cref="_sync"/>.</summary>
    void SaveCore(SellerGroupFile file)
    {
        var stamped = file with
        {
            Version = CurrentVersion,
            UpdatedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            Entries = file.Entries ?? [],
        };

        _collection.Upsert(new Document { Id = DocumentId, Data = stamped });
    }

    public SellerGroupMap BuildMap() => SellerGroupMap.FromEntries(Load().Entries);

    /// <summary>The pre-LiteDB location, read once at startup and never again.</summary>
    internal static string LegacyJsonPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YeniRPA", "WhatsApp", "seller-groups.json");

    public void MigrateLegacyJson()
    {
        if (_collection.Count() > 0)
            return;

        var path = LegacyJsonPath();
        if (!File.Exists(path))
            return;

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return;

        SellerGroupFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SellerGroupFile>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // A legacy file that no longer parses is not a reason to fail startup: Load() would have
            // refused it under the old code too, and there is nothing here worth carrying over.
            return;
        }

        if (file is not null)
            Save(file with { Entries = file.Entries ?? [] });
    }

    // ---------------------------------------------------------------------
    // Excel round trip
    // ---------------------------------------------------------------------

    /// <summary>
    /// A plain three-column sheet, deliberately not <see cref="TableWorkbookBuilder"/> — that writes a
    /// styled report with title rows above the data, which <see cref="ReadWorkbook"/> could not read
    /// back. Same call and same reason as <c>CreateReturnController.BuildWorkbook</c>.
    /// </summary>
    public static byte[] BuildWorkbook(IReadOnlyList<SellerGroupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Seller groups");

        sheet.Cell(1, SellerIdColumn).Value = SellerIdHeaders[0];
        sheet.Cell(1, SellerNameColumn).Value = SellerNameHeaders[0];
        sheet.Cell(1, GroupColumn).Value = GroupHeaders[0];
        sheet.Row(1).Style.Font.Bold = true;

        // Text, not numbers: a seller id of "08664" loses its leading zero on the round trip otherwise,
        // and the id is the authoritative half of the mapping.
        sheet.Column(SellerIdColumn).Style.NumberFormat.Format = "@";
        sheet.Column(SellerNameColumn).Style.NumberFormat.Format = "@";
        sheet.Column(GroupColumn).Style.NumberFormat.Format = "@";

        for (var i = 0; i < entries.Count; i++)
        {
            sheet.Cell(i + 2, SellerIdColumn).SetValue(entries[i].SellerId);
            sheet.Cell(i + 2, SellerNameColumn).SetValue(entries[i].SellerName);
            sheet.Cell(i + 2, GroupColumn).SetValue(entries[i].GroupName);
        }

        sheet.Columns(SellerIdColumn, GroupColumn).AdjustToContents();

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
    }

    /// <summary>Reads a mapping workbook or CSV. The file name is load-bearing — see
    /// <see cref="TabularFile.Read"/>.</summary>
    public static List<SellerGroupEntry> ReadWorkbook(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("The mapping file is empty.");

        var header = TabularFile.BuildHeaderIndex(table[0]);

        var cGroup = FindColumn(header, GroupHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{GroupHeaders[0]}' was not found in the mapping file.");

        var cId = FindColumn(header, SellerIdHeaders);
        var cName = FindColumn(header, SellerNameHeaders);

        if (cId is null && cName is null)
        {
            throw new InvalidOperationException(
                $"The mapping file needs a '{SellerIdHeaders[0]}' or a '{SellerNameHeaders[0]}' column to match sellers on.");
        }

        var entries = new List<SellerGroupEntry>();
        foreach (var row in table.Skip(1))
        {
            var id = SellerGroupMap.NormalizeSellerId(TabularFile.GetCell(row, cId));
            var name = TabularFile.GetCell(row, cName).Trim();
            var group = TabularFile.GetCell(row, cGroup).Trim();

            // A row with nothing to match on cannot map anything; a row with no group is a legitimate
            // "seen but not finished" entry and is kept.
            if (id.Length == 0 && name.Length == 0)
                continue;

            entries.Add(new SellerGroupEntry(id, name, group));
        }

        return entries;
    }

    static int? FindColumn(Dictionary<string, int> header, string[] names)
    {
        foreach (var name in names)
        {
            if (header.TryGetValue(name, out var index))
                return index;
        }
        return null;
    }
}
