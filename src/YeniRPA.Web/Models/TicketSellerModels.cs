using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// Ticket → Seller lookup
//
// Matches the Oracle ticket export against the Mirakl orders export by order number so a case shows
// which seller it belongs to. Consumed by wwwroot/js/ticket-seller.js.
// ---------------------------------------------------------------------------

/// <summary>
/// One line of the lookup. A ticket whose order number splits across sellers in the orders export
/// produces one row per seller, each carrying the same <paramref name="TicketNo"/> and the same
/// <paramref name="MatchCount"/>.
/// </summary>
public sealed record TicketSellerRow(
    /// <summary>"Referans No" — the case number.</summary>
    [property: JsonPropertyName("ticketNo")] string TicketNo,

    /// <summary>The order number as it appeared in the ticket export, before the orders lookup.</summary>
    [property: JsonPropertyName("sourceOrderNo")] string SourceOrderNo,

    /// <summary>Full Mirakl number ("01259_322073064-A"); falls back to the source number when unmatched.</summary>
    [property: JsonPropertyName("orderNumber")] string OrderNumber,

    /// <summary>"Konu", or the category chain when the ticket has no subject of its own.</summary>
    [property: JsonPropertyName("subject")] string Subject,

    [property: JsonPropertyName("seller")] string Seller,
    [property: JsonPropertyName("sellerId")] string SellerId,

    /// <summary>"Kuyruk" — the queue the case sits in.</summary>
    [property: JsonPropertyName("queue")] string Queue,

    /// <summary>One of <see cref="TicketSellerMatchState"/>.</summary>
    [property: JsonPropertyName("matchState")] string MatchState,

    /// <summary>How many rows this one ticket produced — 1 unless the order split across sellers.</summary>
    [property: JsonPropertyName("matchCount")] int MatchCount);

/// <summary>The three outcomes of the order-number lookup, as written into <see cref="TicketSellerRow.MatchState"/>.</summary>
public static class TicketSellerMatchState
{
    public const string Matched = "matched";

    /// <summary>The ticket carries no usable order number ("Değer Yok", empty, or the "0" placeholder).</summary>
    public const string NoOrderNumber = "no-order";

    public const string NotFound = "not-found";
}

public sealed record TicketSellerData(
    [property: JsonPropertyName("rows")] IReadOnlyList<TicketSellerRow> Rows,

    /// <summary>HQ tickets read from the file. Lower than the row count when orders split across sellers.</summary>
    [property: JsonPropertyName("ticketCount")] int TicketCount,

    /// <summary>Order lines read from the orders export, so an obviously short upload is visible.</summary>
    [property: JsonPropertyName("orderLineCount")] int OrderLineCount,

    /// <summary>
    /// Tickets the file held for non-HQ queues. Reported only so the operator can see the case list
    /// was larger than the dashboard — none of these appear in <see cref="Rows"/>.
    /// </summary>
    [property: JsonPropertyName("otherQueueCount")] int OtherQueueCount);
