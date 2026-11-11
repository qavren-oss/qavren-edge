using System.Globalization;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// The ONE implementation of <see cref="IChunkTokenizer.IndexByTokenCount"/> (plan adjustment 1,
/// ADR 0013): a bounded prefix search derived from <c>CountTokens</c> alone, with every candidate
/// snapped to a grapheme-cluster boundary, so the returned index is an index into the string AS
/// PASSED and no normalised copy can leak through it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured on this box, 2026-09-11, and it closes spec 17 item 11 the other way.</b> Spec 8.1
/// states that <c>MlChunkTokenizer</c> can forward to
/// <c>Tokenizer.GetIndexByTokenCount(text, maxTokens, out _, out count, considerNormalization: false)</c>.
/// Against the shipped <c>Microsoft.ML.Tokenizers</c> 2.0.0 <c>BertTokenizer</c> over the real
/// 30,522-entry <c>bert-base-uncased</c> vocabulary, that call returns <c>text.Length</c> with
/// <c>tokenCount = 1</c> for EVERY budget — it does not search at all once normalisation is off:
/// </para>
/// <code>
/// text = 103 chars, CountTokens = 21
/// maxTokens=3   considerNormalization:false -> index=103 tokenCount=1   (true -> index=16 tokenCount=3)
/// maxTokens=5   considerNormalization:false -> index=103 tokenCount=1   (true -> index=26 tokenCount=5)
/// maxTokens=10  considerNormalization:false -> index=103 tokenCount=1   (true -> index=48 tokenCount=10)
/// </code>
/// <para>
/// So the ONNX-free path cannot forward either: <c>considerNormalization: true</c> returns an index
/// into the normalised string and breaks the offset contract, and <c>false</c> returns no index at
/// all. Both implementations therefore derive the index here, which is exactly the shape ADR 0013
/// chose for the ONNX satellite — one shared prefix search — now applied to both.
/// </para>
/// <para>
/// The search is exponential-then-binary over grapheme-cluster boundaries, roughly
/// <c>2 log n</c> <c>CountTokens</c> calls per cut. It accepts a candidate ONLY when that prefix's
/// measured cost is within budget, so the returned index is always safe even though WordPiece
/// prefix cost is not strictly monotone — a longer prefix can tokenize into FEWER sub-words.
/// </para>
/// <para>
/// <b>It reads only as far as the cut it is looking for.</b> It used to open by counting the whole
/// text and listing every boundary in it, which is one pass over the rest of a block for every
/// window cut out of that block: a megabyte paragraph cost a megabyte per window. The boundaries
/// are now walked on demand, and the one thing the whole-text count decided — "does all of it
/// fit?" — is settled without it wherever it can be (see <c>RestExceeds</c>). Every probe the old
/// search made between those two steps is made here unchanged, so the index it returns is the
/// same one.
/// </para>
/// </remarks>
internal static class TokenIndexSearch
{
    /// <summary>
    /// The one character every tokenizer this search serves splits words on: U+0020, and nothing
    /// else that looks like whitespace. Measured against the shipped <c>BertTokenizer</c> on
    /// 2026-11-11: its normaliser DROPS control characters, line feed, carriage return and tab
    /// among them, so sixty <c>q</c>s, a newline and sixty more are ONE 120-character word costing
    /// a single <c>[UNK]</c>, where either half alone costs 60 tokens. A prefix that ends at a
    /// newline therefore proves nothing about the text it is a prefix of.
    /// </summary>
    private const char WordBreak = ' ';

    /// <summary>A span-taking counter, so no normalised string can leak back through the seam.</summary>
    internal delegate int CountTokens(ReadOnlySpan<char> text);

    /// <summary>
    /// The largest grapheme-cluster boundary <c>i</c> this search can show costs at most
    /// <paramref name="maxTokens"/> tokens, with that cost in <paramref name="tokenCount"/>.
    /// </summary>
    internal static int Find(CountTokens count, string text, int maxTokens, out int tokenCount) =>
        Find(count, text, 0, text.Length, maxTokens, out tokenCount);

    /// <summary>
    /// <see cref="Find(CountTokens, string, int, out int)"/> over <c>text[start..end)</c> in place:
    /// the answer the whole-string form gives for that substring, as an offset from
    /// <paramref name="start"/>, without the copy.
    /// </summary>
    internal static int Find(
        CountTokens count, string text, int start, int end, int maxTokens, out int tokenCount)
    {
        tokenCount = 0;
        var length = end - start;
        if (length <= 0 || maxTokens <= 0)
        {
            return 0;
        }

        var range = text.AsSpan(start, length);
        var boundaries = new BoundaryWalk(text, start, end);

        // Exponential ramp: the largest power-of-two stride whose prefix still fits.
        var low = 0;
        var step = 1;
        var overBudget = false;
        while (boundaries.Reaches(low + step))
        {
            if (count(range[..boundaries[low + step]]) > maxTokens)
            {
                overBudget = true;
                break;
            }

            low += step;
            step *= 2;
        }

        int high;
        if (overBudget)
        {
            // A prefix is over budget, which is not yet proof the whole range is: a prefix that
            // stops inside a word can cost more than the text it is a prefix of.
            if (!RestExceeds(count, range, boundaries[low + step], maxTokens, out var whole))
            {
                tokenCount = whole;
                return length;
            }

            high = low + step;
        }
        else
        {
            // The ramp ran off the end with every probe in budget, so the range is at most about
            // twice a prefix that fits and counting all of it is cheap.
            var total = count(range);
            if (total <= maxTokens)
            {
                tokenCount = total;
                return length;
            }

            high = boundaries.Last;
        }

        // Binary refine inside the bracket the ramp left.
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (count(range[..boundaries[mid]]) <= maxTokens)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        var index = boundaries[low];
        tokenCount = index == 0 ? 0 : count(range[..index]);
        return index;
    }

    /// <summary>
    /// Whether the whole of <paramref name="range"/> costs more than <paramref name="maxTokens"/>,
    /// given that its first <paramref name="failed"/> characters do. False means it fits, with its
    /// cost in <paramref name="total"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The certificate is a prefix that is over budget and is FOLLOWED BY A SPACE. Every tokenizer
    /// behind this search tokenizes space-separated words independently, so text added after such
    /// a prefix can only add tokens, and the whole range is over budget too. That is not true of a
    /// prefix that stops mid-word — the first hundred characters of a long unbroken token can cost
    /// a hundred sub-words where the whole of it costs one <c>[UNK]</c> — which is why the failed
    /// probe is not taken at its word and the search moves on to the next space instead.
    /// </para>
    /// <para>
    /// Close to the end of the range the question is answered the way it always was, by counting
    /// all of it: once what is left is no longer than what has been read the count is cheap, and
    /// it keeps the answer exact for a tokenizer whose pieces straddle a space by a token or two.
    /// A range with no space in it at all is counted whole for the same reason, so megabytes of
    /// text without one - minified JSON, a CSV of bare numbers - still cost a pass per window.
    /// </para>
    /// </remarks>
    private static bool RestExceeds(
        CountTokens count, ReadOnlySpan<char> range, int failed, int maxTokens, out int total)
    {
        total = 0;
        var from = failed;
        while (true)
        {
            var offset = range[from..].IndexOf(WordBreak);
            var at = offset < 0 ? range.Length : from + offset;

            if (range.Length - at <= at)
            {
                total = count(range);
                return total > maxTokens;
            }

            if (at == failed || count(range[..at]) > maxTokens)
            {
                return true;
            }

            // A prefix of whole words fits although a shorter one did not. Look twice as far, so
            // the looking stays proportional to where it ends.
            from = at * 2;
        }
    }

    /// <summary>
    /// The grapheme-cluster boundaries of <c>text[start..end)</c> as offsets from <c>start</c>,
    /// first one 0, walked only as far as the search has asked. Element for element what
    /// <see cref="TextSpans.BoundaryList"/> lists for the substring.
    /// </summary>
    private sealed class BoundaryWalk(string text, int start, int end)
    {
        private readonly List<int> _offsets = [0];
        private int _walked;

        /// <summary>The offset of a boundary already walked to.</summary>
        public int this[int index] => _offsets[index];

        /// <summary>The index of the final boundary. Walks whatever is left of the range.</summary>
        public int Last
        {
            get
            {
                while (Advance())
                {
                }

                return _offsets.Count - 1;
            }
        }

        /// <summary>True when the range has a boundary numbered <paramref name="index"/>.</summary>
        public bool Reaches(int index)
        {
            while (_offsets.Count <= index && Advance())
            {
            }

            return index < _offsets.Count;
        }

        private bool Advance()
        {
            var remaining = end - start - _walked;
            if (remaining <= 0)
            {
                return false;
            }

            var length = StringInfo.GetNextTextElementLength(text.AsSpan(start + _walked, remaining));
            if (length <= 0)
            {
                // Defensive: a zero-length element would spin forever.
                length = 1;
            }

            _walked += Math.Min(length, remaining);
            _offsets.Add(_walked);
            return true;
        }
    }
}
