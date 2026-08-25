using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Seller VAT Warnings — splits the "offers with no VAT rate" export into one
// workbook per seller and mails each seller their own.
//
// The sibling of Seller Offer Warnings, with one structural difference: there
// the attachments are produced outside the app and matched by name, here the
// app produces them. That moves the "which seller gets which file" decision
// from a hand-typed cell to a value computed in one place — see VatSplitBuilder.
// Consumed by wwwroot/js/vat-warnings.js.
// ---------------------------------------------------------------------------

/// <summary>
/// One product as it appears in a seller's own attachment.
///
/// <para>Deliberately narrower than the export it comes from. <c>Offer Total Price</c>,
/// <c>Stock Qty</c>, <c>Partner Manager</c>, <c>Seller Rating</c>, <c>country</c> and the
/// product-lifecycle columns are read out of no row at all: a column that never enters this record
/// cannot be written into a file that leaves the building.</para>
///
/// <para>Only <paramref name="Gtin"/> reaches the attachment. <paramref name="ProductTitle"/> and
/// <paramref name="Brand"/> stay on this record because the grouping needs them — a row with no
/// barcode is told apart from another by its title and brand, and an export with no
/// <c>Product Title</c> column is refused as the wrong file.</para>
///
/// <para><paramref name="Gtin"/> is the export's <c>gtin</c> column padded to 13 digits — see
/// <c>VatSplitBuilder.NormalizeGtin</c> for why it arrives short.</para>
/// </summary>
public sealed record VatOfferRow(
    string Gtin,
    string ProductTitle,
    string Brand);

/// <summary>Every product belonging to one seller, as grouped out of the export. One row per
/// product: the same GTIN offered twice is one line, because the file no longer carries the offer
/// number that would tell the two lines apart.</summary>
public sealed record VatSellerGroup(
    string SellerId,
    string SellerName,

    /// <summary>What identifies this seller: the normalised id when there is one, the folded name
    /// otherwise. The same precedence <c>SellerGroupMap.Resolve</c> applies.</summary>
    string SellerKey,

    IReadOnlyList<VatOfferRow> Offers);

/// <summary>
/// One seller's mail, as it will be sent. Subject and body are the exact text — the preview, the
/// payload posted back and what Outlook receives are all these same strings.
///
/// <para>A seller who cannot be mailed stays in the list with <paramref name="Problem"/> set rather
/// than being filtered out, for the same reason <see cref="RenderedMail"/> does it: two collections
/// invite a panel that renders one and forgets the other, and the forgotten one is the seller who
/// silently never got warned.</para>
/// </summary>
public sealed record VatSellerMail(
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
/// A seller in the export with no address. Carried separately from <see cref="VatSellerMail"/>
/// because this list is also the editor: each row is a seller the operator can give an address to,
/// and that address is remembered for next month.
/// </summary>
public sealed record VatUnmatchedSeller(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("sellerKey")] string SellerKey,

    /// <summary>How many products are waiting on this one address — the number that says which of
    /// these rows is worth chasing first.</summary>
    [property: JsonPropertyName("offerCount")] int OfferCount,

    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// Where the sellers went. Each count is a terminal bucket, so they sum to
/// <paramref name="SellersInFile"/> — the same shape as <see cref="OfferMailFunnel"/>.
/// </summary>
public sealed record VatFunnel(
    [property: JsonPropertyName("sellersInFile")] int SellersInFile,
    [property: JsonPropertyName("ready")] int Ready,

    /// <summary>Fewer products than the operator's minimum. Their workbook is not written and no mail
    /// is prepared for them — the one bucket in this funnel that is a choice rather than a fault.</summary>
    [property: JsonPropertyName("belowMinimum")] int BelowMinimum,

    [property: JsonPropertyName("noEmail")] int NoEmail,
    [property: JsonPropertyName("invalidEmail")] int InvalidEmail,

    /// <summary>The seller name appears twice in the uploaded address list carrying two different
    /// addresses. Reported, never resolved to one of them.</summary>
    [property: JsonPropertyName("ambiguousEmail")] int AmbiguousEmail,

    /// <summary>Two sellers whose names reduce to the same file name. Neither is mailed — one of them
    /// would receive the other's price list.</summary>
    [property: JsonPropertyName("fileNameClash")] int FileNameClash,

    [property: JsonPropertyName("writeFailed")] int WriteFailed);

public sealed record VatPrepareData(
    /// <summary>Identifies this batch on the way back in. See <c>VatBatchStore</c> for why the send
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

    /// <summary>Rows in the export, before duplicate products are folded together — the operator's
    /// count of what they uploaded, not the number of lines any seller receives.</summary>
    [property: JsonPropertyName("offersInFile")] int OffersInFile,
    [property: JsonPropertyName("directoryRows")] int DirectoryRows,
    [property: JsonPropertyName("mails")] IReadOnlyList<VatSellerMail> Mails,
    [property: JsonPropertyName("unmatched")] IReadOnlyList<VatUnmatchedSeller> Unmatched,
    [property: JsonPropertyName("funnel")] VatFunnel Funnel,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

// ---------------------------------------------------------------------------
// Saved configuration
// ---------------------------------------------------------------------------

/// <summary>
/// One address the operator entered by hand for a seller the uploaded list does not cover.
///
/// <para>This is the whole answer to the eight sellers that do not match: rather than widening the
/// match until they do — which is how "Yazıcı Bende" ends up receiving "Yazıcı Ticaret"'s list — the
/// operator states the address once and it is remembered.</para>
/// </summary>
public sealed record VatOverrideEntry(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("email")] string Email);

/// <summary>The whole of vat-mails.json — the templates, the hand-entered addresses and the output
/// folder, in one file for the same reason <see cref="SellerMailFile"/> holds all three.</summary>
public sealed record VatMailFile(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("updatedUtc")] string? UpdatedUtc,
    [property: JsonPropertyName("subjectTemplate")] string? SubjectTemplate,
    [property: JsonPropertyName("bodyTemplate")] string? BodyTemplate,
    [property: JsonPropertyName("outputFolder")] string? OutputFolder,

    /// <summary>How few products it takes for a seller not to be worth a mail. <c>null</c> — which is
    /// also what a settings file written before this existed deserialises to — means no minimum, so an
    /// operator who never sets one keeps the behaviour they had.</summary>
    [property: JsonPropertyName("minOfferCount")] int? MinOfferCount,

    /// <summary>Who is copied on every mail, as one <c>;</c>-joined line, or <c>null</c> for nobody.
    /// Visible to the seller: this is a CC, not a BCC, so the address is one the operator is content to
    /// show 130 sellers.</summary>
    [property: JsonPropertyName("ccAddresses")] string? CcAddresses,

    /// <summary>Whether Outlook's own signature goes under every mail. <c>null</c> — a settings file
    /// written before this existed — means off, so nothing starts signing itself unasked.</summary>
    [property: JsonPropertyName("includeSignature")] bool? IncludeSignature,

    [property: JsonPropertyName("overrides")] IReadOnlyList<VatOverrideEntry> Overrides);

// ---------------------------------------------------------------------------
// The prepared batch — server side only
// ---------------------------------------------------------------------------

/// <summary>
/// What the server remembers about one prepared mail. <b>Never serialised to the browser.</b>
///
/// <para>Seller Offer Warnings re-derives the recipient and the attachment from its saved mapping
/// table at send time, so nothing the browser posts can change either. This module has no saved
/// table — the pairing is computed from an uploaded file — so the pairing itself is what has to be
/// remembered. These records are that memory, and they are what <c>send</c> reads instead of trusting
/// the request.</para>
/// </summary>
public sealed record VatBatchMail(
    string SellerKey,
    string SellerId,
    string SellerName,
    IReadOnlyList<string> Recipients,
    string AttachmentPath,
    string AttachmentName);

public sealed record VatBatch(
    string BatchId,
    string OutputFolder,
    DateTimeOffset CreatedUtc,

    /// <summary>Who is copied on every mail in this batch. On the batch and not on each
    /// <see cref="VatBatchMail"/> because it is one decision for the whole run, and held here for the
    /// same reason the recipients are: what gets sent comes from the batch, never from the browser.</summary>
    string? Cc,

    /// <summary>Whether these mails carry the operator's signature. Fixed here at prepare time along
    /// with the CC, so a settings change between the preview and the click cannot alter what is sent.</summary>
    bool IncludeSignature,

    IReadOnlyDictionary<string, VatBatchMail> BySellerKey);
