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
/// <c>2 log n</c> <c>CountTokens</c> calls per cut rather than an extra pass over the text. It
/// accepts a candidate ONLY when that prefix's measured cost is within budget, so the returned
/// index is always safe even though WordPiece prefix cost is not strictly monotone — a longer
/// prefix can tokenize into FEWER sub-words.
/// </para>
/// </remarks>
internal static class TokenIndexSearch
{
    /// <summary>A span-taking counter, so no normalised string can leak back through the seam.</summary>
    internal delegate int CountTokens(ReadOnlySpan<char> text);

    /// <summary>
    /// The largest grapheme-cluster boundary <c>i</c> this search can show costs at most
    /// <paramref name="maxTokens"/> tokens, with that cost in <paramref name="tokenCount"/>.
    /// </summary>
    internal static int Find(CountTokens count, string text, int maxTokens, out int tokenCount)
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

        // Exponential ramp: the largest power-of-two stride whose prefix still fits.
        var low = 0;
        var step = 1;
        while (low + step <= last && count(text.AsSpan(0, boundaries[low + step])) <= maxTokens)
        {
            low += step;
            step *= 2;
        }

        // Binary refine inside the bracket the ramp left.
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
}
