namespace YeniRPA.Web.Models;

/// <summary>One status line as it was read off a seller's Catalog Manager page: "Online (1.204)".</summary>
public sealed record ProductStatusRow(string SellerName, string StatusLabel, int Count);

/// <summary>One seller's row of the pivot. <paramref name="Counts"/> lines up with
/// <see cref="ProductStatusResult.Labels"/> by position.</summary>
public sealed record ProductStatusPivotRow(string SellerName, IReadOnlyList<int> Counts);

/// <summary>
/// What a finished Product Status run produced: the seller × status pivot, plus the sellers that could
/// not be read at all.
///
/// <para>A scrape yields one row per (seller, status) pair, but the question being asked is "how does
/// each seller's catalogue break down", so the answer is a table — and it has to be one table, with the
/// same columns for every seller, even though a seller with no drafts simply has no draft line to
/// report. That widening is what <see cref="FromRows"/> does, and it is deliberately kept out of the
/// scraper so it can be tested without a browser.</para>
/// </summary>
public sealed record ProductStatusResult(
    DateTimeOffset CompletedUtc,
    IReadOnlyList<string> Labels,
    IReadOnlyList<ProductStatusPivotRow> Rows,
    IReadOnlyList<string> Failed)
{
    /// <summary>
    /// Pivots the scraped rows into one row per seller.
    ///
    /// <para><paramref name="sellerNames"/> is the list the operator submitted, and it — not the scrape
    /// — decides the row order, so the table reads back in the order it was asked for. A seller that
    /// returned nothing (no products, or a failure) is left out rather than shown as a row of zeros:
    /// zero online offers and "we could not read this seller" are different answers, and the failure
    /// list is where the second one is reported.</para>
    ///
    /// <para>Columns follow the order the labels were first encountered, which is the order Mirakl's own
    /// dropdown lists them in.</para>
    /// </summary>
    public static ProductStatusResult FromRows(
        IReadOnlyList<string> sellerNames,
        IReadOnlyList<ProductStatusRow> rows,
        IReadOnlyList<string> failed)
    {
        ArgumentNullException.ThrowIfNull(sellerNames);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(failed);

        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (seen.Add(row.StatusLabel))
                labels.Add(row.StatusLabel);
        }

        // Last one wins within a seller: the dropdown lists each status once, so a repeat would be a
        // parsing artefact rather than two real figures to reconcile.
        var bySeller = rows
            .GroupBy(r => r.SellerName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(r => r.StatusLabel, StringComparer.Ordinal)
                      .ToDictionary(l => l.Key, l => l.Last().Count, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var pivot = new List<ProductStatusPivotRow>();
        foreach (var seller in sellerNames)
        {
            if (!bySeller.TryGetValue(seller, out var counts))
                continue;

            pivot.Add(new ProductStatusPivotRow(
                seller,
                [.. labels.Select(label => counts.GetValueOrDefault(label, 0))]));
        }

        return new ProductStatusResult(DateTimeOffset.Now, labels, pivot, failed);
    }
}
