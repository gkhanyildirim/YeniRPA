using System.Globalization;
using System.Text.RegularExpressions;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Renders one seller's VAT warning e-mail.
///
/// <para>Pure and IO-free. Which file is attached is decided by <see cref="VatSplitBuilder"/> and
/// written by <see cref="VatSellerWorkbook"/>; this only fills in the wording, so the templates can
/// be exercised in tests without a folder full of workbooks.</para>
///
/// <para>The defaults here are what ships and what "Reset to default" restores. The operator's edited
/// versions live in <c>vat-mails.json</c> beside the hand-entered addresses — see
/// <see cref="VatMailStore"/>.</para>
/// </summary>
public static partial class VatMailBuilder
{
    /// <summary>
    /// Turkish because the recipients are Turkish sellers; the UI chrome around it stays English like
    /// the rest of the app. Written to be replaced — the whole point of the template box is that the
    /// operator owns this wording.
    /// </summary>
    public const string DefaultSubjectTemplate =
        "{seller} — KDV oranı tanımlı olmayan {offerCount} teklifiniz hakkında";

    public const string DefaultBodyTemplate =
        """
        Sayın {seller} yetkilisi,

        Marketplace üzerindeki tekliflerinizi incelediğimizde, {offerCount} teklifinizde KDV oranının tanımlı olmadığını tespit ettik. KDV oranı girilmemiş teklifler sitede satışa kapalı kalıyor; müşteriler bu ürünleri listelerde göremiyor ve satın alamıyor.

        Etkilenen tekliflerin tamamı ekteki listede yer alıyor ({fileName}). Listede her teklifin numarası, barkodu, ürün adı, markası, güncel fiyatı ve stok adedi bulunuyor.

        Yapmanız gereken: Satıcı panelinizden bu tekliflerin KDV oranını tanımlamak. Oran girildikten sonra teklifleriniz kısa süre içinde yeniden satışa açılacaktır.

        Konuyla ilgili sorularınız için bu maili yanıtlamanız yeterli.

        Bilginize sunar, iyi çalışmalar dileriz.
        """;

    public static readonly string[] Placeholders =
    [
        "{seller}", "{sellerId}", "{email}", "{recipientCount}", "{fileName}",
        "{offerCount}", "{date}",
    ];

    [GeneratedRegex(@"\{[A-Za-z][A-Za-z0-9]*\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>
    /// Fills both templates in for one seller. <paramref name="attachmentName"/>,
    /// <paramref name="attachmentSizeBytes"/> and <paramref name="problem"/> come from the caller,
    /// which is the half that had to look at the disk.
    /// </summary>
    public static VatSellerMail Render(
        VatSellerGroup seller,
        IReadOnlyList<string> recipients,
        string attachmentName,
        long attachmentSizeBytes,
        string date,
        string? subjectTemplate,
        string? bodyTemplate,
        string matchedBy,
        string? problem)
    {
        ArgumentNullException.ThrowIfNull(seller);
        ArgumentNullException.ThrowIfNull(recipients);

        var subject = string.IsNullOrWhiteSpace(subjectTemplate) ? DefaultSubjectTemplate : subjectTemplate;
        var body = string.IsNullOrWhiteSpace(bodyTemplate) ? DefaultBodyTemplate : bodyTemplate;

        return new VatSellerMail(
            SellerId: seller.SellerId,
            SellerName: seller.SellerName,
            SellerKey: seller.SellerKey,
            Email: SellerMailStore.JoinAddresses(recipients),
            Recipients: recipients,
            // Newlines in a subject line are silently dropped by every mail client and would make the
            // approved text differ from the sent text. Folded to spaces here, once.
            //
            // CRLF is collapsed before the halves are, so a subject pasted out of Word arrives with one
            // space rather than two. OfferMailBuilder folds the two characters separately and is left
            // as it is: its wording is in use and a subject line that suddenly loses a space is a
            // change nobody asked for.
            Subject: Fill(subject, seller, recipients, attachmentName, date)
                .Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Trim(),
            Body: Fill(body, seller, recipients, attachmentName, date).Replace("\r\n", "\n").Replace("\r", "\n"),
            AttachmentName: attachmentName,
            AttachmentSizeBytes: attachmentSizeBytes,
            OfferCount: seller.Offers.Count,
            MatchedBy: matchedBy,
            Problem: problem,
            UnknownPlaceholders: FindUnknown(subject, body));
    }

    static string Fill(
        string template,
        VatSellerGroup seller,
        IReadOnlyList<string> recipients,
        string attachmentName,
        string date) => template
        .Replace("{sellerId}", seller.SellerId)
        .Replace("{email}", SellerMailStore.JoinAddresses(recipients))
        .Replace("{recipientCount}", Count(recipients.Count))
        .Replace("{fileName}", attachmentName)
        .Replace("{offerCount}", Count(seller.Offers.Count))
        .Replace("{date}", date)
        // Substituted LAST, after every other placeholder. A seller whose storefront name contains a
        // literal "{email}" would otherwise have it re-substituted — the classic template-injection
        // foot-gun, and here it would put one seller's address in another seller's mail.
        .Replace("{seller}", seller.SellerName);

    /// <summary>Turkish groups thousands with a dot, and an offer count runs to four digits.</summary>
    static string Count(int value) => value.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));

    /// <summary>
    /// Placeholders the operator typed that we do not recognise. They are left in the output verbatim
    /// rather than thrown away: deleting them would ship "Sayın ," to a seller, and throwing would let
    /// one typo block the whole preview. The panel points at them instead.
    /// </summary>
    static IReadOnlyList<string> FindUnknown(string subject, string body)
    {
        var unknown = new List<string>();

        foreach (Match match in PlaceholderPattern().Matches(subject + "\n" + body))
        {
            if (!Placeholders.Contains(match.Value, StringComparer.Ordinal) &&
                !unknown.Contains(match.Value, StringComparer.Ordinal))
            {
                unknown.Add(match.Value);
            }
        }

        return unknown;
    }
}
