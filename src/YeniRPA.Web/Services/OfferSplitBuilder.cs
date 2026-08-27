using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Splits the Mirakl offer export into one group per seller, keeping only the offers whose lead time
/// to ship is short enough to be worth warning about.
///
/// <para>Pure and IO-free, like <see cref="VatSplitBuilder"/>: it reads a stream and groups it, and
/// never touches the output folder. <see cref="OfferSellerWorkbook"/> does the writing. That keeps the
/// rule deciding <em>which offer belongs to which seller</em> testable without a folder full of
/// workbooks, which matters because that rule is the one that, if wrong, hands a seller a competitor's
/// offer list.</para>
///
/// <para>Deliberately a near-copy of <see cref="VatSplitBuilder"/> rather than a shared generic: the
/// two read different columns, filter on different rules and produce different attachments, and the
/// reason <see cref="VatMailStore"/> gives for not sharing a base class applies with more force here —
/// a change made for one module's export must not silently alter whose offers land in whose inbox in
/// the other.</para>
/// </summary>
public static class OfferSplitBuilder
{
    /// <summary>
    /// Column headers accepted for each field. The first of each is exactly what the Mirakl export
    /// writes, so the operator's file is read as it comes out of the back office.
    /// </summary>
    static readonly string[] SellerIdHeaders = ["Seller ID", "Seller id", "SellerId", "Satıcı Id"];
    static readonly string[] SellerNameHeaders = ["Seller", "Seller name", "Satıcı", "Satıcı Adı"];
    static readonly string[] ProductSkuHeaders = ["Product SKU", "Product Sku", "ProductSku", "Ürün SKU"];
    static readonly string[] LeadTimeHeaders =
        ["Lead time to ship", "Lead Time To Ship", "LeadTimeToShip", "Termin"];

    /// <summary>
    /// The lead times this module warns about.
    ///
    /// <para>One and two days are the promises a seller most often cannot keep: an offer that claims
    /// next-day dispatch and then ships on the fourth day is a late order, a customer complaint and a
    /// hit to the seller's own rating. Zero is excluded on purpose — it is what the export writes for
    /// offers that are not shipped by the seller at all, and warning about it would send noise.</para>
    /// </summary>
    public static readonly int[] WarnedLeadTimes = [1, 2];

    /// <summary>
    /// An upper bound on the export, so a wrong file cannot turn into an unbounded allocation and a
    /// folder full of workbooks. The real export runs to ~203 000 rows, which is why this is far above
    /// <see cref="VatSplitBuilder.MaxOfferRows"/> — the two modules read different files.
    /// </summary>
    public const int MaxOfferRows = 500_000;

    public sealed record SplitResult(
        IReadOnlyList<OfferSellerGroup> Sellers,

        /// <summary>Rows carrying a warned lead time, before duplicates are folded together.</summary>
        int OffersInFile,

        /// <summary>Rows the lead-time filter dropped. Reported so "287 sellers out of a 203 000-row
        /// export" reads as the filter working rather than as most of the file having gone missing.</summary>
        int OffersFilteredOut,

        IReadOnlyList<string> Warnings);

    /// <summary>Reads the export and groups it by seller, in the order the sellers first appear.</summary>
    public static SplitResult Read(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Streamed, not materialised: this export is two orders of magnitude larger than anything else
        // the app reads. See OfferExportReader for what loading it whole would cost.
        using var rows = OfferExportReader.Read(stream, fileName).GetEnumerator();

        if (!rows.MoveNext())
            throw new InvalidOperationException("The offer export is empty.");

        var header = TabularFile.BuildHeaderIndex(rows.Current);

        var cId = FindColumn(header, SellerIdHeaders);
        var cName = FindColumn(header, SellerNameHeaders);

        if (cId is null && cName is null)
        {
            throw new InvalidOperationException(
                $"The offer export needs a '{SellerIdHeaders[0]}' or a '{SellerNameHeaders[0]}' column — " +
                "without one there is no way to tell whose offers these are.");
        }

        // Required: it is the only thing in the attachment a seller can look the offer up by. A list of
        // bare lead times identifies nothing and is not worth sending.
        var cSku = FindColumn(header, ProductSkuHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{ProductSkuHeaders[0]}' was not found in the offer export. " +
                "Without it the seller has no way to find the offers being complained about.");

        // Required for the obvious reason: it is what this module filters on. An export that renamed
        // this column would otherwise match nothing and report every seller as having no short lead
        // times at all — a clean-looking result that is entirely wrong.
        var cLead = FindColumn(header, LeadTimeHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{LeadTimeHeaders[0]}' was not found in the offer export. " +
                "That is the column this module selects on — check the file.");

        // Insertion-ordered: the panel lists sellers in the order the export introduces them, which is
        // the order the operator scrolled past in Excel.
        var groups = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var order = new List<string>();

        var rowsRead = 0;
        var offersInFile = 0;
        var offersFilteredOut = 0;
        var rowsWithNoSeller = 0;

        while (rows.MoveNext())
        {
            var row = rows.Current;

            if (++rowsRead > MaxOfferRows)
            {
                throw new InvalidOperationException(
                    $"The export holds more than {MaxOfferRows:N0} rows. " +
                    "That is not the export this module expects — check the file.");
            }

            var id = SellerGroupMap.NormalizeSellerId(TabularFile.GetCell(row, cId));
            var name = TabularFile.GetCell(row, cName).Trim();
            var sku = TabularFile.GetCell(row, cSku).Trim();
            var leadCell = TabularFile.GetCell(row, cLead).Trim();

            // A completely blank line at the end of a sheet is not a row; the used range often runs
            // past the data. Only rows that carry something are counted at all.
            if (id.Length == 0 && name.Length == 0 && sku.Length == 0 && leadCell.Length == 0)
                continue;

            var lead = ReadLeadTime(leadCell);
            if (lead is null || !WarnedLeadTimes.Contains(lead.Value))
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

            builder.Add(new OfferLeadRow(sku, lead.Value));
            builder.SeeName(name);
        }

        var sellers = order
            .Select(key => new OfferSellerGroup(
                SellerId: groups[key].SellerId,
                SellerName: groups[key].SellerName,
                SellerKey: key,
                Offers: groups[key].Offers,
                LeadTime1: groups[key].Offers.Count(o => o.LeadTime == 1),
                LeadTime2: groups[key].Offers.Count(o => o.LeadTime == 2)))
            .ToList();

        var warnings = new List<string>();

        if (rowsWithNoSeller > 0)
        {
            warnings.Add(
                $"{rowsWithNoSeller:N0} row(s) with a short lead time name no seller and are in nobody's " +
                "list. Nothing was guessed for them.");
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
    /// A lead-time cell as a whole number of days, or <c>null</c> when it is not one.
    ///
    /// <para>Blank is <c>null</c> rather than zero: a third of the real export leaves this column empty,
    /// and reading those as "ships same day" would be inventing a promise the seller never made. A
    /// fractional value is <c>null</c> for the same reason — the column is days, and half a day is not a
    /// value this export writes.</para>
    /// </summary>
    public static int? ReadLeadTime(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return null;

        var value = TabularFile.ParseNumber(text);

        // ParseNumber answers 0 both for "0" and for "not a number at all". Neither is a warned lead
        // time, so the two do not have to be told apart — but a cell of "abc" must not become 0 and
        // then look like a real reading to a later caller.
        if (value == 0 && text != "0" && text != "0.0")
            return null;

        var rounded = (int)Math.Round(value);
        return Math.Abs(value - rounded) < 0.0001 ? rounded : null;
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
    public static string FileNameFor(OfferSellerGroup seller)
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
    /// second write overwrote the first, so one of the two sellers receives the other's complete offer
    /// list — the exact leak this module is built to make impossible. Neither is sent.</para>
    /// </summary>
    public static HashSet<string> FindFileNameClashes(IEnumerable<OfferSellerGroup> sellers)
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

        public List<OfferLeadRow> Offers { get; } = [];

        /// <summary>What is already in <see cref="Offers"/>, so the same line is not listed twice.</summary>
        readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Adds an offer unless this seller already has that exact line.
        ///
        /// <para>The key is the SKU <em>and</em> the lead time, not the SKU alone: a seller can hold two
        /// offers on one product shipping in one day and in two, and both are things they are being
        /// asked to look at. Folding them would hide half the problem.</para>
        ///
        /// <para>A row with no SKU is never folded — every one is kept. Collapsing them onto a single
        /// blank key would delete real offers from the seller's list.</para>
        /// </summary>
        public void Add(OfferLeadRow offer)
        {
            if (offer.ProductSku.Length == 0 || _seen.Add($"{offer.ProductSku}|{offer.LeadTime}"))
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
