namespace YeniRPA.Web.Models;

/// <summary>
/// One "download this section as Excel" button. <paramref name="Key"/> is the id of the element the
/// dashboard renders that section into — the JavaScript files the section's data under the same id,
/// which is what ties the button to its data without either side holding a second list of sections.
/// </summary>
/// <param name="Title">Human name for the sheet, its heading and the download file name.</param>
/// <param name="Prefix">Report the section belongs to; prepended to the file name.</param>
public sealed record ExportButton(string Key, string Title, string Prefix = "Order Report");
