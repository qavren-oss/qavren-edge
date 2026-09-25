using System.Text;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Spec 6's one normalisation: CRLF and lone CR to LF, a leading BOM stripped, U+00A0, U+2007 and
/// U+202F folded to U+0020, NFC applied.
/// </summary>
/// <remarks>
/// <para>
/// It runs BEFORE any block offset is computed, because spec 6 says every offset indexes the
/// normalised buffer and an extractor that normalises after recording offsets has silently moved
/// all of them. With <see cref="ExtractionOptions.NormalizeText"/> off this class is not called at
/// all and offsets index the raw decode.
/// </para>
/// <para>
/// NFC CAVEAT, measured on this repo's settings: <c>Directory.Build.props</c> sets
/// <c>InvariantGlobalization=true</c> repo-wide, and in globalization-invariant mode
/// <see cref="string.Normalize(NormalizationForm)"/> is a NO-OP rather than a throw - it returns
/// the input unchanged and <c>IsNormalized</c> answers <see langword="true"/> for text that is not.
/// The call is still made, so a consuming application that leaves invariant mode off gets real NFC;
/// under invariant mode the line-ending and BOM rules are the whole of the normalisation. Nothing
/// downstream depends on composition having happened - the offset contract is about the buffer that
/// is actually returned.
/// </para>
/// </remarks>
internal static class TextNormalizer
{
    /// <summary>
    /// The identity for text that is already normalised; a new string otherwise. Never null.
    /// </summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return text;
        }

        var stripped = text[0] == '\uFEFF' ? text[1..] : text;
        var lineEndingsFixed = FoldSpaces(FixLineEndings(stripped));

        // See the NFC caveat in the remarks: a no-op under InvariantGlobalization, real elsewhere.
        return lineEndingsFixed.IsNormalized(NormalizationForm.FormC)
            ? lineEndingsFixed
            : lineEndingsFixed.Normalize(NormalizationForm.FormC);
    }

    // Issue #30: PostScript drivers map the font's space glyph to U+00A0, so every word
    // separator arrives non-breaking. One char for one char, so no offset moves.
    private static string FoldSpaces(string text) =>
        text.AsSpan().IndexOfAny(' ', ' ', ' ') < 0
            ? text
            : text.Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ');

    private static string FixLineEndings(string text)
    {
        if (text.IndexOf('\r') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r')
            {
                builder.Append(c);
                continue;
            }

            builder.Append('\n');
            if (i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }
        }

        return builder.ToString();
    }
}
