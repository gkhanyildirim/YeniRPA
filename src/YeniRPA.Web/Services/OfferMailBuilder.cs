using System.Globalization;
using System.Text.RegularExpressions;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Renders one seller's lead-time warning e-mail, and works out which file in an output folder is
/// theirs.
///
/// <para>Pure and IO-free: <see cref="ResolveAttachment"/> computes a path and says whether it is
/// allowed, but never touches the disk. The caller does the <c>File.Exists</c>. That keeps the rule
/// that decides <em>which seller gets which file</em> testable without a folder full of workbooks.</para>
///
/// <para><see cref="ResolveAttachment"/> is shared with Seller VAT Warnings, which calls it on both its
/// write and its send path. It is the one piece of this module the twin deliberately reuses rather than
/// copies, because it states an invariant — a file name is a name, resolved inside one folder and
/// nowhere else — that must not be allowed to drift apart between the two.</para>
///
/// <para>The templates here are what ships and what "Reset to default" restores. The operator's edited
/// versions live in <c>offer-warnings.json</c> beside the hand-entered addresses — see
/// <see cref="OfferMailStore"/>.</para>
/// </summary>
public static partial class OfferMailBuilder
{
    /// <summary>
    /// Turkish because the recipients are Turkish sellers; the UI chrome around it stays English like
    /// the rest of the app. Written to be replaced — the whole point of the template box is that the
    /// operator owns this wording.
    /// </summary>
    public const string DefaultSubjectTemplate =
        "{seller} — termin süresi 1-2 gün olan {offerCount} teklifiniz hakkında";

    public const string DefaultBodyTemplate =
        """
        Sayın {seller} yetkilisi,

        Marketplace üzerindeki tekliflerinizi incelediğimizde, {offerCount} teklifinizin termin (kargoya veriliş) süresinin çok kısa tanımlandığını gördük.

        Termini 1 gün olan teklif sayısı: {leadTime1}
        Termini 2 gün olan teklif sayısı: {leadTime2}

        Etkilenen tekliflerin tamamı ekteki listede yer alıyor ({fileName}). Listede her teklifin Product SKU bilgisi ve tanımlı termin süresi bulunuyor.

        Gerçekte karşılayamadığınız bir termin süresi, siparişin geç çıkmasına, iptallere ve mağaza puanınızın düşmesine yol açıyor. Ekteki listeyi kontrol edip karşılayamadığınız termin sürelerini satıcı panelinizden güncellemenizi rica ederiz.

        Konuyla ilgili sorularınız için bu maili yanıtlamanız yeterli.

        Bilginize sunar, iyi çalışmalar dileriz.
        """;

    /// <summary>
    /// <c>{offerCount}</c> is the number of lines in the attachment — the sum of <c>{leadTime1}</c> and
    /// <c>{leadTime2}</c>, not the number of rows the export held, which differ when a seller lists one
    /// SKU twice at the same lead time.
    /// </summary>
    public static readonly string[] Placeholders =
    [
        "{seller}", "{sellerId}", "{email}", "{recipientCount}", "{fileName}",
        "{offerCount}", "{leadTime1}", "{leadTime2}", "{date}",
    ];

    [GeneratedRegex(@"\{[A-Za-z][A-Za-z0-9]*\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>
    /// Fills both templates in for one seller. <paramref name="attachmentName"/>,
    /// <paramref name="attachmentSizeBytes"/> and <paramref name="problem"/> come from the caller,
    /// which is the half that had to look at the disk.
    /// </summary>
    public static OfferSellerMail Render(
        OfferSellerGroup seller,
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

        return new OfferSellerMail(
            SellerId: seller.SellerId,
            SellerName: seller.SellerName,
            SellerKey: seller.SellerKey,
            Email: SellerMailStore.JoinAddresses(recipients),
            Recipients: recipients,
            // Newlines in a subject line are silently dropped by every mail client and would make the
            // approved text differ from the sent text. Folded to spaces here, once — CRLF first, so a
            // subject pasted out of Word arrives with one space rather than two.
            Subject: Fill(subject, seller, recipients, attachmentName, date)
                .Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Trim(),
            Body: Fill(body, seller, recipients, attachmentName, date)
                .Replace("\r\n", "\n").Replace("\r", "\n"),
            AttachmentName: attachmentName,
            AttachmentSizeBytes: attachmentSizeBytes,
            OfferCount: seller.Offers.Count,
            LeadTime1: seller.LeadTime1,
            LeadTime2: seller.LeadTime2,
            MatchedBy: matchedBy,
            Problem: problem,
            UnknownPlaceholders: FindUnknown(subject, body));
    }

    static string Fill(
        string template,
        OfferSellerGroup seller,
        IReadOnlyList<string> recipients,
        string attachmentName,
        string date) => template
        .Replace("{sellerId}", seller.SellerId)
        .Replace("{email}", SellerMailStore.JoinAddresses(recipients))
        .Replace("{recipientCount}", Count(recipients.Count))
        .Replace("{fileName}", attachmentName)
        .Replace("{offerCount}", Count(seller.Offers.Count))
        .Replace("{leadTime1}", Count(seller.LeadTime1))
        .Replace("{leadTime2}", Count(seller.LeadTime2))
        .Replace("{date}", date)
        // Substituted LAST, after every other placeholder. A seller whose storefront name contains a
        // literal "{email}" would otherwise have it re-substituted — the classic template-injection
        // foot-gun, and here it would put one seller's address in another seller's mail.
        .Replace("{seller}", seller.SellerName);

    /// <summary>Turkish groups thousands with a dot, and these counts run to four digits.</summary>
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

    // ---------------------------------------------------------------------
    // Attachment resolution
    // ---------------------------------------------------------------------

    /// <summary>What <see cref="ResolveAttachment"/> decided. <c>Path</c> is set only when
    /// <c>Problem</c> is null.</summary>
    public readonly record struct AttachmentMatch(string Path, string? Problem);

    /// <summary>
    /// Turns a seller's file name into an absolute path inside the output folder, or a problem.
    ///
    /// <para><b>Nothing here is approximate, and nothing approximate may ever be added.</b> Not
    /// "starts with", not "closest file name", not "the only .xlsx containing the seller name". The
    /// failure mode of an 85 %-similar match is attaching one seller's complete offer list to a mail
    /// addressed to a different seller — a competitor data leak delivered by our own automation, which
    /// would look exactly like a working system until someone complained.</para>
    ///
    /// <para>The containment check is the other half: a name of <c>..\..\auth.dat</c> or a UNC path
    /// would otherwise let a spreadsheet cell attach any file the app can read.</para>
    /// </summary>
    public static AttachmentMatch ResolveAttachment(string folder, string fileName)
    {
        var name = (fileName ?? "").Trim();
        if (name.Length == 0)
            return new AttachmentMatch("", "No file name is entered for this seller.");

        var root = (folder ?? "").Trim();
        if (root.Length == 0)
            return new AttachmentMatch("", "No attachment folder is configured.");

        // A file name is a name, not a path. Rejecting separators outright is both the clearest
        // message and the check that cannot be talked around by clever encoding.
        if (name.Contains(System.IO.Path.DirectorySeparatorChar) ||
            name.Contains(System.IO.Path.AltDirectorySeparatorChar) ||
            name.Contains(':'))
        {
            return new AttachmentMatch("", $"'{name}' is a path, not a file name. Enter just the file name as it appears in the folder.");
        }

        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            return new AttachmentMatch("", $"'{name}' contains characters that cannot appear in a file name.");

        string rootFull;
        string candidate;
        try
        {
            rootFull = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
            candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, name));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new AttachmentMatch("", $"'{name}' could not be resolved inside the attachment folder: {ex.Message}");
        }

        // Belt and braces. The separator rejection above already makes this unreachable; it stays
        // because it is the check that actually states the invariant, and the one that survives
        // someone loosening the rule above.
        var prefix = rootFull + System.IO.Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new AttachmentMatch("", $"'{name}' resolves outside the attachment folder.");

        return new AttachmentMatch(candidate, null);
    }
}
