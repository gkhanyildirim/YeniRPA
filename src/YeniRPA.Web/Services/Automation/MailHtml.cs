using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace YeniRPA.Web.Services.Automation;

/// <summary>
/// Turns the operator's plain-text body into the HTML a signed mail needs, and puts it in front of the
/// signature Outlook produced.
///
/// <para>Separate from <see cref="OutlookMailSender"/> and free of any platform attribute: none of this
/// touches COM, and keeping it here is what lets the rules below be tested without Outlook — they are
/// the rules that decide what a seller actually reads.</para>
/// </summary>
public static class MailHtml
{
    /// <summary>
    /// The font the body is rendered in. Stated rather than left to Outlook: an unstyled HTML body
    /// renders as Times New Roman, which would sit under a Calibri signature and look like two
    /// different mails stapled together.
    /// </summary>
    const string BodyStyle = "font-family:Calibri,'Segoe UI',Arial,sans-serif;font-size:11pt";

    /// <summary>
    /// Escapes what HTML needs escaped and nothing else. The default encoder also turns every
    /// non-ASCII character into a numeric entity, which would render this Turkish mail correctly but
    /// leave "Kağıthane" as <c>Ka&amp;#287;&amp;#305;thane</c> in the source — unreadable in a draft the
    /// operator is checking, and needless: the string reaches Outlook as UTF-16 either way.
    /// </summary>
    static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    /// <summary>
    /// The plain-text body as HTML, escaped and with its line breaks kept.
    ///
    /// <para>Escaping is not optional. The body comes back from the browser, where it is a free-text
    /// box the operator can paste anything into; a single <c>&lt;</c> in it would otherwise swallow the
    /// rest of the message on the way to a seller. Turkish letters are left as letters — see
    /// <see cref="Encoder"/>.</para>
    ///
    /// <para>The text arrives already normalised to <c>\n</c> and trimmed (see the send endpoint), so
    /// one replacement covers every break — and every blank line the operator left between paragraphs
    /// survives as its own <c>&lt;br&gt;</c>, which is what makes the mail read the way the preview
    /// did.</para>
    /// </summary>
    public static string FromPlainText(string? body)
    {
        var text = (body ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

        // Split before encoding, not after. The encoder escapes a newline to &#xA; like any other
        // control character, so a replacement run afterwards would find nothing left to replace and
        // the whole mail would arrive as one unbroken paragraph.
        var lines = text.Split('\n').Select(Encoder.Encode);

        return $"<div style=\"{BodyStyle}\">{string.Join("<br>", lines)}</div>";
    }

    /// <summary>
    /// Puts the body in front of the signature.
    ///
    /// <para>What Outlook hands back is a whole document — <c>&lt;html&gt;&lt;body&gt;…&lt;/body&gt;
    /// &lt;/html&gt;</c> — so the body cannot simply be concatenated onto it: that would leave our text
    /// outside the document it belongs to, and mail clients render that inconsistently. It is inserted
    /// immediately after the opening <c>&lt;body&gt;</c> tag instead, which is where a person typing in
    /// Outlook would have put it.</para>
    ///
    /// <para>A signature that is empty, or an unexpected fragment with no <c>&lt;body&gt;</c> at all,
    /// still has to produce a sendable mail: the body simply leads.</para>
    /// </summary>
    public static string InsertBeforeSignature(string bodyHtml, string? signatureHtml)
    {
        var signature = signatureHtml ?? "";
        if (signature.Trim().Length == 0)
            return bodyHtml;

        var open = signature.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (open >= 0)
        {
            // The tag can carry attributes (Outlook writes a lang and a style), so the insertion point
            // is the end of the tag, not the end of the word "<body".
            var close = signature.IndexOf('>', open);
            if (close >= 0)
                return signature[..(close + 1)] + bodyHtml + signature[(close + 1)..];
        }

        return bodyHtml + signature;
    }
}
