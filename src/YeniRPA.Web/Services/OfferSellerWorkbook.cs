using ClosedXML.Excel;
using YeniRPA.Web.Models;
using static YeniRPA.Web.Services.XlsxStyles;

namespace YeniRPA.Web.Services;

/// <summary>
/// Writes one seller's own list of offers with a short lead time to ship.
///
/// <para>Split from <see cref="OfferSplitBuilder"/> the way <see cref="VatSellerWorkbook"/> is split
/// from <see cref="VatSplitBuilder"/>: the builder decides <em>what belongs to whom</em> and is tested
/// on that alone, this only renders a group that has already been decided.</para>
///
/// <para>Two columns: the product SKU and the lead time. The SKU is the key the seller looks the offer
/// up by in their own panel, and the lead time is the value they are being asked to correct. Price,
/// stock, category, discount and offer state are ours, not theirs, and never reach
/// <see cref="OfferLeadRow"/> at all — a column that is not in the record cannot be written into a file
/// that leaves the building.</para>
///
/// <para><c>Product SKU</c> keeps its English name because that is the literal column heading in the
/// Mirakl seller panel the recipient will open to fix this; a translated heading would be one more
/// thing for them to map. The second column is Turkish like the mail that carries it.</para>
/// </summary>
public static class OfferSellerWorkbook
{
    const int TitleRow = 1;
    const int SubtitleRow = 2;
    const int HeaderRow = 4;

    static readonly string[] Headers = ["Product SKU", "Termin (Gün)"];

    /// <summary>Columns that must be written as text. The SKU is the reason: it is alphanumeric in the
    /// real export but an all-digit one would lose its leading zeros the moment Excel read it as a
    /// number, at which point it no longer identifies the offer it names.</summary>
    static readonly int[] TextColumns = [1];

    public static byte[] Build(OfferSellerGroup seller, string date)
    {
        ArgumentNullException.ThrowIfNull(seller);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Termin Süreleri");
        ApplyBaseFont(sheet);
        sheet.ShowGridLines = false;

        WriteHeading(sheet, seller, date);

        for (var c = 0; c < Headers.Length; c++)
        {
            var cell = sheet.Cell(HeaderRow, c + 1);
            cell.SetValue(Headers[c]);
            cell.Style.Alignment.WrapText = true;
        }
        StyleHeaderRow(sheet.Range(HeaderRow, 1, HeaderRow, Headers.Length));

        foreach (var column in TextColumns)
            sheet.Column(column).Style.NumberFormat.Format = "@";

        // Sorted by lead time, then by SKU. The export's own order is a scroll position in the
        // operator's spreadsheet and means nothing to the seller; a list where every one-day offer sits
        // together is one they can work down. Ordinal so the ordering does not shift with a locale.
        var offers = seller.Offers
            .OrderBy(o => o.LeadTime)
            .ThenBy(o => o.ProductSku, StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < offers.Count; i++)
        {
            var offer = offers[i];
            var row = HeaderRow + 1 + i;

            // SetValue<string> stores text as text: a cell beginning with "=" is never promoted to a
            // formula, which is the same guard TableWorkbookBuilder.WriteValue relies on.
            sheet.Cell(row, 1).SetValue(offer.ProductSku);
            sheet.Cell(row, 2).SetValue(offer.LeadTime);
        }

        var lastRow = HeaderRow + offers.Count;
        if (offers.Count > 0)
        {
            ApplyThinBorders(sheet.Range(HeaderRow, 1, lastRow, Headers.Length));
            ApplyZebra(sheet, HeaderRow + 1, lastRow, 1, Headers.Length);
            sheet.Range(HeaderRow + 1, 2, lastRow, 2).Style.Alignment.Horizontal =
                XLAlignmentHorizontalValues.Center;
        }

        // Only the table is measured — a merged title spanning every column would otherwise stretch the
        // first column to the width of the whole heading. Same reason as TableWorkbookBuilder.
        sheet.Columns().AdjustToContents(HeaderRow, lastRow);
        foreach (var column in sheet.ColumnsUsed())
            column.Width = Math.Clamp(column.Width, 12, 46);

        sheet.SheetView.FreezeRows(HeaderRow);
        sheet.Cell(HeaderRow, 1).SetActive();

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    /// <summary>
    /// The seller's own name sits at the top of their file. It is the cheapest possible check that the
    /// right list reached the right inbox: a seller who opens a workbook headed with someone else's
    /// name knows immediately, and so does the operator during a dry run.
    /// </summary>
    static void WriteHeading(IXLWorksheet sheet, OfferSellerGroup seller, string date)
    {
        var title = sheet.Cell(TitleRow, 1);
        title.SetValue(seller.SellerName.Length > 0 ? seller.SellerName : seller.SellerId);
        title.Style.Font.Bold = true;
        title.Style.Font.FontSize = 14;
        title.Style.Font.FontColor = NavyColor;

        var subtitle = sheet.Cell(SubtitleRow, 1);
        subtitle.SetValue(
            $"Termini 1-2 gün olan teklifler · {seller.Offers.Count:N0} teklif " +
            $"({seller.LeadTime1:N0} × 1 gün, {seller.LeadTime2:N0} × 2 gün) · {date}");
        subtitle.Style.Font.Italic = true;
        subtitle.Style.Font.FontSize = 9;
        subtitle.Style.Font.FontColor = MutedColor;

        sheet.Range(TitleRow, 1, TitleRow, Headers.Length).Merge();
        sheet.Range(SubtitleRow, 1, SubtitleRow, Headers.Length).Merge();
    }
}
