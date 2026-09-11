namespace Qavren.Edge.Rag;

/// <summary>
/// 960-999 is the RAG range, published from the package that logs it - <c>Qavren.Edge.Rag</c> does
/// not reference <c>Qavren.Edge.Chat.Onnx</c>, so a constant declared there would be unreachable
/// from the code that logs it.
/// </summary>
public static class EdgeRagEventIds
{
    /// <summary>A retrieval completed; carries the retriever name and the source count.</summary>
    public const int Retrieved = 960;

    /// <summary>The context block was assembled; carries the source count and the token estimate.</summary>
    public const int ContextAssembled = 961;

    /// <summary>The retriever threw. Contained under <c>RagOptions.ContinueOnRetrievalFailure</c>.</summary>
    public const int RetrievalFailed = 962;

    /// <summary>Retrieval returned nothing; the turn short-circuits without a model call.</summary>
    public const int NoContext = 963;

    /// <summary>Citations were resolved and attached to the final update.</summary>
    public const int CitationsAttached = 964;

    /// <summary>A <c>[n]</c> marker matched no source. Left as plain text, counted, never fabricated.</summary>
    public const int CitationUnresolved = 965;

    /// <summary>The extractive floor answered without a model.</summary>
    public const int ExtractiveAnswer = 966;
}
