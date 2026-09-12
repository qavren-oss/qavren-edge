namespace Qavren.Edge.Ingestion;

/// <summary>
/// The token seam every chunker is budgeted against (spec 8.1). Declared in the core so no core
/// type names <c>Qavren.Edge.Embeddings.Onnx</c>; the ONNX satellite supplies its own
/// implementation.
/// </summary>
public interface IChunkTokenizer
{
    /// <summary>A stable identity for the vocabulary and model shape, e.g. <c>wordpiece:30522:minilm-l6-v2-int8</c>.</summary>
    string Id { get; }

    /// <summary>The model ceiling, special tokens INCLUDED.</summary>
    int MaxSequenceLength { get; }

    /// <summary>Tokens the encoder adds that <see cref="CountTokens"/> does not report.</summary>
    int SpecialTokenOverhead { get; }

    /// <summary>The WordPiece (or equivalent) cost of <paramref name="text"/>, specials EXCLUDED.</summary>
    int CountTokens(ReadOnlySpan<char> text);

    /// <summary>
    /// The largest index <c>i</c> such that <c>text[0..i)</c> costs at most
    /// <paramref name="maxTokens"/> tokens.
    /// CONTRACT: <c>i</c> indexes into <paramref name="text"/> AS PASSED — never into a normalised
    /// copy of it (spec 8.1, "The offset contract"). This is load-bearing: every committed golden
    /// offset is an index into <see cref="ExtractedDocument.Text"/>.
    /// </summary>
    int IndexByTokenCount(string text, int maxTokens, out int tokenCount);
}
