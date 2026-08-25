using ClosedXML.Excel;
using YeniRPA.Web.Models;
using static YeniRPA.Web.Services.XlsxStyles;

namespace YeniRPA.Web.Services;

/// <summary>
/// Writes one seller's own list of products with no VAT rate.
///
/// <para>Split from <see cref="VatSplitBuilder"/> the way <c>TitleCleanWorkbook</c> is split from
/// <c>TitleCleanBuilder</c>: the builder decides <em>what belongs to whom</em> and is tested on that
/// alone, this only renders a group that has already been decided.</para>
///
/// <para>One column: the GTIN. It is the key the seller looks the product up by in their own panel,
/// and everything else was theirs to begin with — a title and a brand they already hold tell them
/// nothing they cannot read off the barcode. Price, stock, category and offer state are ours, not
/// theirs, and never reach <see cref="VatOfferRow"/> at all.</para>
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

    static readonly string[] Headers = ["GTIN"];

    /// <summary>Columns that must be written as text. The GTIN is the reason: <c>0858445004684</c> is
    /// a real barcode in this export and loses its leading zero the moment Excel reads it as a number,
    /// at which point it no longer identifies the product it names. <c>VatSplitBuilder.NormalizeGtin</c>
    /// pads that zero back on; this format is what stops Excel taking it off again.</summary>
    static readonly int[] TextColumns = [1];

    public static byte[] Build(VatSellerGroup seller, string date)
    {
        ArgumentNullException.ThrowIfNull(seller);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("KDV Eksik Ürünler");
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

        for (var i = 0; i < seller.Offers.Count; i++)
        {
            var offer = seller.Offers[i];
            var row = HeaderRow + 1 + i;

            // SetValue<string> stores text as text: a cell beginning with "=" is never promoted to a
            // formula, which is the same guard TableWorkbookBuilder.WriteValue relies on.
            sheet.Cell(row, 1).SetValue(offer.Gtin);
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
        subtitle.SetValue($"KDV oranı tanımlı olmayan ürünler · {seller.Offers.Count:N0} ürün · {date}");
        subtitle.Style.Font.Italic = true;
        subtitle.Style.Font.FontSize = 9;
        subtitle.Style.Font.FontColor = MutedColor;

        // Nothing to merge across a single column, and the heading is longer than a GTIN is wide — it
        // simply overflows into the empty cells beside it, which is what Excel does and what a reader
        // expects. Merging it into column A instead would clip the seller's own name.
        if (Headers.Length > 1)
        {
            sheet.Range(TitleRow, 1, TitleRow, Headers.Length).Merge();
            sheet.Range(SubtitleRow, 1, SubtitleRow, Headers.Length).Merge();
        }
    }
}
