using System.Globalization;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>One emitted window: a half-open char range into the document buffer and its cost.</summary>
internal readonly record struct TokenWindowSpan(int Start, int End, int TokenCount);

/// <summary>
/// The terminal windowing engine every chunker delegates to (spec 8.2). Forward cut via
/// <see cref="IChunkTokenizer.IndexByTokenCount"/>; the next window is seeded by counting
/// <c>OverlapTokens</c> back from the emitted chunk's end.
/// </summary>
/// <remarks>
/// Two loop-safety rules, because this is the one chunker that can fail to terminate: a cut that
/// does not advance past the previous chunk's start is widened to the next grapheme cluster, and
/// the emitted chunk's token count is RE-VERIFIED against <c>MaxTokens</c> before it is yielded.
/// </remarks>
internal static class TokenWindow
{
    /// <summary>Cover <c>[rangeStart, rangeEnd)</c> with overlapping windows, none over budget.</summary>
    internal static IEnumerable<TokenWindowSpan> Windows(
        string text,
        int rangeStart,
        int rangeEnd,
        ResolvedChunkOptions options,
        IChunkTokenizer tokenizer,
        string chunkerId)
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
                throw Over(chunkerId, tokens, options.MaxTokens, pos, cut);
            }

            if (TextSpans.IsWhiteSpace(text, pos, cut) && cut < end)
            {
                // Nothing but whitespace and there is more to come: fold it into the next window
                // rather than emitting a chunk no reader would keep (6154's shape, avoided).
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

    /// <summary>The exclusive end of the window starting at <paramref name="pos"/>.</summary>
    /// <remarks>
    /// The search's own cut is MEASURED within budget, and it is kept as the fallback, because
    /// neither of the two adjustments that follow it is monotone in token cost: WordPiece can
    /// tokenize a SHORTER prefix into MORE sub-words, so a backwards sentence nudge can cost more
    /// than the cut it replaced, and a forward grapheme snap adds characters outright.
    /// </remarks>
    private static int Cut(
        string text, int pos, int end, ResolvedChunkOptions options, IChunkTokenizer tokenizer)
    {
        var segment = text[pos..end];
        var index = tokenizer.IndexByTokenCount(segment, options.MaxTokens, out _);
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

        // Snap FORWARD to a grapheme-cluster boundary, so no chunk ends mid-surrogate or
        // mid-combining-mark. SentenceAware = false skips the nudge above and keeps this snap.
        cut = Math.Min(TextSpans.NextBoundary(text, pos, cut), end);

        if (cut <= pos)
        {
            // A cut that does not advance is widened to the next grapheme cluster.
            cut = Math.Min(end, TextSpans.NextBoundary(text, pos, pos + 1));
        }

        if (tokenizer.CountTokens(text.AsSpan(pos, cut - pos)) <= options.MaxTokens)
        {
            return cut;
        }

        // Over budget after adjustment: fall back to the measured cut, then walk back a cluster at
        // a time. Bounded, and it always terminates on the one-cluster window.
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

    /// <summary>
    /// Where the next window begins: the longest suffix of <c>[chunkStart, cut)</c> costing at most
    /// <paramref name="overlapTokens"/>, snapped to a word start so the shared region tokenizes the
    /// same way on both sides of the boundary.
    /// </summary>
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
            // The whole window costs less than the overlap: there is nothing to carry forward, and
            // repeating the window would not terminate.
            return cut;
        }

        return WordStart(text, next, cut, chunkStart);
    }

    /// <summary>The first index after the next whitespace run, so a shared region starts at a word.</summary>
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

    /// <summary>6151 — an emitted chunk over budget is an SP3 bug, never user data.</summary>
    internal static EdgeChunkingException Over(
        string chunkerId, int tokens, int maxTokens, int start, int end) =>
        new(
            EdgeErrorCode.ChunkExceedsTokenBudget,
            string.Format(
                CultureInfo.InvariantCulture,
                "Chunker '{0}' produced a chunk of {1} tokens over chars [{2}, {3}) against a frozen budget of {4}.",
                chunkerId,
                tokens,
                start,
                end,
                maxTokens))
        {
            ChunkerId = chunkerId,
            RequiredTokens = tokens,
            BudgetTokens = maxTokens,
            Remediation = "This is a bug in Qavren.Edge.Ingestion: report it with the document and the resolved budget.",
        };

    /// <summary>6154 — a chunker that emits nothing readable has lost the document.</summary>
    internal static EdgeChunkingException Empty(string chunkerId, int start, int end) =>
        new(
            EdgeErrorCode.ChunkerProducedEmptyChunk,
            string.Format(
                CultureInfo.InvariantCulture,
                "Chunker '{0}' produced an empty chunk over chars [{1}, {2}).",
                chunkerId,
                start,
                end))
        {
            ChunkerId = chunkerId,
            Remediation = "This is a bug in Qavren.Edge.Ingestion: report it with the document and the resolved budget.",
        };
}
