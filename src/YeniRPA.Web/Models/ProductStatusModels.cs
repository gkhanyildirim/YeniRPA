namespace YeniRPA.Web.Models;

/// <summary>One status line as it was read off a seller's Catalog Manager page: "Online (1.204)".</summary>
public sealed record ProductStatusRow(string SellerName, string StatusLabel, int Count);

/// <summary>One seller's row of the pivot. <paramref name="Counts"/> lines up with
/// <see cref="ProductStatusResult.Labels"/> by position.</summary>
public sealed record ProductStatusPivotRow(string SellerName, IReadOnlyList<int> Counts);

/// <summary>
/// What became of the list the operator submitted, line by line.
///
/// <para>These are not statistics, they are an account that has to balance:
/// <c>FileRows - HeaderRows + PastedLines == Blank + Comments + Duplicates + Sellers</c>. Without it
/// a spreadsheet of 765 rows quietly becomes a table of 472 and there is nothing on the page that
/// says which of the five filters below took the other 293 — or whether the scrape did.</para>
/// </summary>
/// <param name="FileRows">Rows read from the uploaded file, header included. Zero when none was sent.</param>
/// <param name="HeaderRows">The header the file path skips — one when a file was sent, else zero.</param>
/// <param name="HeaderText">The first cell of that skipped row. Worth reporting because the skip is
/// unconditional: a file with no header loses a real seller here, and seeing a seller's name in this
/// field is the only way to notice.</param>
/// <param name="PastedLines">Lines that came from the textarea.</param>
/// <param name="Blank">Lines that were empty once trimmed.</param>
/// <param name="Comments">Lines starting with '#'. Spreadsheet error cells land here too — a column
/// of VLOOKUPs gone wrong reaches this as "#N/A" and is dropped as though it were a comment.</param>
/// <param name="Duplicates">Names that repeat one already kept. A per-order export naturally repeats
/// its seller column, so this is usually the largest figure of the five.</param>
/// <param name="Sellers">What is left, and what actually runs.</param>
public sealed record ProductStatusIntake(
    int FileRows,
    int HeaderRows,
    string? HeaderText,
    int PastedLines,
    int Blank,
    int Comments,
    int Duplicates,
    int Sellers)
{
    /// <summary>
    /// Applies the submitted list's five filters, keeping count of what each one took.
    ///
    /// <para>The rules themselves are unchanged from the ones this replaces in
    /// <c>ProductStatusController.Start</c> — trim, drop empty, drop '#', de-duplicate ordinally.
    /// Kept here rather than in the controller so the accounting can be tested against a list of
    /// lines instead of an HTTP request, the same reason <see cref="ProductStatusResult.FromRows"/>
    /// sits outside the scraper.</para>
    /// </summary>
    /// <param name="fileRows">First cells of the uploaded file's rows, header row included.</param>
    /// <param name="pastedText">The textarea's contents, or null.</param>
    public static (IReadOnlyList<string> Sellers, ProductStatusIntake Intake) ParseLines(
        IReadOnlyList<string>? fileRows,
        string? pastedText)
    {
        var hasFile = fileRows is { Count: > 0 };
        var headerRows = hasFile ? 1 : 0;
        var headerText = hasFile ? fileRows![0] : null;

        var lines = new List<string>();
        if (hasFile)
            lines.AddRange(fileRows!.Skip(headerRows));

        var pastedLines = 0;
        if (!string.IsNullOrWhiteSpace(pastedText))
        {
            var pasted = pastedText.Split('\n');
            pastedLines = pasted.Length;
            lines.AddRange(pasted);
        }

        var blank = 0;
        var comments = 0;
        var duplicates = 0;
        var sellers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var name = line.Trim();

            if (name.Length == 0)
                blank++;
            else if (name.StartsWith('#'))
                comments++;
            else if (!seen.Add(name))
                duplicates++;
            else
                sellers.Add(name);
        }

        return (sellers, new ProductStatusIntake(
            hasFile ? fileRows!.Count : 0,
            headerRows,
            headerText,
            pastedLines,
            blank,
            comments,
            duplicates,
            sellers.Count));
    }

    /// <summary>
    /// A neutral account for the paths that never saw the submitted list — the fatal-error result,
    /// and callers that only have the names. Everything is a seller and nothing was dropped, which is
    /// true of a list that has already been through <see cref="ParseLines"/>.
    /// </summary>
    public static ProductStatusIntake ForNames(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        return new ProductStatusIntake(0, 0, null, names.Count, 0, 0, 0, names.Count);
    }
}

/// <summary>
/// What a finished Product Status run produced: the seller × status pivot, plus an account of every
/// seller that is not in it.
///
/// <para>A scrape yields one row per (seller, status) pair, but the question being asked is "how does
/// each seller's catalogue break down", so the answer is a table — and it has to be one table, with the
/// same columns for every seller, even though a seller with no drafts simply has no draft line to
/// report. That widening is what <see cref="FromRows"/> does, and it is deliberately kept out of the
/// scraper so it can be tested without a browser.</para>
///
/// <para>Three of the fields exist so the table's row count can be reconciled with what was asked for.
/// Every submitted seller is in <see cref="Rows"/> or in <see cref="Failed"/> — never both — and
/// <see cref="WithoutProducts"/> names which of the <see cref="Rows"/> are a zero row rather than a
/// real read. <see cref="Intake"/> says how the submitted lines became that set of sellers in the
/// first place.</para>
/// </summary>
/// <param name="WithoutProducts">Sellers that were read successfully and have an empty catalogue —
/// no match in Mirakl's own provider filter is indistinguishable, from here, from a real seller with
/// nothing in it. They still get a row, all zeroes, so the table accounts for every seller that was
/// asked for; this list is what lets the page say why a given row is all zeroes instead of a real
/// count.</param>
public sealed record ProductStatusResult(
    DateTimeOffset CompletedUtc,
    IReadOnlyList<string> Labels,
    IReadOnlyList<ProductStatusPivotRow> Rows,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> WithoutProducts,
    ProductStatusIntake Intake)
{
    /// <summary>
    /// Pivots the scraped rows into one row per seller.
    ///
    /// <para><paramref name="sellerNames"/> is the list the operator submitted, and it — not the scrape
    /// — decides the row order, so the table reads back in the order it was asked for. A seller that
    /// was read but had no products (<paramref name="withoutProducts"/>) still gets a row, all zeroes
    /// — no match in Mirakl and a real seller with zero online offers are indistinguishable to this
    /// module, and either way the table should account for a seller that was asked for. A seller the
    /// scrape could not read at all (<paramref name="failed"/>) is left out instead: that is not a
    /// count, zero or otherwise, and the failure list is where it is reported.</para>
    ///
    /// <para>Columns follow the order the labels were first encountered, which is the order Mirakl's own
    /// dropdown lists them in.</para>
    ///
    /// <para>The last two arguments are optional so that a caller which has nothing to reconcile — a
    /// test, or a run that failed before it read anything — can leave them out and still get a result
    /// whose account balances trivially.</para>
    /// </summary>
    public static ProductStatusResult FromRows(
        IReadOnlyList<string> sellerNames,
        IReadOnlyList<ProductStatusRow> rows,
        IReadOnlyList<string> failed,
        IReadOnlyList<string>? withoutProducts = null,
        ProductStatusIntake? intake = null)
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

        var withoutProductsSet = new HashSet<string>(withoutProducts ?? [], StringComparer.Ordinal);

        var pivot = new List<ProductStatusPivotRow>();
        foreach (var seller in sellerNames)
        {
            if (bySeller.TryGetValue(seller, out var counts))
            {
                pivot.Add(new ProductStatusPivotRow(
                    seller,
                    [.. labels.Select(label => counts.GetValueOrDefault(label, 0))]));
            }
            else if (withoutProductsSet.Contains(seller))
            {
                // No match in Mirakl's provider filter and a real seller with an empty catalogue read
                // the same way here — see the type's doc comment — so both get a zero row rather than
                // vanishing from the table.
                pivot.Add(new ProductStatusPivotRow(seller, [.. labels.Select(_ => 0)]));
            }
            // Neither a scraped row nor a known-empty seller — e.g. a failed read — is left out; see
            // the "Failed" branch of FromRows' doc comment.
        }

        return new ProductStatusResult(
            DateTimeOffset.Now,
            labels,
            pivot,
            failed,
            withoutProducts ?? [],
            intake ?? ProductStatusIntake.ForNames(sellerNames));
    }
}
