namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// <see cref="IChunkTokenizer.IndexByTokenCount"/> over a range of a string instead of a string,
/// which is the form the window engine actually needs.
/// </summary>
/// <remarks>
/// The public member takes a <see cref="string"/>, so cutting window after window out of one long
/// block meant handing it a fresh copy of the rest of that block for every window. Both shipped
/// tokenizers implement this as well and are asked through it. It is internal and optional: a
/// tokenizer without it — every consumer-written one — is asked through the public member exactly
/// as before, copy included.
/// </remarks>
internal interface IRangeTokenIndex
{
    /// <summary>
    /// What <c>IndexByTokenCount(text[start..end], maxTokens, out tokenCount)</c> returns — an
    /// offset from <paramref name="start"/> — without materialising the substring.
    /// </summary>
    int IndexByTokenCount(string text, int start, int end, int maxTokens, out int tokenCount);
}
