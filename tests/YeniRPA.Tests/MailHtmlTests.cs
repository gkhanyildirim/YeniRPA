using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Tests;

/// <summary>
/// The plain-text body on its way to becoming a signed HTML mail. Everything here decides what a
/// seller actually reads, and the body it works on is free text the operator typed into a box.
/// </summary>
public class MailHtmlTests
{
    /// <summary>
    /// The body comes back from the browser, where it is a box anything can be pasted into. An
    /// unescaped <c>&lt;</c> would swallow the rest of the message on the way to a seller.
    /// </summary>
    [Fact]
    public void MarkupInTheBodyIsEscapedRatherThanRendered()
    {
        var html = MailHtml.FromPlainText("Fiyat < 100 & stok > 0 <b>kalın</b>");

        Assert.Contains("&lt;b&gt;kalın&lt;/b&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.DoesNotContain("<b>", html);
    }

    /// <summary>The line breaks are the paragraphs. A blank line between two of them is its own break,
    /// which is what makes the mail read the way the preview did.</summary>
    [Fact]
    public void EveryLineBreakSurvivesIncludingTheBlankOnes()
    {
        var html = MailHtml.FromPlainText("Sayın yetkili,\n\nİlk paragraf.\nİkinci satır.");

        Assert.Contains("Sayın yetkili,<br><br>İlk paragraf.<br>İkinci satır.", html);
    }

    /// <summary>Windows line endings reach this from more than one path; both collapse to the same
    /// single break rather than doubling it.</summary>
    [Fact]
    public void CarriageReturnsDoNotDoubleTheBreaks()
    {
        Assert.Equal(
            MailHtml.FromPlainText("bir\niki"),
            MailHtml.FromPlainText("bir\r\niki"));
    }

    /// <summary>The mail is Turkish. Its letters have to arrive as letters, not as entities a reader
    /// has to decode.</summary>
    [Fact]
    public void TurkishLettersStayReadable()
    {
        var html = MailHtml.FromPlainText("Yeşilçe Mah. · Kağıthane / İstanbul · ı ş ğ ü ö ç");

        Assert.Contains("Yeşilçe Mah. · Kağıthane / İstanbul · ı ş ğ ü ö ç", html);
    }

    /// <summary>An unstyled HTML body renders as Times New Roman under a Calibri signature and looks
    /// like two mails stapled together, so the font is stated.</summary>
    [Fact]
    public void TheBodyCarriesItsOwnFont()
    {
        Assert.Contains("font-family:", MailHtml.FromPlainText("merhaba"));
    }

    // ---------------------------------------------------------------------
    // Placing the body against the signature
    // ---------------------------------------------------------------------

    /// <summary>
    /// What Outlook hands back is a whole document. The body belongs inside it, right after the
    /// opening tag — appending the two would leave our text outside the document it belongs to.
    /// </summary>
    [Fact]
    public void TheBodyGoesInsideTheSignaturesBodyTag()
    {
        const string signature =
            "<html><head><style>p {margin:0}</style></head><body lang=TR style='word-wrap:break-word'>" +
            "<p>Gökhan Yıldırım</p></body></html>";

        var merged = MailHtml.InsertBeforeSignature("<div>MERHABA</div>", signature);

        Assert.Contains("<body lang=TR style='word-wrap:break-word'><div>MERHABA</div><p>Gökhan", merged);
        Assert.EndsWith("</body></html>", merged);

        // And it leads: the body is read first, the signature closes the mail.
        Assert.True(merged.IndexOf("MERHABA", StringComparison.Ordinal)
                  < merged.IndexOf("Gökhan", StringComparison.Ordinal));
    }

    /// <summary>A <c>style</c> block before the body can hold the word "body" itself; the insertion
    /// point is the tag, not the first time the word appears.</summary>
    [Fact]
    public void AStyleRuleMentioningBodyIsNotMistakenForTheTag()
    {
        const string signature = "<html><head><style>body {font-size:11pt}</style></head><body><p>Sig</p></body></html>";

        var merged = MailHtml.InsertBeforeSignature("<div>B</div>", signature);

        Assert.Contains("<body><div>B</div><p>Sig</p>", merged);
        Assert.Contains("<style>body {font-size:11pt}</style>", merged);
    }

    /// <summary>No signature configured, or one that could not be read: the mail still has to be
    /// sendable, and the body is the whole of it.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNoSignatureTheBodyIsTheWholeMail(string? signature)
    {
        Assert.Equal("<div>B</div>", MailHtml.InsertBeforeSignature("<div>B</div>", signature));
    }

    /// <summary>An unexpected fragment with no document around it still produces a mail with the body
    /// first.</summary>
    [Fact]
    public void AFragmentWithNoBodyTagStillPutsTheBodyFirst()
    {
        Assert.Equal("<div>B</div><p>Sig</p>", MailHtml.InsertBeforeSignature("<div>B</div>", "<p>Sig</p>"));
    }
}
