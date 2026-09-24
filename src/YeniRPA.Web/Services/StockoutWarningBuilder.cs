using System.Globalization;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Finds the stockout products worth chasing in a Partner Manager "Products Unavailable" export,
/// filters them by GMV, groups them by seller, and attaches each seller's WhatsApp group.
///
/// <para>Every row in the export is already a stockout product (the file is scoped to that by its
/// own name), so unlike <see cref="LateOrderBuilder"/> there is no eligibility rule beyond the GMV
/// floor the operator sets — the question this builder answers is only "which of these are worth
/// messaging about".</para>
///
/// <para>The export carries no seller id column, only a free-text name, so sellers are resolved by
/// name alone through <see cref="SellerGroupMap"/> — the same situation
/// <c>IncidentWarningBuilder</c> already handles, and the same risk: two spellings of one seller's
/// name, or two different sellers sharing a folded name, must never be guessed at. See
/// <see cref="SellerGroupMap.FoldName"/> and <see cref="SellerGroupMap.Resolve"/>.</para>
/// </summary>
public static class StockoutWarningBuilder
{
    public static readonly string[] RequiredColumns =
    [
        "gtin", "Seller Name", "GMV with shipping accepted", "Sold items accepted",
    ];

    public static readonly string[] OptionalColumns =
    [
        "Product id", "Product Name", "Brand", "Focus Category Name", "Offer condition",
    ];

    /// <summary>Most eligible products one message lists before it is truncated.</summary>
    public const int MaxProductLinesPerMessage = 60;

    public const double DefaultGmvThreshold = 50000;

    const string DisplayFormat = "yyyy-MM-dd HH:mm";

    /// <summary>One export row that survived every filter.</summary>
    sealed record EligibleRow(string SellerName, StockoutProductLine Product);

    /// <summary>The file name is load-bearing — <see cref="TabularFile.Read"/> picks the XLSX or the
    /// CSV reader from the extension.</summary>
    public static StockoutWarningData Build(Stream stream, string fileName, StockoutWarningOptions options, SellerGroupMap map)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(map);

        var table = TabularFile.Read(stream, fileName);
        if (table.Count == 0)
            throw new InvalidOperationException("No rows were found in the uploaded file.");

        var header = TabularFile.BuildHeaderIndex(table[0]);

        int Col(string name)
        {
            if (header.TryGetValue(name, out var index))
                return index;
            throw new InvalidOperationException($"Required column '{name}' was not found in the uploaded file.");
        }

        int? Opt(string name) => header.TryGetValue(name, out var index) ? index : null;

        // Fail on the first missing required column before doing any work, so the operator gets one
        // actionable message instead of whichever lookup happened to run first.
        foreach (var required in RequiredColumns)
            Col(required);

        var cGtin = Col("gtin");
        var cSeller = Col("Seller Name");
        var cGmv = Col("GMV with shipping accepted");
        var cSold = Col("Sold items accepted");

        var cProductId = Opt("Product id");
        var cProductName = Opt("Product Name");
        var cBrand = Opt("Brand");
        var cCategory = Opt("Focus Category Name");
        var cOfferCondition = Opt("Offer condition");

        var referenceTime = DateTime.Now;

        var rowsInFile = 0;
        var missingGtin = 0;
        var missingSeller = 0;
        var unreadableGmv = 0;
        var belowThreshold = 0;

        var eligible = new List<EligibleRow>();

        foreach (var row in table.Skip(1))
        {
            var gtin = TabularFile.GetCell(row, cGtin).Trim();
            var seller = TabularFile.GetCell(row, cSeller).Trim();
            var gmvRaw = TabularFile.GetCell(row, cGmv).Trim();

            if (gtin.Length == 0 && seller.Length == 0 && gmvRaw.Length == 0)
                continue; // a wholly blank line, not a row worth counting

            rowsInFile++;

            if (gtin.Length == 0)
            {
                missingGtin++;
                continue;
            }

            if (seller.Length == 0)
            {
                missingSeller++;
                continue;
            }

            if (gmvRaw.Length == 0)
            {
                unreadableGmv++;
                continue;
            }

            var gmv = TabularFile.ParseNumber(gmvRaw);
            if (gmv < options.GmvThreshold)
            {
                belowThreshold++;
                continue;
            }

            eligible.Add(new EligibleRow(seller, new StockoutProductLine(
                Gtin: gtin,
                ProductId: NullIfBlank(TabularFile.GetCell(row, cProductId)),
                ProductName: NullIfBlank(TabularFile.GetCell(row, cProductName)),
                Brand: NullIfBlank(TabularFile.GetCell(row, cBrand)),
                Category: NullIfBlank(TabularFile.GetCell(row, cCategory)),
                OfferCondition: NullIfBlank(TabularFile.GetCell(row, cOfferCondition)),
                Gmv: gmv,
                SoldItemsAccepted: TabularFile.GetCell(row, cSold).Trim())));
        }

        var warnings = new List<string>();
        var sellers = GroupSellers(eligible, map, warnings);
        warnings.AddRange(map.LoadWarnings);

        var funnel = new StockoutWarningFunnel(
            RowsInFile: rowsInFile,
            MissingGtin: missingGtin,
            MissingSeller: missingSeller,
            UnreadableGmv: unreadableGmv,
            BelowThreshold: belowThreshold,
            Eligible: eligible.Count,
            Sellers: sellers.Count,
            MappedSellers: sellers.Count(s => s.GroupName is not null),
            UnmappedSellers: sellers.Count(s => s.GroupName is null));

        return new StockoutWarningData(
            GmvThreshold: options.GmvThreshold,
            ReferenceTime: referenceTime.ToString(DisplayFormat, CultureInfo.InvariantCulture),
            Sellers: sellers,
            Funnel: funnel,
            Warnings: warnings);
    }

    // ---------------------------------------------------------------------

    static List<StockoutWarningSeller> GroupSellers(
        List<EligibleRow> eligible, SellerGroupMap map, List<string> warnings)
    {
        var result = new List<StockoutWarningSeller>();

        foreach (var group in eligible.GroupBy(r => SellerGroupMap.FoldName(r.SellerName), StringComparer.Ordinal))
        {
            // One folded name can carry more than one display spelling in a single export. Group on
            // the folded key and display the most frequent spelling — the same choice
            // LateOrderBuilder.GroupSellers makes for the id path.
            var names = group
                .Select(r => r.SellerName)
                .GroupBy(n => n, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            var displayName = names.FirstOrDefault()?.Key ?? "(no seller name)";
            if (names.Count > 1)
            {
                warnings.Add(
                    $"Seller '{displayName}' appears under more than one spelling in this export " +
                    $"({string.Join(", ", names.Select(n => $"'{n.Key}'"))}). They are treated as one seller.");
            }

            var products = group
                .Select(r => r.Product)
                .OrderByDescending(p => p.Gmv)
                .ToList();

            var match = map.Resolve("", displayName);

            result.Add(new StockoutWarningSeller(
                SellerName: displayName,
                GroupName: match.GroupName,
                MappingProblem: match.Problem,
                ProductCount: products.Count,
                TotalGmv: products.Sum(p => p.Gmv),
                Products: products));
        }

        return [.. result
            .OrderByDescending(s => s.TotalGmv)
            .ThenByDescending(s => s.ProductCount)
            .ThenBy(s => s.SellerName, StringComparer.OrdinalIgnoreCase)];
    }

    static string? NullIfBlank(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
