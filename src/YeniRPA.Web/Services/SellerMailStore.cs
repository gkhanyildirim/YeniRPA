using System.Text.Json;
using ClosedXML.Excel;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Owns <c>%LOCALAPPDATA%\YeniRPA\Mail\seller-mails.json</c> — the seller → e-mail → attachment
/// mapping, the operator's edited subject and body templates, and the attachment folder.
///
/// <para>Deliberately a near-copy of <see cref="SellerGroupStore"/> rather than a shared base class.
/// The two files hold different shapes and are read by different modules; a common base would make a
/// fix to one silently change the other, and the whole point of both is that a wrong row here sends
/// one seller's data to a different seller.</para>
///
/// <para>Like the WhatsApp mapping this is the only data in the module that cannot be regenerated
/// from an export, which is why <see cref="Save"/> keeps a backup generation and replaces the file
/// atomically.</para>
/// </summary>
public sealed class SellerMailStore
{
    const int CurrentVersion = 1;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Header names accepted by the Excel import. The first of each is what the operator's
    /// own <c>satici_mail_eslesme.xlsx</c> already writes, so that file imports untouched.</summary>
    static readonly string[] SellerNameHeaders = ["Seller", "Seller name", "Satıcı", "Satıcı Adı"];
    static readonly string[] SellerIdHeaders = ["SellerId", "Seller ID", "Seller Id", "Satıcı Id"];
    static readonly string[] EmailHeaders = ["Email", "E-mail", "Mail", "E-posta", "Eposta"];
    static readonly string[] FileNameHeaders = ["DosyaAdi", "Dosya Adı", "Dosya Adi", "File", "File name"];
    static readonly string[] LeadTime0Headers = ["LeadTime0", "Lead time 0", "Lead Time 0"];
    static readonly string[] LeadTime1Headers = ["LeadTime1", "Lead time 1", "Lead Time 1"];

    const int SellerNameColumn = 1;
    const int SellerIdColumn = 2;
    const int EmailColumn = 3;
    const int FileNameColumn = 4;
    const int LeadTime0Column = 5;
    const int LeadTime1Column = 6;

    /// <summary>Load and save are one operation each; a singleton reachable from several controller
    /// actions and the runner needs them not to interleave.</summary>
    readonly object _sync = new();

    public SellerMailStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YeniRPA");

        var directory = Path.Combine(root, "Mail");
        Directory.CreateDirectory(directory);

        FilePath = Path.Combine(directory, "seller-mails.json");
        BackupPath = FilePath + ".bak";

        // Not created here: an empty folder that looks configured is worse than one the panel reports
        // as missing, because the first tells you nothing about why nothing matched.
        DefaultAttachmentFolder = Path.Combine(root, "Offers");
    }

    public string FilePath { get; }
    public string BackupPath { get; }

    /// <summary>Where the per-seller offer workbooks are expected when the operator has not set a
    /// folder of their own.</summary>
    public string DefaultAttachmentFolder { get; }

    /// <summary>Reads the file every call rather than caching it — a few KB, and a cache would go
    /// stale the moment someone edited the JSON by hand.</summary>
    public SellerMailFile Load()
    {
        lock (_sync)
        {
            if (!File.Exists(FilePath))
                return Empty();

            string json;
            try
            {
                json = File.ReadAllText(FilePath);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"The seller/e-mail mapping could not be read from {FilePath}: {ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(json))
                return Empty();

            SellerMailFile? file;
            try
            {
                file = JsonSerializer.Deserialize<SellerMailFile>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                // Never silently start over: that would look identical to "the mapping vanished".
                throw new InvalidOperationException(
                    $"The seller/e-mail mapping at {FilePath} is not valid JSON ({ex.Message}). " +
                    $"The previous version is kept at {BackupPath}.", ex);
            }

            if (file is null)
                return Empty();

            return file with { Entries = file.Entries ?? [] };
        }
    }

    /// <summary>Writes to a temp file and moves it over the original, keeping one backup generation.
    /// A torn write here would destroy a hand-built 188-row table with nothing to rebuild it from.</summary>
    public void Save(SellerMailFile file)
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

    /// <summary>The folder the module should read attachments from: the saved one when set, the
    /// default otherwise.</summary>
    public string ResolveAttachmentFolder(SellerMailFile file) =>
        string.IsNullOrWhiteSpace(file.AttachmentFolder) ? DefaultAttachmentFolder : file.AttachmentFolder.Trim();

    SellerMailFile Empty() => new(CurrentVersion, null, null, null, null, []);

    // ---------------------------------------------------------------------
    // Table-level problems
    // ---------------------------------------------------------------------

    /// <summary>
    /// Problems that are properties of the table rather than of any one row, detected once and shown
    /// above the editor — the counterpart of <see cref="SellerGroupMap.LoadWarnings"/>.
    ///
    /// <para>A repeated file name is the one worth staring at: it means two sellers are set up to
    /// receive the same offer list, which is the exact shape of the leak this module exists to
    /// avoid.</para>
    /// </summary>
    public static IReadOnlyList<string> FindTableProblems(IReadOnlyList<SellerMailEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var warnings = new List<string>();

        // An address on several rows is legitimate — one agency can run three storefronts, and each
        // of those mails carries a different seller's offer list, so all three have to go out. It is
        // still worth saying out loud: someone about to receive three mails in a minute is the kind
        // of thing an operator wants to have known in advance rather than been told about.
        foreach (var group in entries
            .SelectMany(e => SplitAddresses(e.Email).Select(address => (Address: address, Entry: e)))
            .GroupBy(x => NormalizeEmail(x.Address))
            .Where(g => g.Select(x => x.Entry).Distinct().Count() > 1))
        {
            var sellers = group.Select(x => x.Entry.SellerName).Distinct().Count();
            warnings.Add($"'{group.First().Address}' is a recipient for {sellers} sellers and will receive {sellers} separate mails, one per attachment.");
        }

        foreach (var group in entries
            .Where(e => e.FileName.Trim().Length > 0)
            .GroupBy(e => e.FileName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
        {
            warnings.Add($"'{group.Key}' is the attachment for {group.Count()} sellers. One seller's offer list would go to another — check the file names.");
        }

        foreach (var group in entries
            .Where(e => SellerGroupMap.NormalizeSellerId(e.SellerId).Length > 0)
            .GroupBy(e => SellerGroupMap.NormalizeSellerId(e.SellerId), StringComparer.Ordinal)
            .Where(g => g.Count() > 1))
        {
            warnings.Add($"Seller id '{group.Key}' is on more than one row.");
        }

        return warnings;
    }

    /// <summary>The comparison key for an address: trimmed and lowercased. Not a validity check —
    /// see <see cref="LooksLikeEmail"/> for that.</summary>
    public static string NormalizeEmail(string raw) => (raw ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Splits one <c>Email</c> cell into the addresses it holds.
    ///
    /// <para>A seller often has several users in the Mirakl back office and all of them belong on one
    /// mail, so the cell holds a list. Both <c>;</c> and <c>,</c> separate, because a cell filled in by
    /// hand from Outlook uses the first and one pasted out of a spreadsheet often uses the second.</para>
    ///
    /// <para>Order is preserved and repeats <em>within the cell</em> are dropped: the same person
    /// listed twice on one seller would otherwise appear twice in the To line. Repeats across
    /// different rows are a different question and are deliberately left alone — see
    /// <see cref="FindTableProblems"/>.</para>
    /// </summary>
    public static IReadOnlyList<string> SplitAddresses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var addresses = new List<string>();

        foreach (var part in raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(NormalizeEmail(part)))
                addresses.Add(part);
        }

        return addresses;
    }

    /// <summary>How a list of addresses is written back into one cell, and into Outlook's To line.</summary>
    public static string JoinAddresses(IEnumerable<string> addresses) => string.Join("; ", addresses);

    /// <summary>
    /// Deliberately shallow: one <c>@</c>, something either side, no whitespace, a dot in the domain.
    ///
    /// <para>Not <see cref="System.Net.Mail.MailAddress"/>, which accepts <c>"a b"@c</c> and display
    /// names — shapes that are legal RFC 5322 and always a typo in this table. The check exists to
    /// catch a mangled cell before Outlook is asked to send to it, not to be a parser.</para>
    /// </summary>
    public static bool LooksLikeEmail(string raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length < 5 || value.Any(char.IsWhiteSpace))
            return false;

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
            return false;

        var domain = value[(at + 1)..];
        var dot = domain.IndexOf('.');
        return dot > 0 && dot < domain.Length - 1;
    }

    // ---------------------------------------------------------------------
    // Excel round trip
    // ---------------------------------------------------------------------

    /// <summary>
    /// A plain six-column sheet, deliberately not <see cref="TableWorkbookBuilder"/> — that writes a
    /// styled report with title rows above the data, which <see cref="ReadWorkbook"/> could not read
    /// back. Same call and same reason as <see cref="SellerGroupStore.BuildWorkbook"/>.
    /// </summary>
    public static byte[] BuildWorkbook(IReadOnlyList<SellerMailEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Seller mails");

        sheet.Cell(1, SellerNameColumn).Value = SellerNameHeaders[0];
        sheet.Cell(1, SellerIdColumn).Value = SellerIdHeaders[0];
        sheet.Cell(1, EmailColumn).Value = EmailHeaders[0];
        sheet.Cell(1, FileNameColumn).Value = FileNameHeaders[0];
        sheet.Cell(1, LeadTime0Column).Value = LeadTime0Headers[0];
        sheet.Cell(1, LeadTime1Column).Value = LeadTime1Headers[0];
        sheet.Row(1).Style.Font.Bold = true;

        // Text, not numbers: a seller id of "08664" loses its leading zero on the round trip
        // otherwise, and the id is half of what identifies the row.
        sheet.Columns(SellerNameColumn, FileNameColumn).Style.NumberFormat.Format = "@";

        for (var i = 0; i < entries.Count; i++)
        {
            var row = i + 2;
            sheet.Cell(row, SellerNameColumn).SetValue(entries[i].SellerName);
            sheet.Cell(row, SellerIdColumn).SetValue(entries[i].SellerId);
            sheet.Cell(row, EmailColumn).SetValue(entries[i].Email);
            sheet.Cell(row, FileNameColumn).SetValue(entries[i].FileName);
            sheet.Cell(row, LeadTime0Column).SetValue(entries[i].LeadTime0);
            sheet.Cell(row, LeadTime1Column).SetValue(entries[i].LeadTime1);
        }

        sheet.Columns(SellerNameColumn, LeadTime1Column).AdjustToContents();

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
    }

    /// <summary>Reads a mapping workbook or CSV. The file name is load-bearing — see
    /// <c>TabularFile.Read</c>.</summary>
    public static List<SellerMailEntry> ReadWorkbook(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("The mapping file is empty.");

        var header = TabularFile.BuildHeaderIndex(table[0]);

        var cEmail = FindColumn(header, EmailHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{EmailHeaders[0]}' was not found in the mapping file.");

        var cFile = FindColumn(header, FileNameHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{FileNameHeaders[0]}' was not found in the mapping file.");

        var cId = FindColumn(header, SellerIdHeaders);
        var cName = FindColumn(header, SellerNameHeaders);

        if (cId is null && cName is null)
        {
            throw new InvalidOperationException(
                $"The mapping file needs a '{SellerIdHeaders[0]}' or a '{SellerNameHeaders[0]}' column to identify sellers by.");
        }

        var cLead0 = FindColumn(header, LeadTime0Headers);
        var cLead1 = FindColumn(header, LeadTime1Headers);

        var entries = new List<SellerMailEntry>();
        foreach (var row in table.Skip(1))
        {
            var id = SellerGroupMap.NormalizeSellerId(TabularFile.GetCell(row, cId));
            var name = TabularFile.GetCell(row, cName).Trim();

            // A row with nothing identifying a seller cannot map anything; a row with no address or
            // no file is a legitimate "seen but not finished" entry and is kept.
            if (id.Length == 0 && name.Length == 0)
                continue;

            entries.Add(new SellerMailEntry(
                SellerId: id,
                SellerName: name,
                Email: TabularFile.GetCell(row, cEmail).Trim(),
                FileName: TabularFile.GetCell(row, cFile).Trim(),
                LeadTime0: ReadCount(TabularFile.GetCell(row, cLead0)),
                LeadTime1: ReadCount(TabularFile.GetCell(row, cLead1))));
        }

        return entries;
    }

    /// <summary>A count cell. Blank and unreadable both become 0 — the number is quoted in the
    /// message, and refusing the whole import over one bad cell helps nobody.</summary>
    static int ReadCount(string raw)
    {
        var value = TabularFile.ParseNumber(raw);
        return value > 0 ? (int)Math.Round(value) : 0;
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
