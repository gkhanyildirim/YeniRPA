using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Reads an uploaded Mirakl orders export and keeps only the lines whose shipping company is one of
/// the three carriers this module tracks on 17track.net.
///
/// <para>Carrier matching goes through <see cref="CarrierNames.Resolve"/> rather than a fresh string
/// comparison — the same "Shipping company" column that feeds the Carrier Performance report also
/// feeds this filter, and it carries the same casing/diacritic mess ("Surat kargo", "SÃ¼rat Kargo",
/// "SÃœRAT") that module already canonicalises. Writing a second normaliser here would be a second
/// place for the same carrier to end up spelled two ways.</para>
///
/// <para>No Playwright, no I/O beyond the table it is handed — the whole point of keeping this
/// separate from <c>Track17Runner</c> is that the matching logic can be proven against the real
/// export (<c>Track17FilterRealFileTests</c>) without a browser anywhere near it.</para>
/// </summary>
public static class Track17Filter
{
    /// <summary>17track.net's own public-tool limit: at most this many tracking numbers per search.</summary>
    public const int MaxPerBatch = 40;

    /// <summary>A refusal, not a truncation — same reasoning as <c>ProductStatusRunner.MaxSellersPerRun</c>.
    /// 40 numbers per 17track batch, so this is 40 batches for one run.</summary>
    public const int MaxTrackingNumbersPerRun = 1600;

    /// <summary>The three carriers this module tracks, by their <see cref="CarrierNames"/> canonical
    /// name. Everything else <see cref="CarrierNames.Resolve"/> recognises — Yurtiçi, Aras, DHL, ...
    /// — is not shipped through 17track by this operator and is left out.</summary>
    public static readonly IReadOnlyList<string> TargetCarriers = ["Kolay Gelsin", "Sürat Kargo", "PTT Kargo"];

    static readonly string[] OrderNumberHeaders = ["Order number"];
    static readonly string[] TrackingNumberHeaders = ["Tracking number"];
    static readonly string[] ShippingCompanyHeaders = ["Shipping company"];
    static readonly string[] TrackingUrlHeaders = ["Tracking URL"];

    /// <summary>
    /// Filters <paramref name="table"/> (as read by <see cref="TabularFile.Read"/>, header row first)
    /// down to the rows whose carrier is one of <see cref="TargetCarriers"/> and whose tracking number
    /// is usable.
    /// </summary>
    public static (IReadOnlyList<Track17Row> Rows, int SkippedMalformed) Filter(List<List<string>> table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (table.Count == 0)
            throw new InvalidOperationException("The uploaded file is empty.");

        var header = TabularFile.BuildHeaderIndex(table[0]);

        var cOrder = FindColumn(header, OrderNumberHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{OrderNumberHeaders[0]}' was not found in the uploaded file.");

        var cTracking = FindColumn(header, TrackingNumberHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{TrackingNumberHeaders[0]}' was not found in the uploaded file.");

        var cShipping = FindColumn(header, ShippingCompanyHeaders)
            ?? throw new InvalidOperationException(
                $"Required column '{ShippingCompanyHeaders[0]}' was not found in the uploaded file.");

        var cTrackingUrl = FindColumn(header, TrackingUrlHeaders);

        var targets = new HashSet<string>(TargetCarriers, StringComparer.Ordinal);
        var rows = new List<Track17Row>();
        var skipped = 0;

        foreach (var row in table.Skip(1))
        {
            var shippingRaw = TabularFile.GetCell(row, cShipping).Trim();
            var trackingUrl = TabularFile.GetCell(row, cTrackingUrl);
            var carrier = CarrierNames.Resolve(shippingRaw, trackingUrl);

            if (carrier is null || !targets.Contains(carrier))
                continue;

            var (state, code) = TabularFile.ReadTracking(TabularFile.GetCell(row, cTracking));
            if (state != TabularFile.TrackingState.Ok)
            {
                skipped++;
                continue;
            }

            rows.Add(new Track17Row(
                TabularFile.GetCell(row, cOrder).Trim(),
                code,
                shippingRaw,
                carrier));
        }

        return (rows, skipped);
    }

    /// <summary>Builds the summary <c>prepare</c> hands back to the operator: per-carrier counts and
    /// how many 17track batches of ≤40 the distinct tracking numbers will take.</summary>
    public static Track17PrepareResult Summarize(
        string batchId, int totalRows, IReadOnlyList<Track17Row> rows, int skippedMalformed)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var byCarrier = rows
            .GroupBy(r => r.Carrier, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var distinct = rows.Select(r => r.TrackingNumber).Distinct(StringComparer.Ordinal).Count();
        var batchCount = distinct == 0 ? 0 : (distinct + MaxPerBatch - 1) / MaxPerBatch;

        return new Track17PrepareResult(batchId, totalRows, rows, byCarrier, distinct, batchCount, skippedMalformed);
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
