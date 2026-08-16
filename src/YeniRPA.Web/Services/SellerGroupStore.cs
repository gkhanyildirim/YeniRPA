using System.Text.Json;
using ClosedXML.Excel;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Owns <c>%LOCALAPPDATA%\YeniRPA\WhatsApp\seller-groups.json</c> — the seller → WhatsApp group
/// mapping and the operator's edited message templates.
///
/// <para>Deliberately <b>not</b> encrypted, unlike <c>MiraklBrowser</c>'s <c>auth.dat</c>. Those are
/// session cookies granting full operator access to the marketplace; these are group names and Turkish
/// message copy. The inconsistency between the two is the point, not an oversight.</para>
///
/// <para>This is the only data in the whole app that cannot be regenerated from an export, which is
/// why <see cref="Save"/> keeps a backup generation and replaces the file atomically.</para>
/// </summary>
public sealed class SellerGroupStore
{
    const int CurrentVersion = 1;

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

    /// <summary>Load and save are one operation each; a singleton reachable from two controller
    /// actions and the runner needs them not to interleave.</summary>
    readonly object _sync = new();

    public SellerGroupStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA",
            "WhatsApp");
        Directory.CreateDirectory(directory);

        FilePath = Path.Combine(directory, "seller-groups.json");
        BackupPath = FilePath + ".bak";
    }

    public string FilePath { get; }
    public string BackupPath { get; }

    /// <summary>
    /// Reads the file every call rather than caching it. It is a few KB; a cache would buy nothing and
    /// would go stale the moment someone edited the JSON by hand.
    /// </summary>
    public SellerGroupFile Load()
    {
        lock (_sync)
        {
            if (!File.Exists(FilePath))
                return new SellerGroupFile(CurrentVersion, null, null, null, []);

            string json;
            try
            {
                json = File.ReadAllText(FilePath);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"The seller/group mapping could not be read from {FilePath}: {ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(json))
                return new SellerGroupFile(CurrentVersion, null, null, null, []);

            SellerGroupFile? file;
            try
            {
                file = JsonSerializer.Deserialize<SellerGroupFile>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                // Never silently start over: that would look identical to "the mapping vanished".
                throw new InvalidOperationException(
                    $"The seller/group mapping at {FilePath} is not valid JSON ({ex.Message}). " +
                    $"The previous version is kept at {BackupPath}.", ex);
            }

            if (file is null)
                return new SellerGroupFile(CurrentVersion, null, null, null, []);

            return file with { Entries = file.Entries ?? [] };
        }
    }

    /// <summary>
    /// Writes to a temp file and moves it over the original, keeping one backup generation. A torn
    /// write here would destroy a hand-built mapping table with nothing to rebuild it from.
    /// </summary>
    public void Save(SellerGroupFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var stamped = file with
        {
            Version = CurrentVersion,
            UpdatedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'")
        };

        lock (_sync)
        {
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(stamped, JsonOptions));

            if (File.Exists(FilePath))
                File.Copy(FilePath, BackupPath, overwrite: true);

            File.Move(tempPath, FilePath, overwrite: true);
        }
    }

    public SellerGroupMap BuildMap() => SellerGroupMap.FromEntries(Load().Entries);

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
