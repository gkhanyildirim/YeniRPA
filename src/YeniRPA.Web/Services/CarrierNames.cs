using System.Text;

namespace YeniRPA.Web.Services;

/// <summary>One shipping company, the spellings that resolve to it and the hosts it tracks on.</summary>
/// <param name="Name">Canonical display name. Must contain the keyword that
/// <see cref="OrderReportBuilder.IntegratedCarrierKeywords"/> looks for when the carrier is an
/// integrated one, since the integrated/manual split is decided on this name rather than on the
/// text the seller typed.</param>
/// <param name="Aliases">Folded fragments. One with a space matches anywhere in the folded name; a
/// four-character-or-longer one matches a word or the start of a word ("araskargo"); a shorter one
/// only matches a whole word, so "ups" cannot fire inside an unrelated name.</param>
/// <param name="Hosts">Registrable hosts of that carrier's tracking site, without <c>www.</c>.</param>
public sealed record CarrierEntry(string Name, string[] Aliases, string[] Hosts);

/// <summary>
/// Turns the free-text <c>Shipping company</c> value into a canonical carrier name.
///
/// <para>The Mirakl orders export carries no carrier code: <c>Shipping method</c> is the delivery
/// type ("Standard delivery") on every line, <c>Tracking number</c> holds no company information,
/// and <c>Shipping company</c> is typed by the seller. The same carrier therefore arrives as
/// <c>YURTİÇİ</c>, <c>Yurtiçi</c> and <c>yurtici</c> and splits into three rows on the carrier
/// table, each with its own share and its own integrated/manual badge. <c>Tracking URL</c> is the
/// one trustworthy signal — the host names the carrier — but it is only filled in on the lines
/// where the seller pasted it, so it decides where it exists and the name is canonicalised where
/// it does not.</para>
///
/// <para><b>There is no fuzzy matching here and none may be added</b> — no Levenshtein, no "closest
/// carrier". A name that matches nothing in the catalogue is never attached to a similar-looking
/// one; it keeps its own group and merges only with its own spelling variants. The same rule, and
/// the same reasoning, as <see cref="SellerGroupMap"/>: a wrong merge is invisible and looks like a
/// working report right up until someone reconciles a carrier invoice against it.</para>
/// </summary>
public static class CarrierNames
{
    /// <summary>
    /// Order is load-bearing: the first entry that matches wins. If a carrier is ever split back out
    /// of a broader one — the express arm of a parcel network, say — its entry has to come first, or
    /// the broader alias swallows it.
    ///
    /// <para>DHL is one entry on purpose. The export carries "DHL" and "DHL e-Commerce" as separate
    /// shipping companies, but operations reconciles them as one carrier, so both spellings fold
    /// together here.</para>
    /// </summary>
    public static readonly CarrierEntry[] Catalog =
    [
        new("DHL", ["dhl", "dhl ecommerce", "dhl e commerce", "dhl ecom", "dhlecom"],
            ["dhl.com", "dhl.com.tr", "dhlecommerce.com"]),
        new("Yurtiçi Kargo", ["yurtici", "yurt ici"], ["yurticikargo.com"]),
        new("Aras Kargo", ["aras"], ["araskargo.com.tr"]),
        new("MNG Kargo", ["mng", "mngkargo"], ["mngkargo.com.tr"]),
        new("Sürat Kargo", ["surat"], ["suratkargo.com.tr"]),
        new("PTT Kargo", ["ptt", "pttkargo"], ["ptt.gov.tr", "pttkargo.gov.tr"]),
        new("Hepsijet", ["hepsijet"], ["hepsijet.com"]),
        new("Sendeo", ["sendeo"], ["sendeo.com.tr"]),
        new("Kolay Gelsin", ["kolay gelsin", "kolaygelsin"], ["kolaygelsin.com"]),
        new("Trendyol Express", ["trendyol"], ["trendyolexpress.com"]),
        new("Horoz Lojistik", ["horoz"], ["horoz.com.tr"]),
        new("Borusan Lojistik", ["borusan"], ["borusanlojistik.com"]),
        new("Ekol Lojistik", ["ekol"], ["ekol.com"]),
        new("Netlog Lojistik", ["netlog"], ["netlog.com.tr"]),
        new("CEVA Lojistik", ["ceva"], ["cevalogistics.com"]),
        new("UPS", ["ups"], ["ups.com"]),
        new("Aramex", ["aramex"], ["aramex.com"]),
        new("FedEx", ["fedex"], ["fedex.com"]),
        new("TNT", ["tnt"], ["tnt.com"]),
        new("Arçelik Yetkili Servis", ["arcelik"], []),
    ];

    /// <summary>
    /// Comparison key for a carrier name: <see cref="SellerGroupMap.FoldName"/> (which folds the
    /// Turkish i family, collapses whitespace and lowercases) plus the remaining Turkish letters
    /// folded to ASCII and every punctuation mark turned into a space.
    ///
    /// <para>Both halves are needed. Without the i fold <c>YURTİÇİ</c> and <c>Yurtiçi</c> stay
    /// apart; without the accent fold <c>yurtici</c> stays apart from both. Punctuation is what
    /// separates <c>DHL e-Commerce</c> from <c>DHL eCommerce</c>.</para>
    /// </summary>
    public static string Fold(string raw)
    {
        var folded = SellerGroupMap.FoldName(raw);
        if (folded.Length == 0)
            return "";

        var builder = new StringBuilder(folded.Length);
        var pendingSpace = false;

        foreach (var ch in folded)
        {
            var mapped = ch switch
            {
                'ç' => 'c',
                'ş' => 's',
                'ğ' => 'g',
                'ö' => 'o',
                'ü' => 'u',
                'â' => 'a',
                'î' => 'i',
                'û' => 'u',
                _ => ch,
            };

            if (!char.IsLetterOrDigit(mapped))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(mapped);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The canonical carrier for one order line, or <c>null</c> when nothing in the catalogue
    /// recognises it — the caller groups those by <see cref="Fold"/> instead, so an unknown carrier
    /// still stops appearing three times.
    /// </summary>
    public static string? Resolve(string shippingCompany, string trackingUrl) =>
        FromTrackingUrl(trackingUrl) ?? FromName(shippingCompany);

    /// <summary>
    /// The tracking link is copied out of the carrier's own site, so its host outranks whatever was
    /// typed into the shipping company field.
    /// </summary>
    static string? FromTrackingUrl(string trackingUrl)
    {
        var value = (trackingUrl ?? "").Trim();
        if (value.Length == 0)
            return null;

        // Sellers paste bare hosts as often as full links; Uri needs a scheme either way.
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        if (host.Length == 0)
            return null;

        foreach (var entry in Catalog)
        {
            foreach (var candidate in entry.Hosts)
            {
                if (host == candidate || host.EndsWith("." + candidate, StringComparison.Ordinal))
                    return entry.Name;
            }
        }

        return null;
    }

    static string? FromName(string shippingCompany)
    {
        var folded = Fold(shippingCompany);
        if (folded.Length == 0)
            return null;

        var words = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var entry in Catalog)
        {
            foreach (var alias in entry.Aliases)
            {
                if (Matches(folded, words, alias))
                    return entry.Name;
            }
        }

        return null;
    }

    static bool Matches(string folded, string[] words, string alias)
    {
        if (alias.Contains(' ', StringComparison.Ordinal))
            return folded.Contains(alias, StringComparison.Ordinal);

        // A short alias is a word or nothing: "ups" inside another word is a coincidence, not a
        // carrier. A longer one may also start a word, which is how "araskargo" resolves.
        return alias.Length >= 4
            ? words.Any(w => w.StartsWith(alias, StringComparison.Ordinal))
            : words.Any(w => w == alias);
    }
}
