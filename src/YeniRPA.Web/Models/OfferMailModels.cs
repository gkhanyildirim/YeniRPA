using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Seller Offer Warnings — splits the Mirakl offer export into one workbook per
// seller, listing that seller's offers with a lead time to ship of 1 or 2 days,
// and mails each seller their own.
//
// The twin of Seller VAT Warnings: two uploads in, one attachment per seller
// out, the seller → address → file pairing computed here and re-read from the
// held batch at send time rather than taken from the browser. The two modules
// are deliberately separate code with the same shape — see VatMailStore for why
// a shared base class is the wrong answer here.
// Consumed by wwwroot/js/offer-warnings.js.
// ---------------------------------------------------------------------------

/// <summary>
/// One offer as it appears in a seller's own attachment.
///
/// <para>Deliberately narrower than the 26-column export it comes from. <c>Price</c>,
/// <c>Original price</c>, <c>Discount price</c>, <c>Quantity</c>, <c>Category</c> and the rest are read
/// out of no row at all: a column that never enters this record cannot be written into a file that
/// leaves the building, and the file that leaves the building is the one that would hand a competitor a
/// complete price and stock list if it ever reached the wrong inbox.</para>
///
/// <para><paramref name="ProductSku"/> is what the seller looks the offer up by in their own panel, and
/// <paramref name="LeadTime"/> is the value they are being asked to correct. Nothing else is needed to
/// act on the mail.</para>
/// </summary>
public sealed record OfferLeadRow(
    string ProductSku,
    int LeadTime);

/// <summary>Every short-lead-time offer belonging to one seller, as grouped out of the export.</summary>
public sealed record OfferSellerGroup(
    string SellerId,
    string SellerName,

    /// <summary>What identifies this seller: the normalised id when there is one, the folded name
    /// otherwise. The same precedence <c>SellerGroupMap.Resolve</c> applies.</summary>
    string SellerKey,

    IReadOnlyList<OfferLeadRow> Offers,

    /// <summary>How many of <paramref name="Offers"/> ship in one day, and in two. Carried separately
    /// because the mail quotes both counts and the attachment is a single mixed list.</summary>
    int LeadTime1,
    int LeadTime2);

/// <summary>
/// One seller's mail, as it will be sent. Subject and body are the exact text — the preview, the
/// payload posted back and what Outlook receives are all these same strings.
///
/// <para>A seller who cannot be mailed stays in the list with <paramref name="Problem"/> set rather
/// than being filtered out. Two collections — "ready" and "broken" — invite a panel that renders one
/// and forgets the other, and the forgotten one is the seller who silently never got warned.</para>
/// </summary>
public sealed record OfferSellerMail(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("sellerKey")] string SellerKey,

    /// <summary>The To line as one string, for display and for Outlook.</summary>
    [property: JsonPropertyName("email")] string Email,

    /// <summary>The same addresses, split — so the panel can show and count them individually.</summary>
    [property: JsonPropertyName("recipients")] IReadOnlyList<string> Recipients,

    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("attachmentName")] string AttachmentName,
    [property: JsonPropertyName("attachmentSizeBytes")] long AttachmentSizeBytes,
    [property: JsonPropertyName("offerCount")] int OfferCount,
    [property: JsonPropertyName("leadTime1")] int LeadTime1,
    [property: JsonPropertyName("leadTime2")] int LeadTime2,

    /// <summary>Where the address came from: <c>override</c> (typed in by hand) or <c>directory</c>
    /// (matched in the uploaded seller list). Shown on the card so a hand-entered address is visibly
    /// a hand-entered address.</summary>
    [property: JsonPropertyName("matchedBy")] string MatchedBy,

    /// <summary>Why this seller cannot be mailed, or <c>null</c> when they are ready.</summary>
    [property: JsonPropertyName("problem")] string? Problem,

    /// <summary>Placeholders the operator typed that we do not recognise, so the panel can point at
    /// the typo instead of shipping "Sayın ," to a seller.</summary>
    [property: JsonPropertyName("unknownPlaceholders")] IReadOnlyList<string> UnknownPlaceholders);

/// <summary>
/// A seller in the export with no address. Carried separately from <see cref="OfferSellerMail"/>
/// because this list is also the editor: each row is a seller the operator can give an address to,
/// and that address is remembered for next month.
/// </summary>
public sealed record OfferUnmatchedSeller(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("sellerKey")] string SellerKey,

    /// <summary>How many offers are waiting on this one address — the number that says which of these
    /// rows is worth chasing first.</summary>
    [property: JsonPropertyName("offerCount")] int OfferCount,

    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// Where the sellers went. Each count is a terminal bucket, so they sum to
/// <paramref name="SellersInFile"/> — the same shape as <see cref="VatFunnel"/>.
/// </summary>
public sealed record OfferFunnel(
    [property: JsonPropertyName("sellersInFile")] int SellersInFile,
    [property: JsonPropertyName("ready")] int Ready,

    /// <summary>Fewer offers than the operator's minimum. Their workbook is not written and no mail is
    /// prepared for them — the one bucket in this funnel that is a choice rather than a fault, and the
    /// lever that brings a 287-seller run under the 250-mail limit.</summary>
    [property: JsonPropertyName("belowMinimum")] int BelowMinimum,

    [property: JsonPropertyName("noEmail")] int NoEmail,
    [property: JsonPropertyName("invalidEmail")] int InvalidEmail,

    /// <summary>The seller appears twice in the uploaded address list carrying two different addresses.
    /// Reported, never resolved to one of them.</summary>
    [property: JsonPropertyName("ambiguousEmail")] int AmbiguousEmail,

    /// <summary>Two sellers whose names reduce to the same file name. Neither is mailed — one of them
    /// would receive the other's offer list.</summary>
    [property: JsonPropertyName("fileNameClash")] int FileNameClash,

    [property: JsonPropertyName("writeFailed")] int WriteFailed);

public sealed record OfferPrepareData(
    /// <summary>Identifies this batch on the way back in. See <c>OfferBatchStore</c> for why the send
    /// endpoint needs one.</summary>
    [property: JsonPropertyName("batchId")] string BatchId,

    /// <summary>Captured once for the whole build, "yyyy-MM-dd".</summary>
    [property: JsonPropertyName("date")] string Date,

    [property: JsonPropertyName("outputFolder")] string OutputFolder,

    /// <summary>The CC line this batch was built with, or <c>null</c>. Sent back so the panel shows the
    /// address that was fixed into the batch rather than whatever the settings box says now — editing
    /// the box after a build changes nothing until the mails are built again.</summary>
    [property: JsonPropertyName("cc")] string? Cc,

    /// <summary>Whether this batch signs its mails, for the same reason <paramref name="Cc"/> comes
    /// back: the panel shows what was fixed into the batch, not what the settings box says now.</summary>
    [property: JsonPropertyName("includeSignature")] bool IncludeSignature,

    /// <summary>Rows in the export that carry a lead time of 1 or 2, before duplicates are folded
    /// together. Not the size of the file: the export also holds every other lead time, and those rows
    /// are counted separately below.</summary>
    [property: JsonPropertyName("offersInFile")] int OffersInFile,

    /// <summary>Rows the lead-time filter dropped, so "287 sellers out of a 203 000-row file" reads as
    /// the filter working rather than as most of the export having gone missing.</summary>
    [property: JsonPropertyName("offersFilteredOut")] int OffersFilteredOut,

    [property: JsonPropertyName("directoryRows")] int DirectoryRows,
    [property: JsonPropertyName("mails")] IReadOnlyList<OfferSellerMail> Mails,
    [property: JsonPropertyName("unmatched")] IReadOnlyList<OfferUnmatchedSeller> Unmatched,
    [property: JsonPropertyName("funnel")] OfferFunnel Funnel,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

// ---------------------------------------------------------------------------
// Saved configuration
// ---------------------------------------------------------------------------

/// <summary>
/// One address the operator entered by hand for a seller the uploaded list does not cover.
///
/// <para>This is the whole answer to the sellers that do not match: rather than widening the match
/// until they do — which is how "Yazıcı Bende" ends up receiving "Yazıcı Ticaret"'s list — the operator
/// states the address once and it is remembered.</para>
/// </summary>
public sealed record OfferOverrideEntry(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("email")] string Email);

/// <summary>The whole of offer-warnings.json — the templates, the hand-entered addresses and the output
/// folder, in one file for the same reason <see cref="VatMailFile"/> holds all three.</summary>
public sealed record OfferMailFile(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("updatedUtc")] string? UpdatedUtc,
    [property: JsonPropertyName("subjectTemplate")] string? SubjectTemplate,
    [property: JsonPropertyName("bodyTemplate")] string? BodyTemplate,
    [property: JsonPropertyName("outputFolder")] string? OutputFolder,

    /// <summary>How few offers it takes for a seller not to be worth a mail. <c>null</c> means no
    /// minimum, so an operator who never sets one mails every seller in the export.</summary>
    [property: JsonPropertyName("minOfferCount")] int? MinOfferCount,

    /// <summary>Who is copied on every mail, as one <c>;</c>-joined line, or <c>null</c> for nobody.
    /// Visible to the seller: this is a CC, not a BCC.</summary>
    [property: JsonPropertyName("ccAddresses")] string? CcAddresses,

    /// <summary>Whether Outlook's own signature goes under every mail. <c>null</c> means off, so
    /// nothing starts signing itself unasked.</summary>
    [property: JsonPropertyName("includeSignature")] bool? IncludeSignature,

    [property: JsonPropertyName("overrides")] IReadOnlyList<OfferOverrideEntry> Overrides);

// ---------------------------------------------------------------------------
// The prepared batch — server side only
// ---------------------------------------------------------------------------

/// <summary>
/// What the server remembers about one prepared mail. <b>Never serialised to the browser.</b>
///
/// <para>The pairing of seller, address and file is computed from two uploaded files, so it exists
/// nowhere else — if it were not held here the only copy at send time would be the one in the browser.
/// Taking a client-supplied address or path is how an automation ends up mailing one seller's complete
/// offer list to another.</para>
/// </summary>
public sealed record OfferBatchMail(
    string SellerKey,
    string SellerId,
    string SellerName,
    IReadOnlyList<string> Recipients,
    string AttachmentPath,
    string AttachmentName);

public sealed record OfferBatch(
    string BatchId,
    string OutputFolder,
    DateTimeOffset CreatedUtc,

    /// <summary>Who is copied on every mail in this batch. On the batch and not on each
    /// <see cref="OfferBatchMail"/> because it is one decision for the whole run, and held here for the
    /// same reason the recipients are: what gets sent comes from the batch, never from the browser.</summary>
    string? Cc,

    /// <summary>Whether these mails carry the operator's signature. Fixed here at prepare time along
    /// with the CC, so a settings change between the preview and the click cannot alter what is sent.</summary>
    bool IncludeSignature,

    IReadOnlyDictionary<string, OfferBatchMail> BySellerKey);
