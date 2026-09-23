using System.Globalization;
using Microsoft.ML.Tokenizers;
using Qavren.Edge.Ingestion.Internal;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// <see cref="IChunkTokenizer"/> over a <c>Microsoft.ML.Tokenizers</c> <see cref="Tokenizer"/>.
/// <see cref="IndexByTokenCount"/> returns an index into the string AS PASSED — the offset contract
/// spec 8.1 turns on — and it derives that index from <see cref="CountTokens"/> through
/// <c>Internal.TokenIndexSearch</c> rather than forwarding to
/// <c>Tokenizer.GetIndexByTokenCount</c>.
/// </summary>
/// <remarks>
/// Spec 8.1's bullet and ADR 0013's context paragraph still describe the forwarding call
/// (<c>considerNormalization: false</c>); both were written before the real 30,522-entry
/// vocabulary was measured. Neither overload of that call satisfies the offset contract: at
/// <c>considerNormalization: true</c> the index is into the NORMALISED string, which mislocates
/// every boundary after the first non-ASCII difference, and at <c>false</c> the shipped
/// <c>BertTokenizer</c> does not search at all — it hands back <c>text.Length</c> with
/// <c>tokenCount</c> 1 for every budget. ADR 0013's decision (one shared prefix search) is
/// unchanged; only its stated reason moves.
/// </remarks>
public sealed class MlChunkTokenizer : IChunkTokenizer
{
    private readonly Tokenizer _tokenizer;

    /// <summary>Wraps <paramref name="tokenizer"/> as an <see cref="IChunkTokenizer"/>.</summary>
    /// <param name="tokenizer">The underlying <c>Microsoft.ML.Tokenizers</c> tokenizer.</param>
    /// <param name="maxSequenceLength">The model ceiling, special tokens included.</param>
    /// <param name="specialTokenOverhead">Tokens the encoder adds that <paramref name="tokenizer"/>'s own count does not report.</param>
    /// <param name="id">A stable identity, or <see langword="null"/> to derive one from the tokenizer's type and <paramref name="maxSequenceLength"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tokenizer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSequenceLength"/> is not positive, or <paramref name="specialTokenOverhead"/> is negative.</exception>
    public MlChunkTokenizer(
        Tokenizer tokenizer, int maxSequenceLength, int specialTokenOverhead, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSequenceLength, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(specialTokenOverhead);

        _tokenizer = tokenizer;
        MaxSequenceLength = maxSequenceLength;
        SpecialTokenOverhead = specialTokenOverhead;
        Id = id ?? string.Format(
            CultureInfo.InvariantCulture,
            "ml:{0}:{1}",
            tokenizer.GetType().Name,
            maxSequenceLength);
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public int MaxSequenceLength { get; }

    /// <inheritdoc/>
    public int SpecialTokenOverhead { get; }

    /// <inheritdoc/>
    public int CountTokens(ReadOnlySpan<char> text) => _tokenizer.CountTokens(text);

    /// <inheritdoc/>
    public int IndexByTokenCount(string text, int maxTokens, out int tokenCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maxTokens);

        if (text.Length == 0 || maxTokens == 0)
        {
            tokenCount = 0;
            return 0;
        }

        // NOT a forward to Tokenizer.GetIndexByTokenCount. At considerNormalization: true that
        // call returns an index into the NORMALISED string, which mislocates every boundary after
        // the first non-ASCII difference; at false, the shipped BertTokenizer does not search at
        // all and hands back text.Length with tokenCount 1 for every budget (measured - see
        // Internal.TokenIndexSearch). The derivation from CountTokens is the one implementation.
        return TokenIndexSearch.Find(CountTokens, text, maxTokens, out tokenCount);
    }
}

/// <summary>The ONNX-free construction path for <see cref="IChunkTokenizer"/> (spec 8.1).</summary>
public static class EdgeTokenCounter
{
    /// <summary>WordPiece over a <c>vocab.txt</c>. bert-base-uncased shape; overhead 2.</summary>
    public static IChunkTokenizer CreateWordPiece(
        string vocabFilePath, int maxSequenceLength, bool lowerCase = true) =>
        new MlChunkTokenizer(
            BertTokenizer.Create(vocabFilePath, new BertOptions { LowerCaseBeforeTokenization = lowerCase }),
            maxSequenceLength,
            specialTokenOverhead: 2,
            id: WordPieceId(maxSequenceLength, lowerCase));

    /// <summary>WordPiece over a <c>vocab.txt</c> stream, for an embedded or asset-packed vocabulary.</summary>
    public static IChunkTokenizer CreateWordPiece(
        Stream vocabTxt, int maxSequenceLength, bool lowerCase = true) =>
        new MlChunkTokenizer(
            BertTokenizer.Create(vocabTxt, new BertOptions { LowerCaseBeforeTokenization = lowerCase }),
            maxSequenceLength,
            specialTokenOverhead: 2,
            id: WordPieceId(maxSequenceLength, lowerCase));

    /// <summary>Any <c>Microsoft.ML.Tokenizers</c> tokenizer, with the overhead stated by the caller.</summary>
    public static IChunkTokenizer FromTokenizer(
        Tokenizer tokenizer, int maxSequenceLength, int specialTokenOverhead) =>
        new MlChunkTokenizer(tokenizer, maxSequenceLength, specialTokenOverhead);

    private static string WordPieceId(int maxSequenceLength, bool lowerCase) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "wordpiece:{0}:{1}",
            maxSequenceLength,
            lowerCase ? "uncased" : "cased");
}
