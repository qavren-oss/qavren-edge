namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// A backwards scan for the nearest cut a reader would recognise: a paragraph break, then a
/// sentence terminator, then whitespace (spec 8.2). It searches only a 15% look-back window and
/// returns "no boundary" rather than reaching further, so a cut is never dragged far enough to
/// halve a chunk.
/// </summary>
/// <remarks>
/// Culture-invariant throughout. The abbreviation stop-list is matched with
/// <see cref="StringComparison.Ordinal"/>: the dotted-I trap lives in any case-sensitive matcher
/// and <c>InvariantGlobalization</c> is on repo-wide, so an ordinal comparison is both the correct
/// and the only available answer.
/// </remarks>
internal static class SentenceBoundary
{
    private static readonly char[] Terminators = ['.', '!', '?', '\u2026', '\u3002', '\uFF01', '\uFF1F'];

    /// <summary>
    /// Abbreviations whose trailing dot is not a sentence end. Ordinal, and deliberately short:
    /// a long list costs a scan per candidate and buys nothing a 15% window would not.
    /// </summary>
    private static readonly string[] Abbreviations =
        ["Mr.", "Mrs.", "Ms.", "Dr.", "Prof.", "St.", "e.g.", "i.e.", "etc.", "vs.", "No.", "Fig."];

    /// <summary>
    /// The best cut in <c>(rangeStart, cut]</c> whose distance from <paramref name="cut"/> is at
    /// most <paramref name="lookBack"/>, or <c>-1</c> when the window holds none. The returned
    /// index is the EXCLUSIVE end of the chunk.
    /// </summary>
    internal static int FindBackwards(string text, int rangeStart, int cut, int lookBack)
    {
        var windowStart = Math.Max(rangeStart, cut - Math.Max(1, lookBack));
        if (cut <= windowStart)
        {
            return -1;
        }

        var paragraph = FindParagraphBreak(text, windowStart, cut);
        if (paragraph > rangeStart)
        {
            return paragraph;
        }

        var sentence = FindSentenceEnd(text, windowStart, cut);
        if (sentence > rangeStart)
        {
            return sentence;
        }

        var whitespace = FindWhitespace(text, windowStart, cut);
        return whitespace > rangeStart ? whitespace : -1;
    }

    /// <summary>The end of the last run of text before a blank line, or -1.</summary>
    private static int FindParagraphBreak(string text, int windowStart, int cut)
    {
        for (var i = cut - 1; i > windowStart; i--)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            // Walk the whole whitespace run this newline belongs to and count its newlines.
            var runEnd = i + 1;
            var runStart = i;
            while (runStart > windowStart && char.IsWhiteSpace(text[runStart - 1]))
            {
                runStart--;
            }

            var newlines = 0;
            for (var j = runStart; j < runEnd; j++)
            {
                if (text[j] == '\n')
                {
                    newlines++;
                }
            }

            if (newlines >= 2)
            {
                return runStart;
            }

            i = runStart;
        }

        return -1;
    }

    /// <summary>The index just past the last sentence terminator in the window, or -1.</summary>
    private static int FindSentenceEnd(string text, int windowStart, int cut)
    {
        for (var i = cut - 1; i >= windowStart; i--)
        {
            if (Array.IndexOf(Terminators, text[i]) < 0)
            {
                continue;
            }

            // A terminator only ends a sentence when whitespace or the text end follows it.
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                continue;
            }

            if (text[i] == '.' && IsAbbreviationDot(text, i))
            {
                continue;
            }

            return i + 1;
        }

        return -1;
    }

    /// <summary>The start of the last whitespace run in the window, or -1.</summary>
    private static int FindWhitespace(string text, int windowStart, int cut)
    {
        for (var i = cut - 1; i > windowStart; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            var runStart = i;
            while (runStart > windowStart && char.IsWhiteSpace(text[runStart - 1]))
            {
                runStart--;
            }

            return runStart;
        }

        return -1;
    }

    /// <summary>True when the dot at <paramref name="dot"/> closes a stop-list abbreviation or a single initial.</summary>
    private static bool IsAbbreviationDot(string text, int dot)
    {
        foreach (var abbreviation in Abbreviations)
        {
            var start = dot + 1 - abbreviation.Length;
            if (start >= 0 &&
                text.AsSpan(start, abbreviation.Length).Equals(abbreviation, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // A single capital initial - "J. R. R." - is an abbreviation, not a sentence end.
        return dot >= 1
            && char.IsAsciiLetterUpper(text[dot - 1])
            && (dot == 1 || !char.IsLetter(text[dot - 2]));
    }
}
