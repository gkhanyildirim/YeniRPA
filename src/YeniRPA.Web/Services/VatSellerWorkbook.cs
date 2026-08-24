using ClosedXML.Excel;
using YeniRPA.Web.Models;
using static YeniRPA.Web.Services.XlsxStyles;

namespace YeniRPA.Web.Services;

/// <summary>
/// Writes one seller's own list of offers with no VAT rate.
///
/// <para>Split from <see cref="VatSplitBuilder"/> the way <c>TitleCleanWorkbook</c> is split from
/// <c>TitleCleanBuilder</c>: the builder decides <em>what belongs to whom</em> and is tested on that
/// alone, this only renders a group that has already been decided.</para>
///
/// <para>The column headings are Turkish. This is the one artefact in the app that leaves the
/// building and is read by a seller, so it is written in the language of the mail that carries it —
/// the interface, the code and the operator-facing exports stay English.</para>
/// </summary>
public static class VatSellerWorkbook
{
    const int TitleRow = 1;
    const int SubtitleRow = 2;
    const int HeaderRow = 4;

    static readonly string[] Headers =
        ["Teklif No", "EAN", "Ürün Adı", "Marka", "Kategori", "Durum", "Fiyat", "Stok", "Sorun"];

    /// <summary>Columns that must be written as text. An EAN is the reason: <c>0858445004684</c> is a
    /// real barcode in this export and loses its leading zero the moment Excel reads it as a number,
    /// at which point it no longer identifies the product it names.</summary>
    static readonly int[] TextColumns = [1, 2];

    const int StockColumn = 8;

    public static byte[] Build(VatSellerGroup seller, string date)
    {
        ArgumentNullException.ThrowIfNull(seller);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("KDV Eksik Teklifler");
        ApplyBaseFont(sheet);
        sheet.ShowGridLines = false;

        WriteHeading(sheet, seller, date);

        for (var c = 0; c < Headers.Length; c++)
        {
            var cell = sheet.Cell(HeaderRow, c + 1);
            cell.SetValue(Headers[c]);
            cell.Style.Alignment.WrapText = true;
        }
        sheet.Cell(HeaderRow, StockColumn).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        StyleHeaderRow(sheet.Range(HeaderRow, 1, HeaderRow, Headers.Length));

        foreach (var column in TextColumns)
            sheet.Column(column).Style.NumberFormat.Format = "@";

        for (var i = 0; i < seller.Offers.Count; i++)
        {
            var offer = seller.Offers[i];
            var row = HeaderRow + 1 + i;

            // SetValue<string> stores text as text: a title beginning with "=" is never promoted to a
            // formula, which is the same guard TableWorkbookBuilder.WriteValue relies on.
            sheet.Cell(row, 1).SetValue(offer.OfferId);
            sheet.Cell(row, 2).SetValue(offer.Ean);
            sheet.Cell(row, 3).SetValue(offer.ProductTitle);
            sheet.Cell(row, 4).SetValue(offer.Brand);
            sheet.Cell(row, 5).SetValue(offer.Category);
            sheet.Cell(row, 6).SetValue(offer.Condition);
            sheet.Cell(row, 7).SetValue(offer.Price);

            var stock = sheet.Cell(row, StockColumn);
            if (offer.Stock.HasValue)
            {
                stock.Value = offer.Stock.Value;
                stock.Style.NumberFormat.Format = "#,##0";
            }
            stock.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            sheet.Cell(row, 9).SetValue(offer.StateReasons);
        }

        var lastRow = HeaderRow + seller.Offers.Count;
        if (seller.Offers.Count > 0)
        {
            ApplyThinBorders(sheet.Range(HeaderRow, 1, lastRow, Headers.Length));
            ApplyZebra(sheet, HeaderRow + 1, lastRow, 1, Headers.Length);
        }

        // Only the table is measured — a merged title spanning every column would otherwise stretch
        // the first column to the width of the whole heading. Same reason as TableWorkbookBuilder.
        sheet.Columns().AdjustToContents(HeaderRow, lastRow);
        foreach (var column in sheet.ColumnsUsed())
            column.Width = Math.Clamp(column.Width, 10, 46);

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
    static void WriteHeading(IXLWorksheet sheet, VatSellerGroup seller, string date)
    {
        var title = sheet.Cell(TitleRow, 1);
        title.SetValue(seller.SellerName.Length > 0 ? seller.SellerName : seller.SellerId);
        title.Style.Font.Bold = true;
        title.Style.Font.FontSize = 14;
        title.Style.Font.FontColor = NavyColor;

        var subtitle = sheet.Cell(SubtitleRow, 1);
        subtitle.SetValue($"KDV oranı tanımlı olmayan teklifler · {seller.Offers.Count:N0} teklif · {date}");
        subtitle.Style.Font.Italic = true;
        subtitle.Style.Font.FontSize = 9;
        subtitle.Style.Font.FontColor = MutedColor;

        sheet.Range(TitleRow, 1, TitleRow, Headers.Length).Merge();
        sheet.Range(SubtitleRow, 1, SubtitleRow, Headers.Length).Merge();
    }
}
