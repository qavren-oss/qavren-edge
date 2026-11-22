namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// The blank-line paragraph rule: paragraphs are runs of non-blank lines, and a block's
/// <c>[Start, End)</c> runs from the first to just past the last non-whitespace character of its
/// run, so the separating newlines and any edge whitespace belong to no block.
/// </summary>
/// <remarks>
/// <b>This file is compiled into two assemblies.</b> The core owns it for
/// <c>PlainTextExtractor</c>, and the PDF package links it to split each page, so a .txt and a .pdf
/// carrying the same text get the same block boundaries - and with them the same chunk boundaries,
/// content hashes and vector reuse. It must therefore depend on nothing but the core's public types.
/// </remarks>
internal static class ParagraphSplitter
{
    /// <summary>Appends one <see cref="DocumentBlockKind.Paragraph"/> block per run of non-blank lines.</summary>
    /// <param name="text">The text to split.</param>
    /// <param name="baseOffset">Where <paramref name="text"/> starts in the document's buffer; added to every offset.</param>
    /// <param name="pageNumber">The page every block is stamped with, or null.</param>
    /// <param name="blocks">The list the blocks are appended to.</param>
    public static void Split(string text, int baseOffset, int? pageNumber, List<DocumentBlock> blocks)
    {
        var start = -1;
        var end = -1;
        var i = 0;

        while (i <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', i);
            var hasNewline = lineEnd >= 0;
            var stop = hasNewline ? lineEnd : text.Length;

            if (i >= text.Length && !hasNewline && i > 0)
            {
                break;
            }

            if (IsBlank(text, i, stop))
            {
                if (start >= 0)
                {
                    blocks.Add(Paragraph(baseOffset + start, baseOffset + end, pageNumber));
                    start = -1;
                }
            }
            else
            {
                if (start < 0)
                {
                    start = FirstNonWhitespace(text, i, stop);
                }

                end = LastNonWhitespaceExclusive(text, i, stop);
            }

            if (!hasNewline)
            {
                break;
            }

            i = lineEnd + 1;
        }

        if (start >= 0)
        {
            blocks.Add(Paragraph(baseOffset + start, baseOffset + end, pageNumber));
        }
    }

    private static DocumentBlock Paragraph(int start, int end, int? pageNumber) =>
        new(DocumentBlockKind.Paragraph, start, end, HeadingLevel: null, PageNumber: pageNumber);

    private static bool IsBlank(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int FirstNonWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return start;
    }

    private static int LastNonWhitespaceExclusive(string text, int start, int end)
    {
        for (var i = end - 1; i >= start; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return i + 1;
            }
        }

        return end;
    }
}
