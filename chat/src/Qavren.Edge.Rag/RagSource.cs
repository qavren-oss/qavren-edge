namespace Qavren.Edge.Rag;

/// <summary>
/// Which direction a retriever's score runs.
/// </summary>
/// <remarks>
/// <b>Load-bearing.</b> MEVD's <c>SearchAsync</c> over sub-project 2's store returns a DISTANCE
/// (lower is better, threshold pushed into SQL as <c>distance &lt;=</c>) while
/// <c>HybridSearchAsync</c> returns the RRF score (higher is better, threshold applied client-side
/// as <c>&gt;=</c>). Mixing them silently inverts every ranking, which is why the kind rides on the
/// source rather than being inferred.
/// </remarks>
public enum RetrievalScoreKind
{
    /// <summary>Higher is better. The hybrid lane's RRF score.</summary>
    Relevance,

    /// <summary>Lower is better. The vector lane's distance.</summary>
    Distance,
}

/// <summary>One retrieved chunk, on its way to a numbered slot in the assembled context block.</summary>
public sealed record RagSource
{
    /// <summary>Creates a source from the two fields a projector must always supply.</summary>
    public RagSource(string id, string text)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(text);

        Id = id;
        Text = text;
    }

    /// <summary>Stable identifier - a chunk id, a row key. Becomes <c>CitationAnnotation.FileId</c>.</summary>
    public string Id { get; init; }

    /// <summary>The chunk body the model is shown, before <c>RagPrompts.Format</c> clamps it.</summary>
    public string Text { get; init; }

    /// <summary>Optional human-readable title, rendered as the block's <c>Title:</c> line.</summary>
    public string? Title { get; init; }

    /// <summary>Optional origin, rendered as the block's <c>Source:</c> line.</summary>
    public Uri? Uri { get; init; }

    /// <summary>
    /// Set by the retriever, never by a projector. <c>VectorStoreRetriever&lt;TKey,TRecord&gt;</c>
    /// stamps it from the MEVD result after projection.
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Set by the retriever from the search lane it actually took, never by a projector - a
    /// projector cannot know which lane ran, and guessing inverts the ranking.
    /// </summary>
    public RetrievalScoreKind ScoreKind { get; init; }

    /// <summary>
    /// 1-based position in the assembled block. What the model is told to cite as <c>[n]</c>.
    /// Assigned by <see cref="RagPrompts.Format(IReadOnlyList{RagSource}, RagOptions)"/> after
    /// ranking and clamping; 0 until then.
    /// </summary>
    public int Ordinal { get; init; }

    /// <summary>The record the projector was handed, for a consumer that wants the typed row back.</summary>
    public object? Record { get; init; }
}

/// <summary>What the middleware asks a retriever for.</summary>
public sealed class RetrievalRequest
{
    /// <summary>How many sources to return. Default 5.</summary>
    public int Top { get; set; } = 5;

    /// <summary>How many leading results to skip. Default 0.</summary>
    public int Skip { get; set; }

    /// <summary>
    /// Terms for the hybrid lane's keyword half. Null or empty degrades to pure vector search
    /// rather than emitting a malformed FTS <c>MATCH</c>.
    /// </summary>
    public IReadOnlyCollection<string>? Keywords { get; set; }

    /// <summary>Applied by the retriever with the correct polarity for the lane it took. Null means no threshold.</summary>
    public double? ScoreThreshold { get; set; }
}
