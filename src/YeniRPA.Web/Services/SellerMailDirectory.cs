namespace YeniRPA.Web.Services;

/// <summary>The outcome of looking one seller up. Exactly one side is ever set.</summary>
public readonly record struct DirectoryMatch(string? Email, string? Problem);

/// <summary>
/// An immutable snapshot of the uploaded seller → e-mail list, built once per request. Pure and
/// IO-free once constructed, so the matching rule is testable without a 700-row workbook.
///
/// <para><b>There is no fuzzy matching here, and none may ever be added.</b> Not Levenshtein, not
/// <c>Contains</c>, not "closest seller". The measured cost of that rule on the real files is eight
/// sellers out of 131 that have to be entered by hand; the cost of relaxing it is that
/// <c>Yazıcı Bende</c> — which is in neither list — lands on <c>Yazıcı Ticaret</c>, and one seller
/// receives another's complete price and stock list. Every ambiguity below is reported for the
/// operator to resolve, never guessed at. Same rule, and the same reason, as
/// <see cref="SellerGroupMap"/>.</para>
/// </summary>
public sealed class SellerMailDirectory
{
    /// <summary>The sheet the onboarding workbook keeps addresses on. Its first sheet is a funnel
    /// summary with no address column at all, so the name is not optional there.</summary>
    public const string DefaultSheetName = "Data";

    static readonly string[] SellerNameHeaders =
        ["Satıcı", "Satici", "Seller", "Seller name", "Satıcı Adı", "Shopname MIRAKL"];

    static readonly string[] EmailHeaders = ["Mail", "E-mail", "Email", "E-posta", "Eposta"];

    static readonly string[] SellerIdHeaders =
        ["Seller id", "Seller ID", "SellerId", "Satıcı Id", "MIRAKL ID"];

    readonly Dictionary<string, string> _byId;
    readonly Dictionary<string, string> _byName;
    readonly HashSet<string> _ambiguousIds;
    readonly HashSet<string> _ambiguousNames;

    SellerMailDirectory(
        Dictionary<string, string> byId,
        Dictionary<string, string> byName,
        HashSet<string> ambiguousIds,
        HashSet<string> ambiguousNames,
        int rowCount,
        IReadOnlyList<string> warnings)
    {
        _byId = byId;
        _byName = byName;
        _ambiguousIds = ambiguousIds;
        _ambiguousNames = ambiguousNames;
        RowCount = rowCount;
        Warnings = warnings;
    }

    /// <summary>How many usable rows the file yielded, so "0 matched" can be told apart from "the
    /// wrong sheet was read".</summary>
    public int RowCount { get; }

    /// <summary>Problems that are properties of the file rather than of any one lookup, detected once
    /// here and shown above the results — the counterpart of <see cref="SellerGroupMap.LoadWarnings"/>.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Reads the address list. <paramref name="sheetName"/> selects the sheet in a multi-sheet
    /// workbook; null or blank reads the first, which is what a purpose-built single-sheet list wants.
    /// </summary>
    public static SellerMailDirectory Read(Stream stream, string fileName, string? sheetName)
    {
        var table = TabularFile.Read(stream, fileName, sheetName);
        if (table.Count == 0)
            throw new InvalidOperationException("The seller address list is empty.");

        var header = TabularFile.BuildHeaderIndex(table[0]);

        var cEmail = FindColumn(header, EmailHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{EmailHeaders[0]}' was not found in the seller address list" +
                (string.IsNullOrWhiteSpace(sheetName) ? "." : $" on sheet '{sheetName}'."));

        var cId = FindColumn(header, SellerIdHeaders);
        var cName = FindColumn(header, SellerNameHeaders);

        if (cId is null && cName is null)
        {
            throw new InvalidOperationException(
                $"The seller address list needs a '{SellerNameHeaders[0]}' or a '{SellerIdHeaders[0]}' " +
                "column to match sellers on.");
        }

        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguousIds = new HashSet<string>(StringComparer.Ordinal);
        var ambiguousNames = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        var rows = 0;
        var brokenCells = 0;

        foreach (var row in table.Skip(1))
        {
            var rawEmail = TabularFile.GetCell(row, cEmail).Trim();
            var id = SellerGroupMap.NormalizeSellerId(TabularFile.GetCell(row, cId));
            var name = TabularFile.GetCell(row, cName).Trim();

            if (id.Length == 0 && name.Length == 0)
                continue;

            // Spreadsheet error values ("#N/A", "#REF!") are what a broken lookup leaves behind. They
            // are not addresses and must not be treated as "this seller has one".
            if (rawEmail.StartsWith('#'))
            {
                brokenCells++;
                continue;
            }

            // Canonicalised on the way in, so a cell holding several users comes out using one
            // separator and holding no repeats.
            var email = SellerMailStore.JoinAddresses(SellerMailStore.SplitAddresses(rawEmail));
            if (email.Length == 0)
                continue;

            rows++;

            if (id.Length > 0)
                Add(byId, ambiguousIds, id, email);

            var folded = SellerGroupMap.FoldName(name);
            if (folded.Length > 0)
                Add(byName, ambiguousNames, folded, email);
        }

        if (brokenCells > 0)
        {
            warnings.Add(
                $"{brokenCells:N0} row(s) in the address list hold a spreadsheet error instead of an " +
                "address (#N/A, #REF!) and were skipped.");
        }

        foreach (var name in ambiguousNames)
        {
            warnings.Add(
                $"The address list gives seller '{name}' more than one different address. " +
                "Nothing is sent to that seller until one of the rows is corrected.");
        }

        foreach (var id in ambiguousIds)
        {
            warnings.Add(
                $"The address list gives seller id '{id}' more than one different address. " +
                "Nothing is sent to that seller until one of the rows is corrected.");
        }

        return new SellerMailDirectory(byId, byName, ambiguousIds, ambiguousNames, rows, warnings);
    }

    /// <summary>
    /// Looks one seller up. The id wins over the name — ids are stable, a storefront name changes
    /// whenever a seller edits it — and anything ambiguous returns a problem rather than an address.
    /// </summary>
    public DirectoryMatch Find(string sellerId, string sellerName)
    {
        var id = SellerGroupMap.NormalizeSellerId(sellerId ?? "");
        var name = SellerGroupMap.FoldName(sellerName ?? "");

        if (id.Length > 0)
        {
            if (_ambiguousIds.Contains(id))
                return new DirectoryMatch(null, $"The address list gives seller id '{id}' two different addresses.");

            if (_byId.TryGetValue(id, out var byId))
                return new DirectoryMatch(byId, null);
        }

        if (name.Length > 0)
        {
            if (_ambiguousNames.Contains(name))
                return new DirectoryMatch(null, $"The address list gives '{sellerName}' two different addresses.");

            if (_byName.TryGetValue(name, out var byName))
                return new DirectoryMatch(byName, null);
        }

        return new DirectoryMatch(null, "This seller is not in the uploaded address list.");
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// Records one key → address. A key repeated with the <em>same</em> address is a duplicate row and
    /// is fine; repeated with a different one, the key is poisoned — neither address wins, because
    /// picking one would be picking whose inbox a price list lands in.
    /// </summary>
    static void Add(Dictionary<string, string> index, HashSet<string> ambiguous, string key, string email)
    {
        if (!index.TryGetValue(key, out var existing))
        {
            index[key] = email;
            return;
        }

        if (!string.Equals(SellerMailStore.NormalizeEmail(existing), SellerMailStore.NormalizeEmail(email), StringComparison.Ordinal))
            ambiguous.Add(key);
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
