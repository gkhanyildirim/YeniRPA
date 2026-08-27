using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Splits the "offers with no VAT rate" export into one group per seller.
///
/// <para>Pure and IO-free, like the report builders: it reads a stream into a table and groups it,
/// and never touches the output folder. <see cref="VatSellerWorkbook"/> does the writing. That keeps
/// the rule deciding <em>which offer belongs to which seller</em> testable without a folder full of
/// workbooks, which matters because that rule is the one that, if wrong, hands a seller a
/// competitor's product list.</para>
/// </summary>
public static class VatSplitBuilder
{
    /// <summary>
    /// Column headers accepted for each field. The first of each is what the Mirakl export writes, so
    /// the operator's file is read as it comes out of the back office.
    /// </summary>
    static readonly string[] SellerIdHeaders = ["Seller id", "Seller ID", "SellerId", "Satıcı Id"];
    static readonly string[] SellerNameHeaders = ["Seller", "Seller name", "Satıcı", "Satıcı Adı"];
    static readonly string[] OfferIdHeaders = ["Offer id", "Offer ID", "OfferId", "Teklif No"];
    static readonly string[] TitleHeaders = ["Product Title", "Product title", "Ürün Adı"];
    static readonly string[] GtinHeaders = ["gtin", "GTIN", "EAN", "Barkod"];
    static readonly string[] BrandHeaders = ["Product Brand", "Brand", "Marka"];
    static readonly string[] StateReasonHeaders =
        ["State Reasons", "State Reason", "StateReasons", "Durum Nedeni"];

    /// <summary>The one state reason this module warns about. See <see cref="IsVatRateMissingOnly"/>.</summary>
    public const string VatRateMissing = "VAT_RATE_MISSING";

    /// <summary>The number of digits a GTIN-13 has, and what a shorter one is padded out to. See
    /// <see cref="NormalizeGtin"/>.</summary>
    const int GtinLength = 13;

    /// <summary>
    /// An upper bound on the export, so a wrong file cannot turn into an unbounded allocation and a
    /// folder full of workbooks. The real export runs to ~1 600 rows.
    /// </summary>
    public const int MaxOfferRows = 200_000;

    public sealed record SplitResult(
        IReadOnlyList<VatSellerGroup> Sellers,

        /// <summary>Rows whose only state reason is <see cref="VatRateMissing"/>, before duplicate
        /// products are folded together.</summary>
        int OffersInFile,

        /// <summary>Rows the state-reason filter dropped. Reported so "40 sellers out of a 1 600-row
        /// export" reads as the filter working rather than as most of the file having gone missing.</summary>
        int OffersFilteredOut,

        IReadOnlyList<string> Warnings);

    /// <summary>Reads the export and groups it by seller, in the order the sellers first appear.</summary>
    public static SplitResult Read(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("The offer export is empty.");

        var header = TabularFile.BuildHeaderIndex(table[0]);

        var cId = FindColumn(header, SellerIdHeaders);
        var cName = FindColumn(header, SellerNameHeaders);

        if (cId is null && cName is null)
        {
            throw new InvalidOperationException(
                $"The offer export needs a '{SellerIdHeaders[0]}' or a '{SellerNameHeaders[0]}' column — " +
                "without one there is no way to tell whose offers these are.");
        }

        var cOffer = FindColumn(header, OfferIdHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{OfferIdHeaders[0]}' was not found in the offer export.");

        var cTitle = FindColumn(header, TitleHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{TitleHeaders[0]}' was not found in the offer export. " +
                "A list a seller cannot read the product names off is not worth sending.");

        // Required, unlike the other product columns: the GTIN is how a seller finds the product in
        // their own panel, and a list of titles with no barcodes is what this module used to send
        // when the export renamed this column and nothing noticed.
        var cGtin = FindColumn(header, GtinHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{GtinHeaders[0]}' was not found in the offer export. " +
                "Without it the seller has no way to look the products up.");

        var cBrand = FindColumn(header, BrandHeaders);

        // Required for the obvious reason: it is what this module filters on. An export that renamed
        // this column would otherwise match nothing, and rather than warning nobody it would fall
        // back to warning everybody — every offer in the file mailed to its seller as a VAT problem.
        var cState = FindColumn(header, StateReasonHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{StateReasonHeaders[0]}' was not found in the offer export. " +
                "That is the column this module selects on — check the file.");

        var dataRows = table.Count - 1;
        if (dataRows > MaxOfferRows)
        {
            throw new InvalidOperationException(
                $"The export holds {dataRows:N0} rows, over the {MaxOfferRows:N0}-row limit. " +
                "That is not the export this module expects — check the file.");
        }

        // Insertion-ordered: the panel lists sellers in the order the export introduces them, which is
        // the order the operator scrolled past in Excel.
        var groups = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var order = new List<string>();

        var offersInFile = 0;
        var offersFilteredOut = 0;
        var rowsWithNoSeller = 0;

        foreach (var row in table.Skip(1))
        {
            var id = SellerGroupMap.NormalizeSellerId(TabularFile.GetCell(row, cId));
            var name = TabularFile.GetCell(row, cName).Trim();

            var offerId = TabularFile.GetCell(row, cOffer).Trim();
            var title = TabularFile.GetCell(row, cTitle).Trim();
            var state = TabularFile.GetCell(row, cState);

            // A completely blank line at the end of a sheet is not a row; the used range often runs
            // past the data. Only rows that carry something are counted.
            if (id.Length == 0 && name.Length == 0 && offerId.Length == 0 && title.Length == 0 &&
                state.Trim().Length == 0)
                continue;

            // Before the row is counted as ours: an offer that is also inactive, out of stock or
            // priced at zero has a bigger problem than its VAT rate, and telling its seller to fix
            // the VAT rate is the wrong message. Only offers whose sole complaint is the missing VAT
            // rate are warned about.
            if (!IsVatRateMissingOnly(state))
            {
                offersFilteredOut++;
                continue;
            }

            offersInFile++;

            if (id.Length == 0 && name.Length == 0)
            {
                // Cannot be attributed to anyone, so it cannot be mailed to anyone. Counted and
                // reported rather than dropped in silence.
                rowsWithNoSeller++;
                continue;
            }

            var key = SellerKey(id, name);

            if (!groups.TryGetValue(key, out var builder))
            {
                builder = new Builder(id, name);
                groups[key] = builder;
                order.Add(key);
            }

            builder.Add(new VatOfferRow(
                Gtin: NormalizeGtin(TabularFile.GetCell(row, cGtin)),
                ProductTitle: title,
                Brand: TabularFile.GetCell(row, cBrand).Trim()));

            builder.SeeName(name);
        }

        var sellers = order
            .Select(key => new VatSellerGroup(
                SellerId: groups[key].SellerId,
                SellerName: groups[key].SellerName,
                SellerKey: key,
                Offers: groups[key].Offers))
            .ToList();

        var warnings = new List<string>();

        if (rowsWithNoSeller > 0)
        {
            warnings.Add(
                $"{rowsWithNoSeller:N0} row(s) in the export name no seller and are in nobody's list. " +
                "Nothing was guessed for them.");
        }

        // One seller id written under two different storefront names. Harmless — a seller renamed their
        // shop mid-month — but worth saying, because the name is what appears in the mail.
        foreach (var builder in order.Select(key => groups[key]).Where(b => b.OtherNames.Count > 0))
        {
            warnings.Add(
                $"Seller id '{builder.SellerId}' appears under more than one name " +
                $"('{builder.SellerName}', {string.Join(", ", builder.OtherNames.Select(n => $"'{n}'"))}). " +
                $"'{builder.SellerName}' is the one used in the mail.");
        }

        return new SplitResult(sellers, offersInFile, offersFilteredOut, warnings);
    }

    /// <summary>
    /// Whether a <c>State Reasons</c> cell says the missing VAT rate is this offer's <em>only</em>
    /// problem.
    ///
    /// <para>The export writes this column as a comma-joined list — an offer can arrive as
    /// <c>INACTIVE_IN_MIRAKL,MIRAKL_ZERO_QUANTITY,VAT_RATE_MISSING</c>. A substring test would accept
    /// that row, and its seller would be asked to fix a VAT rate on an offer that is switched off and
    /// out of stock. So the cell must reduce to exactly one reason, and that reason must be
    /// <see cref="VatRateMissing"/>.</para>
    ///
    /// <para>An empty cell is <c>false</c>: a row that states no reason at all is not a row that
    /// states this one.</para>
    /// </summary>
    public static bool IsVatRateMissingOnly(string raw)
    {
        var reasons = (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return reasons.Length == 1 &&
               string.Equals(reasons[0], VatRateMissing, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------
    // File names
    // ---------------------------------------------------------------------

    /// <summary>
    /// The file name one seller's workbook gets.
    ///
    /// <para>The seller id leads because it is what makes the name unique and stable — two sellers can
    /// pick the same storefront name, and a storefront name changes whenever a seller edits it. The
    /// name follows it so the operator can recognise the file in the folder without looking anything
    /// up.</para>
    ///
    /// <para>Everything Windows refuses in a file name becomes <c>-</c>. Turkish letters are kept:
    /// they are legal in a file name, and stripping them turns "Bizbiz-E" and "Bızbız-E" into the same
    /// file, which is precisely the collision this name must not create.</para>
    /// </summary>
    public static string FileNameFor(VatSellerGroup seller)
    {
        ArgumentNullException.ThrowIfNull(seller);

        var id = seller.SellerId.Trim();
        var name = Sanitize(seller.SellerName);

        var stem = (id.Length, name.Length) switch
        {
            (> 0, > 0) => $"{id} - {name}",
            (> 0, _) => id,
            (_, > 0) => name,
            _ => ""
        };

        // Unreachable via Read, which drops rows with neither. Kept so the function is total rather
        // than returning ".xlsx" for a group built some other way.
        if (stem.Length == 0)
            stem = "seller";

        // Windows' hard ceiling is 255; well under it leaves room for the folder path.
        if (stem.Length > 120)
            stem = stem[..120].TrimEnd();

        return stem + ".xlsx";
    }

    /// <summary>
    /// Seller keys whose file names collide, case-insensitively — the folder is Windows, where
    /// <c>Prodesk.xlsx</c> and <c>PRODESK.xlsx</c> are one file.
    ///
    /// <para>Both sides of a collision are returned, not just the second. Mailing either one means the
    /// second write overwrote the first, so one of the two sellers receives the other's complete
    /// product list — the exact leak this module is built to make impossible. Neither is sent.</para>
    /// </summary>
    public static HashSet<string> FindFileNameClashes(IEnumerable<VatSellerGroup> sellers)
    {
        ArgumentNullException.ThrowIfNull(sellers);

        return [.. sellers
            .GroupBy(FileNameFor, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(s => s.SellerKey))];
    }

    // ---------------------------------------------------------------------

    /// <summary>Matches <see cref="SellerGroupMap.Resolve"/>'s precedence: the id when there is one,
    /// the folded name otherwise.</summary>
    public static string SellerKey(string sellerId, string sellerName)
    {
        var id = SellerGroupMap.NormalizeSellerId(sellerId ?? "");
        return id.Length > 0 ? "id:" + id : "name:" + SellerGroupMap.FoldName(sellerName ?? "");
    }

    /// <summary>
    /// A GTIN as it should be read, not as the export writes it.
    ///
    /// <para>The export stores this column as a number, so <c>0858445004684</c> arrives as
    /// <c>858445004684</c> — the leading zero is already gone by the time the file reaches us, and a
    /// 12-digit barcode identifies nothing. Anything shorter than 13 digits is padded back out.</para>
    ///
    /// <para>Nothing is ever truncated: a 14-digit GTIN-14 is a real barcode and is passed through as
    /// it stands. A cell that is not all digits is passed through too — showing a seller exactly what
    /// their export holds is more use than a padded guess. An empty cell stays empty; padding it
    /// would invent <c>0000000000000</c>.</para>
    /// </summary>
    public static string NormalizeGtin(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return "";

        // A numeric cell read back through a general format can carry a decimal tail: "8683052680295.0"
        // is the same barcode. Only an all-zero fraction is dropped — .5 is not a rounding error we
        // may quietly discard.
        var dot = text.IndexOf('.');
        if (dot > 0 && text[(dot + 1)..].All(c => c == '0'))
            text = text[..dot];

        if (!text.All(char.IsAsciiDigit))
            return text;

        return text.Length < GtinLength ? text.PadLeft(GtinLength, '0') : text;
    }

    static string Sanitize(string raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0)
            return "";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var ch in value)
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);

        // A trailing dot or space is legal to write and then impossible to open on Windows.
        return builder.ToString().Trim(' ', '.');
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

    /// <summary>Accumulates one seller while the export is being walked.</summary>
    sealed class Builder(string sellerId, string sellerName)
    {
        public string SellerId { get; } = sellerId;

        /// <summary>The first non-empty name seen for this seller; that is the one the mail uses.</summary>
        public string SellerName { get; private set; } = sellerName;

        /// <summary>Other spellings the export used for the same seller, for the warning.</summary>
        public List<string> OtherNames { get; } = [];

        public List<VatOfferRow> Offers { get; } = [];

        /// <summary>What is already in <see cref="Offers"/>, so the same product is not listed twice.</summary>
        readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Adds a product unless this seller already has it. A seller can hold two offers on one
        /// product; the attachment no longer carries the offer number that would tell those two lines
        /// apart, so a second identical line would read as a mistake in our file rather than as two
        /// offers. The first row seen wins.
        ///
        /// <para>Rows with no GTIN fall back to title and brand — folding every barcode-less product
        /// onto one key would delete real products from the seller's list.</para>
        /// </summary>
        public void Add(VatOfferRow offer)
        {
            var key = offer.Gtin.Length > 0
                ? "gtin:" + offer.Gtin
                : $"name:{offer.ProductTitle}{offer.Brand}";

            if (_seen.Add(key))
                Offers.Add(offer);
        }

        public void SeeName(string name)
        {
            var value = (name ?? "").Trim();
            if (value.Length == 0)
                return;

            if (SellerName.Length == 0)
            {
                SellerName = value;
                return;
            }

            if (!string.Equals(SellerName, value, StringComparison.Ordinal) &&
                !OtherNames.Contains(value, StringComparer.Ordinal))
            {
                OtherNames.Add(value);
            }
        }
    }
}
