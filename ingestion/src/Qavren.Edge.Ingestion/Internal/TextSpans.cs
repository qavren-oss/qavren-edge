using System.Globalization;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Grapheme-cluster arithmetic for chunk cuts. Every boundary a chunker emits is snapped through
/// here, so no chunk ever ends mid-surrogate or mid-combining-mark (spec 8.2).
/// </summary>
/// <remarks>
/// Every walk starts from a KNOWN boundary — the chunk's own start — rather than from index 0, so
/// the whole pass over a document stays linear instead of quadratic.
/// </remarks>
internal static class TextSpans
{
    /// <summary>The first grapheme-cluster boundary at or after <paramref name="target"/>.</summary>
    internal static int NextBoundary(string text, int from, int target)
    {
        var (_, next) = Boundaries(text, from, target);
        return next;
    }

    /// <summary>The last grapheme-cluster boundary at or before <paramref name="target"/>.</summary>
    internal static int PreviousBoundary(string text, int from, int target)
    {
        var (previous, _) = Boundaries(text, from, target);
        return previous;
    }

    /// <summary>The boundary pair bracketing <paramref name="target"/>; equal when it IS a boundary.</summary>
    internal static (int Previous, int Next) Boundaries(string text, int from, int target)
    {
        if (target <= from)
        {
            return (from, from);
        }

        if (target >= text.Length)
        {
            return (text.Length, text.Length);
        }

        var cursor = from;
        while (cursor < target)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(cursor));
            if (length <= 0)
            {
                // Defensive: a zero-length element would spin forever.
                length = 1;
            }

            var next = cursor + length;
            if (next >= target)
            {
                return next == target ? (target, target) : (cursor, Math.Min(next, text.Length));
            }

            cursor = next;
        }

        return (cursor, cursor);
    }

    /// <summary>Every grapheme-cluster boundary in <c>[from, to]</c>, ascending, both ends included.</summary>
    internal static List<int> BoundaryList(string text, int from, int to)
    {
        var boundaries = new List<int>(Math.Max(4, (to - from) / 4)) { from };
        var cursor = from;
        while (cursor < to)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(cursor));
            if (length <= 0)
            {
                length = 1;
            }

            cursor = Math.Min(cursor + length, to);
            boundaries.Add(cursor);
        }

        if (boundaries[^1] != to)
        {
            boundaries.Add(to);
        }

        return boundaries;
    }

    /// <summary>True when <c>[start, end)</c> holds nothing but whitespace (or is empty).</summary>
    internal static bool IsWhiteSpace(string text, int start, int end)
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

    /// <summary>Trims <c>[start, end)</c> to its first and last non-whitespace characters.</summary>
    internal static (int Start, int End) Trim(string text, int start, int end)
    {
        var s = Math.Max(0, start);
        var e = Math.Min(text.Length, end);
        while (s < e && char.IsWhiteSpace(text[s]))
        {
            s++;
        }

        while (e > s && char.IsWhiteSpace(text[e - 1]))
        {
            e--;
        }

        return (s, e);
    }
}
