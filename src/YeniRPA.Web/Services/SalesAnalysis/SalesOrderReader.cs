using System.Globalization;

namespace YeniRPA.Web.Services.SalesAnalysis;

/// <summary>One kind of problem found while importing, with the first few spreadsheet rows it hit.</summary>
public sealed record ImportIssue(string Kind, string Message, int Count, IReadOnlyList<int> SampleRows);

/// <summary>What the import read, what it set aside, and why.</summary>
public sealed record ImportReport(
    int RowsRead,
    int RowsUsed,
    IReadOnlyList<ImportIssue> Issues,
    IReadOnlyList<string> MissingOptionalColumns,
    IReadOnlyList<string> Currencies);

public sealed record SalesDataset(IReadOnlyList<SalesLine> Lines, ImportReport Report);

/// <summary>
/// Reads the Mirakl orders export into <see cref="SalesLine"/>s for the Sales Analysis.
///
/// <para>Rows are streamed through <see cref="OfferExportReader"/> (OpenXmlReader, one row at a time)
/// rather than ClosedXML's DOM, per CLAUDE.md: production exports are expected to be far larger than
/// the ~3.5K-line weekly sample. Header lookup goes through <see cref="TabularFile.BuildHeaderIndex"/>
/// and only the columns named in <see cref="SalesColumnMap"/> are ever read.</para>
///
/// <para>Validation never throws for a bad row. A row whose date cannot be parsed is left out of the
/// analysis (it cannot be placed in a period); every other finding is reported and the row is kept.</para>
/// </summary>
public static class SalesOrderReader
{
    const int MaxSampleRows = 5;

    static readonly string[] DateFormats =
    [
        "MM/dd/yyyy hh:mm:ss tt", "MM/dd/yyyy h:mm:ss tt", "M/d/yyyy h:mm:ss tt", "MM/dd/yyyy HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd",
    ];

    public static SalesDataset Read(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var rows = OfferExportReader.Read(stream, fileName).GetEnumerator();
        if (!rows.MoveNext())
            throw new InvalidOperationException("The uploaded file is empty.");

        var header = TabularFile.BuildHeaderIndex(rows.Current);

        var missingRequired = SalesColumnMap.Required.Where(c => !header.ContainsKey(c)).ToList();
        if (missingRequired.Count > 0)
        {
            throw new InvalidOperationException(
                "Satış analizi için gerekli kolon(lar) dosyada bulunamadı: " +
                string.Join(", ", missingRequired.Select(c => $"'{c}'")) +
                ". Mirakl 'Orders' export'unu kolonları değiştirmeden yükleyin.");
        }

        int? Col(string name) => header.TryGetValue(name, out var idx) ? idx : null;

        var cDate = Col(SalesColumnMap.DateCreated);
        var cOrder = Col(SalesColumnMap.OrderNumber);
        var cLineNo = Col(SalesColumnMap.OrderLineNo);
        var cStatus = Col(SalesColumnMap.Status);
        var cQty = Col(SalesColumnMap.Quantity);
        var cUnit = Col(SalesColumnMap.UnitPrice);
        var cAmount = Col(SalesColumnMap.Amount);
        var cCurrency = Col(SalesColumnMap.Currency);
        var cShipping = Col(SalesColumnMap.ShippingPrice);
        var cTotalExcl = Col(SalesColumnMap.OrderTotalExclTaxes);
        var cTotalIncl = Col(SalesColumnMap.OrderTotalInclVat);
        var cCategory = Col(SalesColumnMap.CategoryLabel);
        var cCategoryCode = Col(SalesColumnMap.CategoryCode);
        var cBrand = Col(SalesColumnMap.Brand);
        var cSeller = Col(SalesColumnMap.Seller);
        var cSku = Col(SalesColumnMap.ProductSku);
        var cSellerSku = Col(SalesColumnMap.SellerSku);
        var cTitle = Col(SalesColumnMap.ProductTitle);
        var cOfferState = Col(SalesColumnMap.OfferState);
        var cCommission = Col(SalesColumnMap.Commission);
        var cTransferred = Col(SalesColumnMap.TransferredToSeller);
        var cWithCancel = Col(SalesColumnMap.LineWithCancelations);
        var cCanceled = Col(SalesColumnMap.CanceledAmount);
        var cCancelReq = Col(SalesColumnMap.CancellationRequestStatus);
        var cCity = Col(SalesColumnMap.City);
        var cCarrier = Col(SalesColumnMap.ShippingCompany);
        var cLeadTime = Col(SalesColumnMap.LeadTimeToShip);
        var cCustomer = Col(SalesColumnMap.CustomerId);

        var issues = new Dictionary<string, (string Message, int Count, List<int> Rows)>();
        void Flag(string kind, string message, int rowNumber)
        {
            if (!issues.TryGetValue(kind, out var entry))
                entry = (message, 0, []);
            if (entry.Rows.Count < MaxSampleRows)
                entry.Rows.Add(rowNumber);
            issues[kind] = (entry.Message, entry.Count + 1, entry.Rows);
        }

        var lines = new List<SalesLine>();
        var seenLineNumbers = new HashSet<string>(StringComparer.Ordinal);
        var currencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowsRead = 0;
        var rowNumber = 1;

        while (rows.MoveNext())
        {
            rowNumber++;
            var row = rows.Current;
            string Text(int? col) => TabularFile.GetCell(row, col).Trim();
            double Number(int? col) => TabularFile.ParseNumber(TabularFile.GetCell(row, col));

            var orderNumber = Text(cOrder);
            if (orderNumber.Length == 0)
                continue;
            rowsRead++;

            var status = Text(cStatus);
            var quantity = Number(cQty);
            var amount = Number(cAmount);
            var lineNo = Text(cLineNo);
            var currency = Text(cCurrency);
            if (currency.Length > 0)
                currencies.Add(currency);

            var created = ParseDate(Text(cDate));
            if (created is null)
            {
                Flag("unparsedDate", "'Date created' okunamadı; satır analizden çıkarıldı.", rowNumber);
                continue;
            }

            if (amount < 0 || quantity < 0)
                Flag("negative", "Negatif tutar veya adet.", rowNumber);

            var canceled = string.Equals(status, SalesStatuses.Canceled, StringComparison.OrdinalIgnoreCase);
            if (quantity == 0 && !canceled)
                Flag("zeroQuantity", "Adet 0 ama satır iptal değil.", rowNumber);

            if (lineNo.Length > 0 && !seenLineNumbers.Add(lineNo))
                Flag("duplicateLine", "Aynı 'Order line no.' birden fazla kez geçiyor.", rowNumber);

            lines.Add(new SalesLine
            {
                OrderNumber = orderNumber,
                OrderLineNo = lineNo,
                Created = DateTime.SpecifyKind(created.Value, DateTimeKind.Unspecified),
                Status = status,
                Quantity = quantity,
                Amount = amount,
                UnitPrice = Number(cUnit),
                Currency = currency,
                ShippingPrice = Number(cShipping),
                OrderTotalExclTaxes = Number(cTotalExcl),
                OrderTotalInclVat = Number(cTotalIncl),
                CategoryLabel = Text(cCategory),
                CategoryCode = Text(cCategoryCode),
                Brand = Text(cBrand),
                Seller = Text(cSeller),
                ProductSku = NormalizeSku(Text(cSku)),
                SellerSku = Text(cSellerSku),
                Title = Text(cTitle),
                OfferState = Text(cOfferState),
                Commission = Number(cCommission),
                TransferredToSeller = Number(cTransferred),
                LineWithCancelations = string.Equals(Text(cWithCancel), "yes", StringComparison.OrdinalIgnoreCase),
                CanceledAmount = Number(cCanceled),
                CancellationRequestStatus = Text(cCancelReq).ToUpperInvariant(),
                City = Text(cCity),
                ShippingCompany = Text(cCarrier),
                LeadTimeToShip = Number(cLeadTime),
                CustomerId = Text(cCustomer),
            });
        }

        if (rowsRead == 0)
            throw new InvalidOperationException("Dosyada sipariş satırı bulunamadı.");

        if (currencies.Count > 1)
        {
            issues["currency"] = ("Birden fazla para birimi var (" + string.Join(", ", currencies) +
                "); tutarlar dönüştürülmeden toplanır.", currencies.Count, []);
        }

        var report = new ImportReport(
            rowsRead,
            lines.Count,
            [.. issues.Select(kv => new ImportIssue(kv.Key, kv.Value.Message, kv.Value.Count, kv.Value.Rows))],
            [.. SalesColumnMap.Optional.Where(c => !header.ContainsKey(c))],
            [.. currencies.Order(StringComparer.OrdinalIgnoreCase)]);

        return new SalesDataset(lines, report);
    }

    /// <summary>A numeric SKU stored as a number arrives as "184268797.0"; any other SKU is kept as typed.</summary>
    internal static string NormalizeSku(string raw) =>
        raw.Length > 2 && raw.EndsWith(".0", StringComparison.Ordinal) && raw[..^2].All(char.IsAsciiDigit) ? raw[..^2] : raw;

    /// <summary>
    /// The export writes "09/28/2026 12:07:33 AM" — month first, invariant culture. Parsed with exact
    /// formats so a day-first reading can never silently swap day and month; a numeric cell (a date
    /// Excel stored as a serial number) is read as an OLE automation date.
    /// </summary>
    internal static DateTime? ParseDate(string text)
    {
        if (text.Length == 0)
            return null;

        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) &&
            serial is > 1 and < 2958466)
        {
            return DateTime.FromOADate(serial);
        }

        return null;
    }
}
