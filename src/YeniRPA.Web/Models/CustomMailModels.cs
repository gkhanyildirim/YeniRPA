using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Custom Mail — a free-form subject/body the operator writes themselves, with
// one shared attachment, sent to whichever sellers they tick out of an
// uploaded seller list. The seller list names WHO to reach; their addresses
// are looked up in a separate directory upload, never read from the seller
// list's own e-mail column, even when it has one — see
// CustomMailSellerListReader for why.
//
// Unlike Seller Offer Warnings/VAT Warnings, there is no per-seller attachment
// and no template: the mail is identical for every recipient, so the batch
// only has to hold who may be mailed, not what file or wording belongs to them.
// Consumed by wwwroot/js/custom-mail.js.
// ---------------------------------------------------------------------------

/// <summary>One recipient as shown on the checkbox list: a seller from the uploaded seller list,
/// resolved to an address in the uploaded directory.</summary>
public sealed record CustomMailRecipientDto(
    /// <summary>The normalized e-mail — also the key <see cref="CustomMailBatch.ByEmailKey"/> uses,
    /// so ticking a row and sending it can never disagree about which address is meant.</summary>
    [property: JsonPropertyName("key")] string Key,

    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("email")] string Email,

    /// <summary>Where the address came from: <c>override</c> (typed in by hand) or <c>directory</c>
    /// (matched in the uploaded address directory). Shown on the card so a hand-entered address is
    /// visibly a hand-entered address — same purpose as <see cref="OfferSellerMail.MatchedBy"/>.</summary>
    [property: JsonPropertyName("matchedBy")] string MatchedBy);

/// <summary>A seller from the uploaded seller list with no usable address in the directory —
/// carried separately from <see cref="CustomMailRecipientDto"/> so a seller who cannot be reached is
/// visibly accounted for rather than silently dropped from the count.</summary>
public sealed record CustomMailUnmatchedSeller(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("reason")] string Reason,

    /// <summary>Identifies this seller for the "enter an address, save" flow — the browser posts this
    /// back rather than recomputing <see cref="CustomMailSellerListReader.SellerKey"/> itself. Same
    /// reason <see cref="OfferUnmatchedSeller.SellerKey"/> exists: a fold that disagreed by a single
    /// character would add a second row for a seller instead of replacing the first.</summary>
    [property: JsonPropertyName("sellerKey")] string SellerKey);

public sealed record CustomMailPrepareData(
    /// <summary>Identifies this batch on the way back in. See <see cref="CustomMailBatchStore"/> for
    /// why the send endpoint needs one.</summary>
    [property: JsonPropertyName("batchId")] string BatchId,

    [property: JsonPropertyName("recipients")] IReadOnlyList<CustomMailRecipientDto> Recipients,

    /// <summary>Distinct sellers read out of the seller list — the number
    /// <paramref name="Recipients"/>.Count is measured against, so "137 of 150 resolved" reads as the
    /// lookup working rather than as most of the list having gone missing.</summary>
    [property: JsonPropertyName("sellersInFile")] int SellersInFile,

    [property: JsonPropertyName("unmatched")] IReadOnlyList<CustomMailUnmatchedSeller> Unmatched,

    /// <summary>Rows the directory yielded — so "0 resolved" out of an empty directory reads
    /// differently from "0 resolved" out of the wrong sheet.</summary>
    [property: JsonPropertyName("directoryRows")] int DirectoryRows,

    [property: JsonPropertyName("maxMailsPerRun")] int MaxMailsPerRun,
    [property: JsonPropertyName("mailsPerPass")] int MailsPerPass,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

// ---------------------------------------------------------------------------
// Hand-entered addresses — persisted
// ---------------------------------------------------------------------------

/// <summary>One address the operator entered by hand for a seller the uploaded directory does not
/// cover. Same shape and the same reason as <see cref="OfferOverrideEntry"/>.</summary>
public sealed record CustomMailOverrideEntry(
    [property: JsonPropertyName("sellerId")] string SellerId,
    [property: JsonPropertyName("sellerName")] string SellerName,
    [property: JsonPropertyName("email")] string Email);

/// <summary>The whole of Custom Mail's own settings document — just the hand-entered addresses.
/// Unlike <see cref="OfferMailFile"/> there is no template, output folder or CC to remember: those
/// are typed fresh for every campaign, not carried between runs.</summary>
public sealed record CustomMailFile(
    [property: JsonPropertyName("updatedUtc")] string? UpdatedUtc,
    [property: JsonPropertyName("overrides")] IReadOnlyList<CustomMailOverrideEntry> Overrides);

// ---------------------------------------------------------------------------
// The prepared batch — server side only
// ---------------------------------------------------------------------------

/// <summary>One recipient as the server resolved them. <b>Never serialised to the browser</b> — the
/// browser only ever sends the address back, and this is what that address is checked against.</summary>
public sealed record CustomMailRecipient(string Name, string Email);

/// <summary>
/// What the server remembers about one prepared recipient list. <b>Never serialised to the browser.</b>
///
/// <para>Same shape and the same reason as <see cref="OfferBatch"/>: the recipient list is computed
/// by looking every seller in the uploaded seller list up in the uploaded address directory, and
/// exists nowhere else, so without this the only copy at send time would be whatever the browser
/// echoes back. The mail itself carries no
/// per-recipient secret the way an offer workbook does — every recipient gets the identical subject,
/// body and attachment — so the one thing this batch has to guard is <em>who</em> may be mailed, not
/// what they are mailed.</para>
/// </summary>
public sealed record CustomMailBatch(
    string BatchId,
    DateTimeOffset CreatedUtc,
    IReadOnlyDictionary<string, CustomMailRecipient> ByEmailKey);
