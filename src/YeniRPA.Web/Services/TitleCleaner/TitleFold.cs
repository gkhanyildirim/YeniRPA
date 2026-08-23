using System.Text;

namespace YeniRPA.Web.Services.TitleCleaner;

/// <summary>
/// A product title in comparison form, with a 1:1 map back to the original character positions.
///
/// <para><b>Why this exists rather than reusing the folds already in the app.</b>
/// <see cref="SellerGroupMap.FoldName"/> collapses whitespace and <see cref="CarrierNames.Fold"/>
/// additionally turns every punctuation mark into a space. Both are exactly right for their job —
/// comparing two whole names — and both destroy character positions, which is the one thing this
/// module cannot lose: it does not compare titles, it <em>cuts spans out of</em> them. Folding the
/// inch mark away and then removing the wrong five characters corrupts a catalogue silently.</para>
///
/// <para>The folding <em>rules</em> are taken from those two (the Turkish i-family and the accent
/// map), deliberately copied rather than shared — the same trade, and the same reasoning, as
/// <see cref="SellerGroupMap.NormalizeSellerId"/> keeping its own copy.</para>
/// </summary>
public sealed class FoldedTitle
{
    /// <summary>Original index each folded character came from.</summary>
    readonly int[] _start;

    /// <summary>Exclusive original end of what produced each folded character — normally one past
    /// <see cref="_start"/>, but wider where a dropped mark was absorbed into it.</summary>
    readonly int[] _end;

    FoldedTitle(string original, string folded, int[] start, int[] end)
    {
        Original = original;
        Folded = folded;
        _start = start;
        _end = end;
    }

    public string Original { get; }

    /// <summary>Lower-cased, Turkish letters folded to ASCII, quote marks normalised. Same length as
    /// <see cref="Original"/> unless the source carried combining marks.</summary>
    public string Folded { get; }

    /// <summary>
    /// Translates a span found on <see cref="Folded"/> back to the corresponding span of
    /// <see cref="Original"/>.
    /// </summary>
    public (int Start, int End) ToOriginal(int foldedStart, int foldedEnd)
    {
        if (foldedEnd <= foldedStart)
            return (0, 0);

        return (_start[foldedStart], _end[foldedEnd - 1]);
    }

    /// <summary>True when the original character before <paramref name="originalIndex"/> is a letter
    /// or digit — i.e. a span starting there would be cutting into the middle of a word.</summary>
    public bool AlphanumericBefore(int originalIndex) =>
        originalIndex > 0 && originalIndex <= Original.Length && char.IsLetterOrDigit(Original[originalIndex - 1]);

    /// <summary>True when the original character at <paramref name="originalIndex"/> is a letter or
    /// digit — i.e. a span ending there would be cutting into the middle of a word.</summary>
    public bool AlphanumericAt(int originalIndex) =>
        originalIndex >= 0 && originalIndex < Original.Length && char.IsLetterOrDigit(Original[originalIndex]);

    public static FoldedTitle Of(string? raw)
    {
        var original = raw ?? "";
        var folded = new StringBuilder(original.Length);
        var start = new int[original.Length];
        var end = new int[original.Length];

        for (var i = 0; i < original.Length; i++)
        {
            var mapped = FoldChar(original[i]);

            if (mapped is null)
            {
                // A dropped mark belongs to the character it decorates, so cutting that character
                // takes the mark with it instead of leaving it behind on its own.
                if (folded.Length > 0)
                    end[folded.Length - 1] = i + 1;
                continue;
            }

            start[folded.Length] = i;
            end[folded.Length] = i + 1;
            folded.Append(mapped.Value);
        }

        return new FoldedTitle(original, folded.ToString(), start, end);
    }

    /// <summary>Folds a short value into a comparison key with no position guarantees — for
    /// comparing a cell against a canonical spelling, never for locating spans.</summary>
    public static string Fold(string? raw) => Of(raw).Folded;

    /// <summary>
    /// One character's comparison form, or <c>null</c> when it carries no meaning of its own.
    ///
    /// <para>The i-family fold is the part that is not optional and not obtainable from any built-in
    /// comparison — the reasoning is written out in full on <see cref="SellerGroupMap.FoldName"/>.
    /// Titles carry "DIZUSTU", "Dizüstü" and "dizustu" for one word, and no <c>StringComparison</c>
    /// collides all three.</para>
    ///
    /// <para>Quote marks are normalised because the inch mark is a real unit here: a title pasted
    /// out of Word carries a curly U+201D where the attribute cell carries a straight U+0022, and an
    /// unfolded comparison reads those as two different screen sizes.</para>
    /// </summary>
    public static char? FoldChar(char ch) => ch switch
    {
        // U+0307 is the combining dot above that ICU leaves behind when it lowercases a dotted capital I.
        '̇' => null,

        'İ' or 'I' or 'ı' or 'i' => 'i',
        'Ç' or 'ç' => 'c',
        'Ş' or 'ş' => 's',
        'Ğ' or 'ğ' => 'g',
        'Ö' or 'ö' => 'o',
        'Ü' or 'ü' => 'u',
        'Â' or 'â' => 'a',
        'Î' or 'î' => 'i',
        'Û' or 'û' => 'u',

        // The inch mark as Word, Excel and the marketplace each write it.
        '“' or '”' or '″' or '＂' => '"',

        // The foot mark, and the apostrophe Turkish suffixes are written with.
        '‘' or '’' or '′' => '\'',

        // Non-breaking and narrow spaces survive copy-paste and would otherwise glue two tokens.
        ' ' or ' ' or ' ' => ' ',

        // Trademark marks decorate the word they follow and are never written in a title. The
        // marketplace's own attribute lists carry them throughout ("Intel®", "Core™ i7", "NVIDIA®"),
        // so without this an Intel row's brand cell simply never matches its title. Dropped rather
        // than mapped, exactly like the combining dot above: the mark is absorbed into the span of
        // the character it decorates, so cutting that character takes the mark with it.
        '™' or '®' or '©' or '℠' => null,

        _ => char.ToLowerInvariant(ch),
    };
}
