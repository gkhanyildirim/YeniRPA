using ClosedXML.Excel;

namespace YeniRPA.Web.Services;

/// <summary>
/// The look every workbook this app produces shares: navy header band, hairline borders, grey zebra
/// rows, Arial throughout. Both the four-sheet report workbook and the per-section exports go through
/// here so a file downloaded from a dashboard card cannot end up looking like a different product.
/// </summary>
internal static class XlsxStyles
{
    public static readonly XLColor NavyColor = XLColor.FromArgb(0x1F, 0x38, 0x64);
    public static readonly XLColor RedColor = XLColor.FromArgb(0xDC, 0x26, 0x26);
    public static readonly XLColor GrayZebraColor = XLColor.FromArgb(0xF3, 0xF4, 0xF6);
    public static readonly XLColor BorderColor = XLColor.FromArgb(0xD1, 0xD5, 0xDB);
    public static readonly XLColor MutedColor = XLColor.FromArgb(0x6B, 0x72, 0x80);

    public static void StyleHeaderRow(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Fill.BackgroundColor = NavyColor;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    public static void ApplyBaseFont(IXLWorksheet sheet)
    {
        sheet.Style.Font.FontName = "Arial";
    }

    public static void ApplyThinBorders(IXLRange range)
    {
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = BorderColor;
        range.Style.Border.InsideBorderColor = BorderColor;
    }

    public static void ApplyZebra(IXLWorksheet sheet, int firstDataRow, int lastDataRow, int firstCol, int lastCol)
    {
        for (var r = firstDataRow; r <= lastDataRow; r++)
        {
            if ((r - firstDataRow) % 2 == 1)
                sheet.Range(r, firstCol, r, lastCol).Style.Fill.BackgroundColor = GrayZebraColor;
        }
    }
}
