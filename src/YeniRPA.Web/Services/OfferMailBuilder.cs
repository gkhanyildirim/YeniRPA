using System.Globalization;
using System.Text.RegularExpressions;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>
/// Renders one seller's warning e-mail and works out which file in the attachment folder is theirs.
///
/// <para>Pure and IO-free, like the report builders: <see cref="ResolveAttachment"/> computes a path
/// and says whether it is allowed, but never touches the disk. The caller does the
/// <c>File.Exists</c>. That keeps the rule that decides <em>which seller gets which file</em>
/// testable without a folder full of 1 MB workbooks.</para>
///
/// <para>The defaults here are what ships and what "Reset to default" restores. The operator's
/// edited versions live in <c>seller-mails.json</c> beside the mappings — see
/// <see cref="SellerMailStore"/>.</para>
/// </summary>
public static partial class OfferMailBuilder
{
    /// <summary>
    /// Turkish because the recipients are Turkish sellers; the UI chrome around it stays English like
    /// the rest of the app. Written to be replaced — the whole point of the template box is that the
    /// operator owns this wording.
    /// </summary>
    public const string DefaultSubjectTemplate = "{seller} — teklif termin sürelerinizle ilgili bilgilendirme";

    public const string DefaultBodyTemplate =
        """
        Sayın {seller} yetkilisi,

        Marketplace üzerindeki tekliflerinizi inceledik. Ekteki listede güncel teklifleriniz ve her birinin termin (kargoya veriliş) süresi yer alıyor.

        Termini 0 gün olan teklif sayısı: {leadTime0}
        Termini 1 gün olan teklif sayısı: {leadTime1}

        Termin sürelerinin doğru ayarlanması hem siparişin zamanında çıkışı hem de mağaza puanınız açısından önemli. Ekteki listeyi kontrol edip gerçekte karşılayamadığınız termin sürelerini panelden güncellemenizi rica ederiz.

        Bilginize sunar, iyi çalışmalar dileriz.
        """;

    public static readonly string[] Placeholders =
    [
        "{seller}", "{sellerId}", "{email}", "{recipientCount}", "{fileName}",
        "{leadTime0}", "{leadTime1}", "{leadTimeTotal}", "{date}",
    ];

    [GeneratedRegex(@"\{[A-Za-z][A-Za-z0-9]*\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>
    /// Fills both templates in for one seller. <paramref name="attachmentPath"/> and
    /// <paramref name="problem"/> come from the caller, which is the half that had to look at the
    /// disk.
    /// </summary>
    public static RenderedMail Render(
        SellerMailEntry entry,
        string date,
        string? subjectTemplate,
        string? bodyTemplate,
        string attachmentPath,
        long attachmentSizeBytes,
        string? problem)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var subject = string.IsNullOrWhiteSpace(subjectTemplate) ? DefaultSubjectTemplate : subjectTemplate;
        var body = string.IsNullOrWhiteSpace(bodyTemplate) ? DefaultBodyTemplate : bodyTemplate;

        var recipients = SellerMailStore.SplitAddresses(entry.Email);

        return new RenderedMail(
            SellerId: entry.SellerId,
            SellerName: entry.SellerName,
            Email: SellerMailStore.JoinAddresses(recipients),
            Recipients: recipients,
            // Newlines in a subject line are silently dropped by every mail client and would make the
            // approved text differ from the sent text. Folded to spaces here, once.
            Subject: Fill(subject, entry, recipients, date).Replace("\r", " ").Replace("\n", " ").Trim(),
            Body: Fill(body, entry, recipients, date).Replace("\r\n", "\n").Replace("\r", "\n"),
            AttachmentPath: attachmentPath,
            AttachmentName: entry.FileName.Trim(),
            AttachmentSizeBytes: attachmentSizeBytes,
            LeadTime0: entry.LeadTime0,
            LeadTime1: entry.LeadTime1,
            Problem: problem,
            UnknownPlaceholders: FindUnknown(subject, body));
    }

    static string Fill(string template, SellerMailEntry entry, IReadOnlyList<string> recipients, string date) => template
        .Replace("{sellerId}", entry.SellerId)
        .Replace("{email}", SellerMailStore.JoinAddresses(recipients))
        .Replace("{recipientCount}", Count(recipients.Count))
        .Replace("{fileName}", entry.FileName.Trim())
        .Replace("{leadTime0}", Count(entry.LeadTime0))
        .Replace("{leadTime1}", Count(entry.LeadTime1))
        .Replace("{leadTimeTotal}", Count(entry.LeadTime0 + entry.LeadTime1))
        .Replace("{date}", date)
        // Substituted LAST, after every other placeholder. A seller whose storefront name contains a
        // literal "{email}" would otherwise have it re-substituted — the classic template-injection
        // foot-gun, and here it would put one seller's address in another seller's mail.
        .Replace("{seller}", entry.SellerName);

    /// <summary>Turkish groups thousands with a dot, and these numbers run to five digits.</summary>
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
    /// Turns a mapping row's file name into an absolute path inside the attachment folder, or a
    /// problem.
    ///
    /// <para><b>Nothing here is approximate, and nothing approximate may ever be added.</b> Not
    /// "starts with", not "closest file name", not "the only .xlsx containing the seller name". The
    /// failure mode of an 85 %-similar match is attaching one seller's complete price and stock list
    /// to a mail addressed to a different seller — a competitor data leak delivered by our own
    /// automation, which would look exactly like a working system until someone complained.</para>
    ///
    /// <para>The containment check is the other half: a <c>DosyaAdi</c> of <c>..\..\auth.dat</c> or a
    /// UNC path would otherwise let the mapping table attach any file the app can read.</para>
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
