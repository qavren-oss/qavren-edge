using Microsoft.ML.Tokenizers;
using Qavren.Edge.Embeddings.Onnx.Internal;

namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>Builds WordPiece tokenizers over a <c>vocab.txt</c>.</summary>
public static class EdgeTokenizer
{
    /// <summary>Builds a WordPiece tokenizer from a <c>vocab.txt</c> on disk.</summary>
    /// <param name="vocabFilePath">The absolute path of the vocabulary.</param>
    /// <param name="options">Construction settings, or null for the defaults.</param>
    /// <returns>The tokenizer.</returns>
    public static IEdgeTokenizer CreateWordPiece(string vocabFilePath, WordPieceTokenizerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabFilePath);

        using var stream = File.OpenRead(vocabFilePath);
        return CreateWordPiece(stream, options);
    }

    /// <summary>Builds a WordPiece tokenizer from a <c>vocab.txt</c> stream.</summary>
    /// <param name="vocabTxt">The vocabulary, one token per line.</param>
    /// <param name="options">Construction settings, or null for the defaults.</param>
    /// <returns>The tokenizer.</returns>
    public static IEdgeTokenizer CreateWordPiece(Stream vocabTxt, WordPieceTokenizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(vocabTxt);

        var settings = options ?? new WordPieceTokenizerOptions();
        var bytes = ReadAll(vocabTxt);

        // The vocabulary is read twice on purpose: once to count entries, because
        // Microsoft.ML.Tokenizers 2.0.0 exposes no vocabulary-size member on WordPieceTokenizer,
        // and once by BertTokenizer.Create. Both reads are over the same in-memory buffer.
        var vocabularySize = CountEntries(bytes);

        using var buffer = new MemoryStream(bytes, writable: false);
        var tokenizer = BertTokenizer.Create(buffer, ToBertOptions(settings));

        return new BertEdgeTokenizer(tokenizer, vocabularySize, settings.MaxSequenceLength);
    }

    /// <summary>Builds a WordPiece tokenizer from a <c>vocab.txt</c> on disk, asynchronously.</summary>
    /// <param name="vocabFilePath">The absolute path of the vocabulary.</param>
    /// <param name="options">Construction settings, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The tokenizer.</returns>
    public static async Task<IEdgeTokenizer> CreateWordPieceAsync(
        string vocabFilePath,
        WordPieceTokenizerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabFilePath);

        var settings = options ?? new WordPieceTokenizerOptions();
        var bytes = await File.ReadAllBytesAsync(vocabFilePath, cancellationToken).ConfigureAwait(false);
        var vocabularySize = CountEntries(bytes);

        using var buffer = new MemoryStream(bytes, writable: false);
        var tokenizer = await BertTokenizer
            .CreateAsync(buffer, ToBertOptions(settings), cancellationToken)
            .ConfigureAwait(false);

        return new BertEdgeTokenizer(tokenizer, vocabularySize, settings.MaxSequenceLength);
    }

    private static BertOptions ToBertOptions(WordPieceTokenizerOptions settings) => new()
    {
        LowerCaseBeforeTokenization = settings.LowerCase,
        UnknownToken = settings.UnknownToken,
        PaddingToken = settings.PaddingToken,
        ClassificationToken = settings.ClassificationToken,
        SeparatorToken = settings.SeparatorToken,
        RemoveNonSpacingMarks = settings.RemoveNonSpacingMarks,
    };

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var segment))
        {
            return segment.AsSpan().ToArray();
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static int CountEntries(ReadOnlySpan<byte> vocab)
    {
        var entries = 0;
        var pending = false;

        for (var i = 0; i < vocab.Length; i++)
        {
            if (vocab[i] == (byte)'\n')
            {
                entries++;
                pending = false;
            }
            else if (vocab[i] != (byte)'\r')
            {
                pending = true;
            }
        }

        // A final line without a trailing newline is still an entry.
        return pending ? entries + 1 : entries;
    }
}
