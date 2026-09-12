using System.Globalization;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Ingestion.Internal;

namespace Qavren.Edge.Ingestion.Onnx;

/// <summary>
/// <see cref="IChunkTokenizer"/> over SP2's <see cref="IEdgeTokenizer"/> (spec 8.1, 11.2). It holds
/// ONLY the <see cref="IEdgeTokenizer"/>: <see cref="CountTokens"/> forwards to
/// <see cref="IEdgeTokenizer.CountTokens"/>, which SP2 forwards verbatim to
/// <c>Tokenizer.CountTokens</c> at its defaults and which takes a span rather than returning an
/// index, so no normalised string can leak through it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IndexByTokenCount"/> does <b>not</b> forward to
/// <see cref="IEdgeTokenizer.IndexByTokenCount"/>. That member forwards
/// <c>Tokenizer.GetIndexByTokenCount</c> with normalisation left on and discards the normalised
/// string, so its index is into a copy SP2 does not need and SP3 cannot use - the exact bug spec
/// 8.1's offset contract exists to prevent. The index is derived here from
/// <see cref="CountTokens"/> through <c>Internal.TokenIndexSearch</c>, the ONE implementation both
/// tokenizers share (plan adjustment 1, ADR 0013), reached through the core's
/// <c>InternalsVisibleTo</c> grant (plan adjustment 24).
/// </para>
/// <para>
/// <see cref="SpecialTokenOverhead"/> is <b>2</b>, measured on 2026-09-11 against the shipped
/// <c>Microsoft.ML.Tokenizers</c> 2.0.0 <c>BertTokenizer</c> (plan "Environment ground truth",
/// spec 17 item 2): <c>"hello world"</c> counts 2 and encodes to 4 ids, <c>"the quick brown
/// fox"</c> counts 4 and encodes to 6, <c>"a"</c> counts 1 and encodes to 3 - <c>[CLS]</c> and
/// <c>[SEP]</c> on every sequence, which <c>CountTokens</c> does not report.
/// </para>
/// <para>
/// <see cref="Id"/> is spelled exactly as <c>EdgeTokenCounter.CreateWordPiece</c> spells its own -
/// <c>wordpiece:{MaxSequenceLength}:{uncased|cased}</c> - on purpose. The id is a recipe input, and
/// the ONNX-free path and this bridge are the same vocabulary at the same settings; a consumer
/// who swaps one wiring for the other must not re-index a corpus for a spelling difference.
/// </para>
/// </remarks>
public sealed class EdgeChunkTokenizer : IChunkTokenizer
{
    /// <summary>The measured <c>[CLS]</c> + <c>[SEP]</c> overhead of a WordPiece encoder.</summary>
    private const int WordPieceOverhead = 2;

    private readonly IEdgeTokenizer _tokenizer;

    /// <summary>Wraps a built SP2 tokenizer.</summary>
    /// <param name="tokenizer">The tokenizer, already provisioned.</param>
    /// <param name="lowerCase">
    /// <c>EmbeddingPreset.LowerCase</c> for the preset this tokenizer was built for. It is part of
    /// the <see cref="Id"/> only; <see cref="IEdgeTokenizer"/> does not expose it.
    /// </param>
    public EdgeChunkTokenizer(IEdgeTokenizer tokenizer, bool lowerCase = true)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tokenizer.MaxSequenceLength, 0);

        _tokenizer = tokenizer;
        Id = string.Format(
            CultureInfo.InvariantCulture,
            "wordpiece:{0}:{1}",
            tokenizer.MaxSequenceLength,
            lowerCase ? "uncased" : "cased");
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public int MaxSequenceLength => _tokenizer.MaxSequenceLength;

    /// <inheritdoc />
    public int SpecialTokenOverhead => WordPieceOverhead;

    /// <inheritdoc />
    public int CountTokens(ReadOnlySpan<char> text) => _tokenizer.CountTokens(text);

    /// <inheritdoc />
    public int IndexByTokenCount(string text, int maxTokens, out int tokenCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maxTokens);

        if (text.Length == 0 || maxTokens == 0)
        {
            tokenCount = 0;
            return 0;
        }

        // NOT a forward to IEdgeTokenizer.IndexByTokenCount - see the type remarks.
        return TokenIndexSearch.Find(CountTokens, text, maxTokens, out tokenCount);
    }
}
