using Qavren.Edge.Ingestion.Internal;

namespace Qavren.Edge.Ingestion.Tests.Chunking;

/// <summary>
/// The window engine and its prefix search as 1.0.0 shipped them, kept as the ORACLE for
/// <see cref="TokenWindowScalingTests"/>. That version counted the whole remainder of a block and
/// listed every grapheme boundary in it before each cut, which is correct and quadratic; the
/// product code now reaches the same cuts without either pass, and "the same cuts" is a claim only
/// a copy of the original can check.
/// </summary>
/// <remarks>
/// Do not tidy this file towards the product code. Its value is that it does NOT change when
/// <see cref="TokenIndexSearch"/> and <see cref="TokenWindow"/> do.
/// </remarks>
internal static class LegacyTokenWindow
{
    /// <summary>1.0.0's <c>TokenIndexSearch.Find</c>, verbatim.</summary>
    internal static int Find(TokenIndexSearch.CountTokens count, string text, int maxTokens, out int tokenCount)
    {
        tokenCount = 0;
        if (text.Length == 0 || maxTokens <= 0)
        {
            return 0;
        }

        var total = count(text.AsSpan());
        if (total <= maxTokens)
        {
            tokenCount = total;
            return text.Length;
        }

        var boundaries = TextSpans.BoundaryList(text, 0, text.Length);
        var last = boundaries.Count - 1;

        var low = 0;
        var step = 1;
        while (low + step <= last && count(text.AsSpan(0, boundaries[low + step])) <= maxTokens)
        {
            low += step;
            step *= 2;
        }

        var high = Math.Min(low + step, last);
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (count(text.AsSpan(0, boundaries[mid])) <= maxTokens)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        var index = boundaries[low];
        tokenCount = index == 0 ? 0 : count(text.AsSpan(0, index));
        return index;
    }

    /// <summary>1.0.0's <c>TokenWindow.Windows</c>, verbatim but for the search it calls.</summary>
    internal static IEnumerable<TokenWindowSpan> Windows(
        string text, int rangeStart, int rangeEnd, ResolvedChunkOptions options, IChunkTokenizer tokenizer)
    {
        var (start, end) = TextSpans.Trim(text, rangeStart, rangeEnd);
        if (start >= end)
        {
            yield break;
        }

        var pos = start;
        while (pos < end)
        {
            var cut = Cut(text, pos, end, options, tokenizer);
            var tokens = tokenizer.CountTokens(text.AsSpan(pos, cut - pos));

            if (tokens > options.MaxTokens)
            {
                throw TokenWindow.Over("legacy", tokens, options.MaxTokens, pos, cut);
            }

            if (TextSpans.IsWhiteSpace(text, pos, cut) && cut < end)
            {
                pos = cut;
                continue;
            }

            if (!TextSpans.IsWhiteSpace(text, pos, cut))
            {
                yield return new TokenWindowSpan(pos, cut, tokens);
            }

            if (cut >= end)
            {
                yield break;
            }

            var next = OverlapStart(text, pos, cut, options.OverlapTokens, tokenizer);
            pos = next > pos ? next : cut;
        }
    }

    private static int Cut(
        string text, int pos, int end, ResolvedChunkOptions options, IChunkTokenizer tokenizer)
    {
        // Both shipped tokenizers' IndexByTokenCount was this call and nothing else.
        var segment = text[pos..end];
        var index = Find(tokenizer.CountTokens, segment, options.MaxTokens, out _);
        if (index >= segment.Length)
        {
            return end;
        }

        var measured = pos + Math.Max(index, 0);
        var cut = measured;

        if (options.SentenceAware)
        {
            var lookBack = Math.Max(1, (cut - pos) * 15 / 100);
            var nudged = SentenceBoundary.FindBackwards(text, pos, cut, lookBack);
            if (nudged > pos && nudged <= cut)
            {
                cut = nudged;
            }
        }

        cut = Math.Min(TextSpans.NextBoundary(text, pos, cut), end);

        if (cut <= pos)
        {
            cut = Math.Min(end, TextSpans.NextBoundary(text, pos, pos + 1));
        }

        if (tokenizer.CountTokens(text.AsSpan(pos, cut - pos)) <= options.MaxTokens)
        {
            return cut;
        }

        cut = measured > pos ? measured : Math.Min(end, TextSpans.NextBoundary(text, pos, pos + 1));
        while (cut > pos && tokenizer.CountTokens(text.AsSpan(pos, cut - pos)) > options.MaxTokens)
        {
            var back = TextSpans.PreviousBoundary(text, pos, cut - 1);
            if (back <= pos)
            {
                return Math.Min(end, TextSpans.NextBoundary(text, pos, pos + 1));
            }

            cut = back;
        }

        return cut;
    }

    private static int OverlapStart(
        string text, int chunkStart, int cut, int overlapTokens, IChunkTokenizer tokenizer)
    {
        if (overlapTokens <= 0)
        {
            return cut;
        }

        var boundaries = TextSpans.BoundaryList(text, chunkStart, cut);
        var lo = 0;
        var hi = boundaries.Count - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var candidate = boundaries[mid];
            if (tokenizer.CountTokens(text.AsSpan(candidate, cut - candidate)) <= overlapTokens)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        var next = boundaries[lo];
        if (next <= chunkStart)
        {
            return cut;
        }

        return WordStart(text, next, cut, chunkStart);
    }

    private static int WordStart(string text, int from, int limit, int chunkStart)
    {
        if (from <= chunkStart || from >= limit || char.IsWhiteSpace(text[from - 1]))
        {
            return from;
        }

        var i = from;
        while (i < limit && !char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        while (i < limit && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i < limit ? i : from;
    }
}
