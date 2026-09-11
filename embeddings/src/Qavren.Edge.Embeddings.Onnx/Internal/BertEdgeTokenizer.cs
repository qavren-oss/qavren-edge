using System.Buffers;
using Microsoft.ML.Tokenizers;

namespace Qavren.Edge.Embeddings.Onnx.Internal;

/// <summary>
/// The WordPiece <see cref="IEdgeTokenizer"/>. It holds the CONCRETE <see cref="BertTokenizer"/>:
/// <c>BertTokenizer.EncodeToIds</c> is declared <c>new</c>, so a <c>Tokenizer</c>-typed field
/// would silently bind the base method and drop <c>[CLS]</c> and <c>[SEP]</c> from every encode.
/// </summary>
internal sealed class BertEdgeTokenizer : IEdgeTokenizer
{
    private readonly BertTokenizer _tokenizer;

    internal BertEdgeTokenizer(BertTokenizer tokenizer, int vocabularySize, int maxSequenceLength)
    {
        _tokenizer = tokenizer;
        VocabularySize = vocabularySize;
        MaxSequenceLength = maxSequenceLength;
    }

    /// <inheritdoc />
    public EdgeTokenizerKind Kind => EdgeTokenizerKind.WordPieceVocabTxt;

    /// <inheritdoc />
    public int VocabularySize { get; }

    /// <inheritdoc />
    public int MaxSequenceLength { get; }

    /// <inheritdoc />
    public int PadTokenId => _tokenizer.PaddingTokenId;

    /// <inheritdoc />
    public int Encode(ReadOnlySpan<char> text, int maxTokens, Span<int> destination, out int charsConsumed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxTokens);

        if (destination.Length < maxTokens)
        {
            throw new ArgumentException(
                $"The destination holds {destination.Length} ids but maxTokens is {maxTokens}.",
                nameof(destination));
        }

        // The truncating overload accounts for the two special tokens itself: it delegates with
        // maxTokenCount - 2 and returns empty when maxTokenCount < 2, so maxTokens: 256 yields at
        // most 256 ids INCLUDING [CLS] and [SEP].
        var ids = _tokenizer.EncodeToIds(text, maxTokens, out _, out charsConsumed);

        for (var i = 0; i < ids.Count; i++)
        {
            destination[i] = ids[i];
        }

        return ids.Count;
    }

    /// <inheritdoc />
    public TokenizedBatch EncodeBatch(IReadOnlyList<string> texts, int maxSequenceLength, IReadOnlyList<int> buckets)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSequenceLength);

        var cap = Math.Min(maxSequenceLength, MaxSequenceLength);
        var batchSize = texts.Count;
        var encoded = new int[batchSize][];
        var tokenCounts = new int[batchSize];
        var truncated = new bool[batchSize];

        var longest = 0;
        for (var b = 0; b < batchSize; b++)
        {
            var text = texts[b] ?? string.Empty;
            var ids = _tokenizer.EncodeToIds(text.AsSpan(), cap, out var normalized, out var charsConsumed);

            encoded[b] = [.. ids];
            tokenCounts[b] = ids.Count;

            // Truncation is "the encoder stopped before the end of the normalized text", and the
            // second clause keeps a text that legitimately encodes to nothing from being reported
            // as truncated.
            var normalizedLength = (normalized ?? text).Length;
            truncated[b] = charsConsumed < normalizedLength && ids.Count >= cap;

            longest = Math.Max(longest, ids.Count);
        }

        var sequenceLength = RoundUpToBucket(longest, buckets, cap);
        var total = batchSize * sequenceLength;

        // Spec 11: the three tensor buffers come from ArrayPool<long>.Shared. Rent returns an array
        // that is AT LEAST `total` long, so every write and every read is windowed to [0, total) -
        // TokenizedBatch.TensorLength is the length that means anything - and the window is cleared
        // first, because a rented array carries the previous renter's bytes.
        var pool = ArrayPool<long>.Shared;
        var inputIds = pool.Rent(total);
        var attentionMask = pool.Rent(total);
        var tokenTypeIds = pool.Rent(total);

        inputIds.AsSpan(0, total).Fill(PadTokenId);
        attentionMask.AsSpan(0, total).Clear();
        tokenTypeIds.AsSpan(0, total).Clear();

        for (var b = 0; b < batchSize; b++)
        {
            var row = b * sequenceLength;
            var ids = encoded[b];
            for (var t = 0; t < ids.Length; t++)
            {
                inputIds[row + t] = ids[t];
                attentionMask[row + t] = 1;
            }
        }

        return new TokenizedBatch(
            inputIds,
            attentionMask,
            tokenTypeIds,
            batchSize,
            sequenceLength,
            tokenCounts,
            truncated)
        {
            Pooled = true,
        };
    }

    /// <inheritdoc />
    public int CountTokens(ReadOnlySpan<char> text) => _tokenizer.CountTokens(text);

    /// <inheritdoc />
    public int IndexByTokenCount(string text, int maxTokens, out int tokenCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _tokenizer.GetIndexByTokenCount(text, maxTokens, out _, out tokenCount);
    }

    /// <summary>
    /// Nothing to release: <see cref="BertTokenizer"/> holds only managed state and is not
    /// <see cref="IDisposable"/>. The interface declares <see cref="IDisposable"/> because a
    /// Tokenizers 3.x SentencePiece implementation will own native state.
    /// </summary>
    public void Dispose()
    {
        // Intentionally empty. See the summary above.
    }

    private static int RoundUpToBucket(int longest, IReadOnlyList<int> buckets, int cap)
    {
        for (var i = 0; i < buckets.Count; i++)
        {
            var bucket = buckets[i];
            if (bucket >= longest && bucket <= cap)
            {
                return bucket;
            }
        }

        // No bucket fits: the batch maximum is above every bucket at or below the cap, so the cap
        // itself is the shape. Never zero - a zero-width tensor is not a legal ORT input.
        return Math.Max(1, cap);
    }
}
