using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Seller Offer Warnings — one warning e-mail per seller, carrying that seller's
// own offer list as an attachment.
//
// The sibling of Late Order Warnings: a hand-built mapping table, an operator-owned
// template, a full preview, then a background run reported over the shared bus.
// Consumed by wwwroot/js/offer-warnings.js.
// ---------------------------------------------------------------------------

/// <summary>
/// One line of the mapping table: who the seller is, where their warning goes, and which file in the
/// attachment folder is theirs.
///
/// <para>A row with no e-mail or no file name is kept, exactly like a <see cref="SellerGroupEntry"/>
/// with a blank group: that is "seen but not finished", which is reported differently from "never
/// entered". <paramref name="LeadTime0"/> and <paramref name="LeadTime1"/> are the seller's offer
/// counts at each lead time, carried through to the message text as placeholders.</para>
/// </summary>
public sealed record SellerMailEntry(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,

    /// <summary>
    /// Every address this seller's single warning is addressed to, separated by <c>;</c> — a seller
    /// usually has more than one user in the Mirakl back office and they all belong on one mail, not
    /// on one each. Split with <c>SellerMailStore.SplitAddresses</c>.
    /// </summary>
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("leadTime0")] int LeadTime0,
    [property: JsonPropertyName("leadTime1")] int LeadTime1);

/// <summary>
/// The whole of seller-mails.json. The templates and the attachment folder live here rather than in
/// appsettings for the same reason the WhatsApp templates live beside their mappings — this is
/// operator-owned configuration, and one file to back up beats three places to look.
/// </summary>
public sealed record SellerMailFile(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("updatedUtc")] string? UpdatedUtc,
    [property: JsonPropertyName("subjectTemplate")] string? SubjectTemplate,
    [property: JsonPropertyName("bodyTemplate")] string? BodyTemplate,
    [property: JsonPropertyName("attachmentFolder")] string? AttachmentFolder,
    [property: JsonPropertyName("entries")] IReadOnlyList<SellerMailEntry> Entries);

/// <summary>
/// One e-mail, as it will be sent. Subject and body are the exact text — the preview, the payload
/// posted back and what Outlook receives are all these same strings.
///
/// <para>A row that cannot be sent stays in the list with <paramref name="Problem"/> set rather than
/// being filtered out. Two collections — "ready" and "broken" — invite a panel that renders one and
/// forgets the other, and the forgotten one is the seller who silently never got warned.</para>
/// </summary>
public sealed record RenderedMail(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,

    /// <summary>The To line as one string, for display and for Outlook.</summary>
    [property: JsonPropertyName("email")] string Email,

    /// <summary>The same addresses, split — so the panel can show and count them individually.</summary>
    [property: JsonPropertyName("recipients")] IReadOnlyList<string> Recipients,

    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("body")] string Body,

    /// <summary>The absolute path the attachment resolved to, or "" when it did not resolve.</summary>
    [property: JsonPropertyName("attachmentPath")] string AttachmentPath,

    [property: JsonPropertyName("attachmentName")] string AttachmentName,
    [property: JsonPropertyName("attachmentSizeBytes")] long AttachmentSizeBytes,
    [property: JsonPropertyName("leadTime0")] int LeadTime0,
    [property: JsonPropertyName("leadTime1")] int LeadTime1,

    /// <summary>Why this row cannot be sent, or <c>null</c> when it is ready.</summary>
    [property: JsonPropertyName("problem")] string? Problem,

    /// <summary>Placeholders the operator typed that we do not recognise, so the panel can point at
    /// the typo instead of shipping "Sayın ," to a seller.</summary>
    [property: JsonPropertyName("unknownPlaceholders")] IReadOnlyList<string> UnknownPlaceholders);

/// <summary>
/// Where the mapping rows went. Each count is a terminal bucket, so they sum to
/// <paramref name="EntriesInTable"/> — the same shape as <see cref="LateOrderFunnel"/>.
/// </summary>
public sealed record OfferMailFunnel(
    [property: JsonPropertyName("entriesInTable")] int EntriesInTable,
    [property: JsonPropertyName("ready")] int Ready,
    [property: JsonPropertyName("noEmail")] int NoEmail,
    [property: JsonPropertyName("invalidEmail")] int InvalidEmail,
    [property: JsonPropertyName("noFileName")] int NoFileName,
    [property: JsonPropertyName("fileNotFound")] int FileNotFound,

    /// <summary>
    /// Two rows describing the same seller. A repeated <em>address</em> is fine — one agency can run
    /// several storefronts and each mail carries a different attachment — but a repeated seller means
    /// the run cannot tell which of the two rows is authoritative.
    /// </summary>
    [property: JsonPropertyName("duplicateSeller")] int DuplicateSeller);

public sealed record OfferMailData(
    /// <summary>Captured once for the whole build, "yyyy-MM-dd".</summary>
    [property: JsonPropertyName("date")] string Date,

    [property: JsonPropertyName("attachmentFolder")] string AttachmentFolder,

    /// <summary>How many files the folder holds, so "0 matched" can be told apart from "wrong folder".</summary>
    [property: JsonPropertyName("filesInFolder")] int FilesInFolder,

    [property: JsonPropertyName("mails")] IReadOnlyList<RenderedMail> Mails,
    [property: JsonPropertyName("funnel")] OfferMailFunnel Funnel,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);
