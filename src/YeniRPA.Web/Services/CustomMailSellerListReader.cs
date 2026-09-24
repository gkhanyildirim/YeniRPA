namespace YeniRPA.Web.Services;

/// <summary>
/// Reads which sellers Custom Mail should reach out of the uploaded seller list — an id and a name
/// per row, nothing more.
///
/// <para>The address itself is deliberately never read from this file, even when it has its own
/// e-mail column (the "shops" export does, in "E-posta"): the operator wants every address resolved
/// from a separate, authoritative directory upload instead — see <see cref="SellerMailDirectory"/>,
/// which <see cref="Controllers.CustomMailController.Prepare"/> looks each of these sellers up in.</para>
/// </summary>
internal static class CustomMailSellerListReader
{
    public readonly record struct SellerRow(string SellerId, string SellerName);

    /// <summary>
    /// What identifies a seller for Custom Mail's own purposes: the normalized id when there is one,
    /// the folded name otherwise — the same precedence <see cref="SellerGroupMap.Resolve"/> and
    /// <see cref="OfferSplitBuilder.SellerKey"/> apply. A second copy of that composition rather than a
    /// call into <see cref="OfferSplitBuilder"/>, an Offer-specific class Custom Mail otherwise has no
    /// reason to depend on — see <see cref="SellerMailStore"/>'s own doc comment on why sibling mail
    /// modules in this app deliberately don't share this kind of logic.
    ///
    /// <para>Used both by <see cref="Read"/>'s own row-deduplication and by
    /// <see cref="CustomMailStore.FindOverride"/>, so a hand-entered override and an uploaded seller
    /// row are matched by the identical rule.</para>
    /// </summary>
    internal static string SellerKey(string sellerId, string sellerName)
    {
        var id = SellerGroupMap.NormalizeSellerId(sellerId ?? "");
        return id.Length > 0 ? "id:" + id : "name:" + SellerGroupMap.FoldName(sellerName ?? "");
    }

    public static IReadOnlyList<SellerRow> Read(Stream stream, string fileName)
    {
        var table = TabularFile.Read(stream, fileName, null);

        if (table.Count == 0)
            throw new InvalidOperationException($"'{fileName}' is empty.");

        var header = TabularFile.BuildHeaderIndex(table[0]);

        var cId = FindColumn(header, SellerMailDirectory.SellerIdHeaders);
        var cName = FindColumn(header, SellerMailDirectory.SellerNameHeaders);

        if (cId is null && cName is null)
        {
            throw new InvalidOperationException(
                $"'{fileName}' needs a '{SellerMailDirectory.SellerNameHeaders[0]}' or a " +
                $"'{SellerMailDirectory.SellerIdHeaders[0]}' column to identify sellers by.");
        }

        var sellers = new List<SellerRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in table.Skip(1))
        {
            var id = SellerGroupMap.NormalizeSellerId(TabularFile.GetCell(row, cId));
            var name = TabularFile.GetCell(row, cName).Trim();

            if (id.Length == 0 && name.Length == 0)
                continue;

            // The same seller appearing on two rows (a re-export, a status change) is one seller to
            // reach, not two mails.
            if (!seen.Add(SellerKey(id, name)))
                continue;

            sellers.Add(new SellerRow(id, name));
        }

        return sellers;
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
